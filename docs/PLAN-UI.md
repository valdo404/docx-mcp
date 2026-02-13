# Plan : UI de consultation des sessions docx-mcp

## Contexte

Le serveur MCP `docx-mcp` gere des sessions de documents DOCX avec un systeme de persistence base sur :
- **Baseline snapshots** (`.docx`) : etat initial du document
- **Write-Ahead Log** (`.wal`) : journal de toutes les mutations (patches RFC 6902 adaptes OOXML)
- **Checkpoints** (`.ckpt.N.docx`) : snapshots intermediaires tous les N patches
- **Index** (`index.json`) : metadonnees de toutes les sessions

L'objectif est de creer une UI de consultation read-only, **legere et compilable en NativeAOT**, qui permet de naviguer dans les documents et de visualiser l'evolution de chaque session au fil des patches, avec un rendu fidele du DOCX natif dans le navigateur.

---

## 1. Contrainte : NativeAOT, zero reflexion, binaire leger

Le binaire `docx-ui` doit etre aussi compact que `docx-mcp` (~28 MB NativeAOT). Cela exclut :
- **Blazor Server** (depend de la reflexion pour le rendu des composants)
- **Blazor WebAssembly** (meme probleme + gros payload WASM)
- **MVC / Razor Pages** (reflection pour les vues)

La solution : **Kestrel Minimal API (NativeAOT) + SPA statique en pur JS**.

| Couche | Technologie | NativeAOT | Reflexion |
|--------|-------------|-----------|-----------|
| Serveur HTTP | ASP.NET Core Minimal API (`CreateSlimBuilder`) | Oui | Non |
| Serialisation JSON | `System.Text.Json` source-generated | Oui | Non |
| Frontend shell | Fluent UI Web Components (`@fluentui/web-components`) | N/A (JS pur) | N/A |
| Rendu DOCX | `docx-preview` (JS, ~280 KB) | N/A (JS pur) | N/A |
| Streaming temps reel | SSE (`text/event-stream`) | Oui | Non |

**Resultat** : le back-end .NET est un serveur de fichiers statiques + 5 endpoints REST/SSE. Toute la logique UI est en JavaScript vanilla + Web Components Microsoft.

---

## 2. Trois piliers technologiques

### 2.1 Kestrel Minimal API (back-end NativeAOT)

ASP.NET Core Minimal API est entierement compatible NativeAOT depuis .NET 8 via `WebApplication.CreateSlimBuilder()`. Pas de controllers, pas de reflexion, pas de MVC.

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

// Source-generated JSON
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(UiJsonContext.Default));

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SessionBrowserService>();

var app = builder.Build();
app.UseStaticFiles();  // sert wwwroot/ (le SPA)

// 5 endpoints REST + SSE (voir section 4)
app.MapGet("/api/sessions", ...);
app.MapGet("/api/sessions/{id}", ...);
app.MapGet("/api/sessions/{id}/docx", ...);
app.MapGet("/api/sessions/{id}/history", ...);
app.MapGet("/api/events", ...);  // SSE

app.Run();
```

### 2.2 Fluent UI Web Components (frontend Microsoft, pur JS)

[`@fluentui/web-components`](https://github.com/microsoft/fluentui/tree/master/packages/web-components) sont des **Web Components standards** (Custom Elements + Shadow DOM) qui implementent le Fluent Design System de Microsoft. Ils fonctionnent avec du JavaScript vanilla, sans React, Angular, ni framework.

**Chargement** : un seul fichier JS (~150 KB gzip) via CDN ou vendorise :

```html
<script type="module" src="/lib/fluent-web-components.min.js"></script>
```

**Composants utilises** :

| Composant | Usage |
|-----------|-------|
| `<fluent-data-grid>` | Liste des sessions, historique des patches |
| `<fluent-tree-view>` / `<fluent-tree-item>` | Arborescence du document |
| `<fluent-toolbar>` | Barre d'outils (export, navigation) |
| `<fluent-button>` | Boutons d'action |
| `<fluent-slider>` | Curseur de scrubbing dans la timeline |
| `<fluent-tabs>` / `<fluent-tab>` / `<fluent-tab-panel>` | Onglets Document / Diff |
| `<fluent-dialog>` | Dialogues d'export |
| `<fluent-badge>` | Marqueurs (checkpoint, position courante, SSE status) |
| `<fluent-switch>` | Toggle dark/light theme |
| `<fluent-progress-ring>` | Loading spinner pendant le rebuild |
| `<fluent-divider>` | Separateurs visuels |
| `<fluent-card>` | Carte pour chaque session dans la liste |

**Theming** (dark/light mode natif Fluent) :

```html
<fluent-design-system-provider
    base-layer-luminance="0.15"  <!-- dark mode -->
    accent-base-color="#0078d4"> <!-- Office blue -->
  <!-- tout le contenu -->
</fluent-design-system-provider>
```

### 2.3 docx-preview.js (rendu DOCX natif dans le navigateur)

Au lieu de convertir le DOCX en HTML cote serveur (perte de fidelite), on envoie les **bytes DOCX bruts** au navigateur et on les rend via [`docx-preview`](https://www.npmjs.com/package/docx-preview) (librairie JS open-source, ~280 KB).

**Pourquoi** :
- Rendu fidele de la mise en page Word (marges, headers/footers, page breaks, styles)
- Pas de conversion HTML lossy cote serveur
- Rendu client-side pur : le serveur ne fait que servir les bytes
- Supporte les images (base64), les tableaux, les styles, les listes numerotees

**Utilisation** :

```javascript
// Fetch les bytes DOCX depuis l'API, puis rendre
const response = await fetch(`/api/sessions/${sessionId}/docx?position=${pos}`);
const blob = await response.blob();

await docx.renderAsync(blob, container, styleContainer, {
    className: "docx-preview",
    inWrapper: true,
    ignoreWidth: false,
    ignoreHeight: false,
    ignoreFonts: false,
    breakPages: true,
    useBase64URL: false,  // pas de SignalR, on peut utiliser blob URL directement
    experimental: true,
    trimXmlDeclaration: true
});
```

**Note** : contrairement a Blazor Server, ici les bytes transitent via HTTP classique (pas SignalR), donc `useBase64URL: false` est possible, ce qui donne de meilleures performances pour les images.

### 2.4 SSE (Server-Sent Events) pour le streaming temps reel

```
┌──────────────┐     file I/O      ┌──────────────────┐
│  MCP Server  │ ──── WAL/Index ──→│  Sessions Dir     │
│  (docx-mcp)  │                   │  ~/.docx-mcp/     │
└──────────────┘                   │  sessions/        │
                                   └────────┬─────────┘
                                            │ FileSystemWatcher
                                            │ + polling fallback
┌──────────────┐     SSE stream    ┌────────▼─────────┐
│  Browser     │ ←── text/event ───│  UI Server        │
│  (Fluent UI) │     -stream       │  (docx-ui)        │
└──────────────┘                   └──────────────────┘
```

Le serveur UI surveille le repertoire sessions (`FileSystemWatcher` + polling fallback 2s pour Docker/NFS) et emet des evenements SSE :

```
event: index.changed
data: {"type":"index.changed","timestamp":"2026-02-02T14:23:00Z"}

event: session.patched
data: {"type":"session.patched","sessionId":"abc","position":16,"timestamp":"..."}
```

Cote navigateur, l'API standard `EventSource` gere la connexion, la reconnexion automatique, et le parsing :

```javascript
const events = new EventSource("/api/events");
events.addEventListener("index.changed", (e) => {
    refreshSessionList();
});
events.addEventListener("session.patched", (e) => {
    const data = JSON.parse(e.data);
    if (data.sessionId === currentSessionId) {
        updateTimelineMax(data.position);
    }
});
```

**Avantages** :
- Latence quasi-nulle (pas de polling)
- Reconnexion automatique integree (spec HTML5)
- Fonctionne a travers les proxys HTTP
- Zero overhead quand rien ne change

---

## 3. Architecture du projet

### Nouveau projet : `DocxMcp.Ui`

```
src/DocxMcp.Ui/
├── DocxMcp.Ui.csproj
├── Program.cs                              # CreateSlimBuilder + endpoints + static files
├── UiJsonContext.cs                        # Source-generated JSON serializer
├── Services/
│   ├── SessionBrowserService.cs            # Lecture read-only + reconstruction + LRU cache
│   └── EventBroadcaster.cs                 # FileSystemWatcher → Channel<T> → SSE
├── Models/
│   ├── SessionEvent.cs                     # Evenements SSE
│   ├── SessionListItem.cs                  # DTO pour /api/sessions
│   ├── SessionDetailDto.cs                 # DTO pour /api/sessions/{id}
│   └── HistoryEntryDto.cs                  # DTO pour /api/sessions/{id}/history
└── wwwroot/                                # SPA statique (aucun build step requis)
    ├── index.html                          # Point d'entree HTML
    ├── css/
    │   ├── app.css                         # Layout, theming, docx container, diff styles
    │   └── diff.css                        # Surlignage avant/apres
    ├── js/
    │   ├── app.js                          # Router SPA + composants + SSE client
    │   ├── docxRenderer.js                 # Wrapper docx-preview renderAsync
    │   ├── sseClient.js                    # EventSource manager avec reconnexion
    │   ├── diffView.js                     # Logique diff side-by-side
    │   └── documentTree.js                 # Generation du fluent-tree-view
    ├── lib/                                # Vendorise (pas de npm/node requis)
    │   ├── docx-preview.min.js             # ~280 KB
    │   └── fluent-web-components.min.js    # ~150 KB gzip
    └── favicon.ico
```

### `.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>DocxMcp.Ui</RootNamespace>
    <AssemblyName>docx-ui</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>false</InvariantGlobalization>
    <OptimizationPreference>Size</OptimizationPreference>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\DocxMcp\DocxMcp.csproj" />
  </ItemGroup>
</Project>
```

**Zero dependance NuGet supplementaire** : seul le SDK Web + la reference au projet principal suffisent. Pas de Fluent UI Blazor, pas de packages JS build-time.

### Ajout a la solution + Dockerfile

Le binaire `docx-ui` peut etre produit dans la meme image Docker multi-binaires existante :

```dockerfile
# Stage: build docx-ui (NativeAOT)
RUN dotnet publish src/DocxMcp.Ui -c Release -o /out/ui
```

---

## 4. Endpoints HTTP (API REST + SSE)

Tous les endpoints utilisent la serialisation JSON source-generated (NativeAOT-safe).

### `GET /api/sessions`

Liste toutes les sessions depuis `index.json`.

```csharp
app.MapGet("/api/sessions", (SessionBrowserService svc) =>
    Results.Ok(svc.ListSessions()));

// Retourne : SessionListItem[]
// { id, sourcePath, createdAt, lastModifiedAt, walCount, cursorPosition }
```

### `GET /api/sessions/{id}`

Detail d'une session (metadonnees + info checkpoints).

```csharp
app.MapGet("/api/sessions/{id}", (string id, SessionBrowserService svc) =>
{
    var detail = svc.GetSessionDetail(id);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

// Retourne : SessionDetailDto
// { id, sourcePath, createdAt, lastModifiedAt, walCount, cursorPosition,
//   checkpointPositions: int[] }
```

### `GET /api/sessions/{id}/docx?position={N}`

Retourne les bytes DOCX reconstruits a la position N (pour `docx-preview.js`).

```csharp
app.MapGet("/api/sessions/{id}/docx", (string id, int? position, SessionBrowserService svc) =>
{
    var pos = position ?? svc.GetCurrentPosition(id);
    var bytes = svc.GetDocxBytesAtPosition(id, pos);
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        $"{id}-pos{pos}.docx");
});
```

### `GET /api/sessions/{id}/history?offset={N}&limit={N}`

Historique pagine des WAL entries.

```csharp
app.MapGet("/api/sessions/{id}/history",
    (string id, int? offset, int? limit, SessionBrowserService svc) =>
    Results.Ok(svc.GetHistory(id, offset ?? 0, limit ?? 50)));

// Retourne : HistoryEntryDto[]
// { position, timestamp, description, isCheckpoint }
```

### `GET /api/events` (SSE)

Stream temps reel.

```csharp
app.MapGet("/api/events", async (HttpContext ctx, EventBroadcaster broadcaster) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var channel = Channel.CreateUnbounded<SessionEvent>();
    broadcaster.Subscribe(channel.Writer);
    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            var json = JsonSerializer.Serialize(evt, UiJsonContext.Default.SessionEvent);
            await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    finally { broadcaster.Unsubscribe(channel.Writer); }
});
```

### Source-generated JSON context

```csharp
[JsonSerializable(typeof(SessionListItem[]))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(HistoryEntryDto[]))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiJsonContext : JsonSerializerContext { }
```

---

## 5. Services back-end (.NET, NativeAOT-safe)

### 5.1 `SessionBrowserService` : lecture read-only + LRU cache

```csharp
public sealed class SessionBrowserService
{
    private readonly SessionStore _store;
    private readonly ILogger<SessionBrowserService> _logger;

    // Cache LRU : (sessionId, position) → byte[]
    // Evite de reconstruire le document a chaque deplacement du slider
    private readonly LruCache<(string, int), byte[]> _docxCache = new(capacity: 20);

    // --- Lecture ---

    public SessionListItem[] ListSessions()
    {
        var index = _store.LoadIndex();
        return index.Sessions.Select(e => new SessionListItem { ... }).ToArray();
    }

    public SessionDetailDto? GetSessionDetail(string sessionId) { ... }

    public int GetCurrentPosition(string sessionId)
    {
        var index = _store.LoadIndex();
        var entry = index.Sessions.Find(e => e.Id == sessionId);
        return entry?.CursorPosition ?? 0;
    }

    public HistoryEntryDto[] GetHistory(string id, int offset, int limit)
    {
        var entries = _store.ReadWalEntries(id);
        var index = _store.LoadIndex();
        var session = index.Sessions.Find(e => e.Id == id);
        var checkpoints = session?.CheckpointPositions ?? new List<int>();

        return entries
            .Select((e, i) => new HistoryEntryDto
            {
                Position = i + 1,
                Timestamp = e.Timestamp,
                Description = e.Description ?? "(no description)",
                IsCheckpoint = checkpoints.Contains(i + 1)
            })
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    // --- Reconstruction DOCX a une position ---

    public byte[] GetDocxBytesAtPosition(string sessionId, int position)
    {
        var key = (sessionId, position);
        if (_docxCache.TryGet(key, out var cached))
            return cached;

        var bytes = RebuildAtPosition(sessionId, position);
        _docxCache.Set(key, bytes);
        return bytes;
    }

    private byte[] RebuildAtPosition(string sessionId, int position)
    {
        // 1. Charger le checkpoint le plus proche
        var index = _store.LoadIndex();
        var entry = index.Sessions.Find(e => e.Id == sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        var checkpoints = entry.CheckpointPositions ?? new List<int>();
        var (ckptPos, ckptBytes) = _store.LoadNearestCheckpoint(sessionId, position, checkpoints);

        // 2. Creer une session temporaire en memoire
        using var session = DocxSession.FromBytes(ckptBytes, sessionId, entry.SourcePath);

        // 3. Rejouer les patches du WAL
        if (position > ckptPos)
        {
            var patches = _store.ReadWalRange(sessionId, ckptPos, position);
            foreach (var patchJson in patches)
            {
                try { SessionManager.ReplayPatch(session, patchJson); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to replay patch during rebuild.");
                    break;
                }
            }
        }

        // 4. Extraire les bytes
        session.Document.Save();
        return session.Stream.ToArray();
    }
}
```

### 5.2 `EventBroadcaster` : FileSystemWatcher → SSE

```csharp
public sealed class EventBroadcaster : IDisposable
{
    private readonly string _sessionsDir;
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private readonly List<ChannelWriter<SessionEvent>> _subscribers = new();
    private readonly Lock _lock = new();
    private string _lastIndexHash = "";

    public void Start()
    {
        // FileSystemWatcher sur le repertoire sessions
        if (Directory.Exists(_sessionsDir))
        {
            _watcher = new FileSystemWatcher(_sessionsDir, "index.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) => CheckForChanges();
            _watcher.EnableRaisingEvents = true;
        }

        // Polling fallback 2s (Docker, NFS, ou watcher non fiable)
        _pollTimer = new Timer(_ => CheckForChanges(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void CheckForChanges()
    {
        try
        {
            var indexPath = Path.Combine(_sessionsDir, "index.json");
            if (!File.Exists(indexPath)) return;

            var hash = ComputeFileHash(indexPath);
            if (hash == _lastIndexHash) return;
            _lastIndexHash = hash;

            Emit(new SessionEvent
            {
                Type = "index.changed",
                Timestamp = DateTime.UtcNow
            });
        }
        catch { /* best effort */ }
    }

    public void Subscribe(ChannelWriter<SessionEvent> writer)
    {
        lock (_lock) _subscribers.Add(writer);
    }

    public void Unsubscribe(ChannelWriter<SessionEvent> writer)
    {
        lock (_lock) _subscribers.Remove(writer);
    }

    private void Emit(SessionEvent evt)
    {
        lock (_lock)
        {
            foreach (var writer in _subscribers)
                writer.TryWrite(evt);
        }
    }
}
```

### 5.3 `LruCache<TKey, TValue>` (inline, pas de package)

```csharp
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _list;

    public LruCache(int capacity) { ... }
    public bool TryGet(TKey key, out TValue value) { ... }
    public void Set(TKey key, TValue value) { ... }
}
```

---

## 6. Frontend SPA (pur JavaScript + Fluent UI Web Components)

### 6.1 Structure

Aucun build step (ni npm, ni webpack, ni vite). Les fichiers JS sont ecrits en modules ES natifs (`import`/`export`). Les librairies sont vendorisees dans `wwwroot/lib/`.

### 6.2 `index.html` : point d'entree

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>docx-mcp Session Browser</title>
    <link rel="stylesheet" href="/css/app.css" />
    <script type="module" src="/lib/fluent-web-components.min.js"></script>
    <script src="/lib/docx-preview.min.js"></script>
</head>
<body>
    <fluent-design-system-provider id="design-provider">
        <div id="app-shell">
            <header id="app-header">
                <fluent-toolbar>
                    <h1 slot="start">docx-mcp</h1>
                    <fluent-switch slot="end" id="theme-toggle">Dark mode</fluent-switch>
                    <fluent-badge slot="end" id="sse-status">SSE</fluent-badge>
                </fluent-toolbar>
            </header>
            <main id="app-content">
                <!-- Contenu dynamique injecte par le router JS -->
            </main>
        </div>
    </fluent-design-system-provider>

    <script type="module" src="/js/app.js"></script>
</body>
</html>
```

### 6.3 `app.js` : router SPA minimaliste

Pas de framework : un router hash-based (`#/sessions`, `#/session/{id}`, `#/diff/{id}/{position}`) qui monte/demonte les vues.

```javascript
// js/app.js
import { renderSessionList } from './views/sessionList.js';
import { renderSessionDetail } from './views/sessionDetail.js';
import { renderDiffView } from './views/diffView.js';
import { connectSSE } from './sseClient.js';

const content = document.getElementById('app-content');
const sse = connectSSE('/api/events');

function route() {
    const hash = location.hash || '#/sessions';
    const [_, path, ...params] = hash.split('/');

    content.innerHTML = '';  // unmount previous view

    switch (path) {
        case 'sessions':
            renderSessionList(content, sse);
            break;
        case 'session':
            renderSessionDetail(content, params[0], sse);
            break;
        case 'diff':
            renderDiffView(content, params[0], parseInt(params[1]));
            break;
        default:
            renderSessionList(content, sse);
    }
}

window.addEventListener('hashchange', route);
route();

// Theme toggle
document.getElementById('theme-toggle').addEventListener('change', (e) => {
    const provider = document.getElementById('design-provider');
    provider.setAttribute('base-layer-luminance', e.target.checked ? '0.15' : '1');
});
```

### 6.4 `sseClient.js` : gestion SSE avec reconnexion

```javascript
// js/sseClient.js
export function connectSSE(url) {
    const listeners = {};
    let source = new EventSource(url);
    const statusBadge = document.getElementById('sse-status');

    function updateStatus(connected) {
        statusBadge.setAttribute('color', connected ? 'success' : 'danger');
        statusBadge.textContent = connected ? 'Live' : 'Disconnected';
    }

    source.onopen = () => updateStatus(true);
    source.onerror = () => updateStatus(false);
    // EventSource gere la reconnexion automatique

    return {
        on(eventType, callback) {
            if (!listeners[eventType]) {
                listeners[eventType] = [];
                source.addEventListener(eventType, (e) => {
                    const data = JSON.parse(e.data);
                    listeners[eventType].forEach(cb => cb(data));
                });
            }
            listeners[eventType].push(callback);
        },
        off(eventType, callback) {
            if (listeners[eventType]) {
                listeners[eventType] = listeners[eventType].filter(cb => cb !== callback);
            }
        }
    };
}
```

### 6.5 `docxRenderer.js` : wrapper docx-preview

```javascript
// js/docxRenderer.js

// Debounce pour le scrubbing rapide du slider
let renderTimeout = null;

export async function renderDocxAtPosition(container, sessionId, position, debounceMs = 200) {
    clearTimeout(renderTimeout);

    return new Promise((resolve) => {
        renderTimeout = setTimeout(async () => {
            // Afficher spinner
            container.innerHTML = '<fluent-progress-ring></fluent-progress-ring>';

            try {
                const resp = await fetch(`/api/sessions/${sessionId}/docx?position=${position}`);
                if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
                const blob = await resp.blob();

                container.innerHTML = '';
                const styleContainer = document.createElement('style');
                container.appendChild(styleContainer);

                await docx.renderAsync(blob, container, styleContainer, {
                    className: "docx-preview",
                    inWrapper: true,
                    ignoreWidth: false,
                    ignoreHeight: false,
                    ignoreFonts: false,
                    breakPages: true,
                    useBase64URL: false,
                    experimental: true,
                    trimXmlDeclaration: true
                });
            } catch (err) {
                container.innerHTML = `<p class="error">Failed to render: ${err.message}</p>`;
            }

            resolve();
        }, debounceMs);
    });
}

// Rendu immediat (sans debounce) pour le diff view
export async function renderDocxImmediate(container, sessionId, position) {
    return renderDocxAtPosition(container, sessionId, position, 0);
}
```

---

## 7. Pages de l'UI

### 7.1 Page d'accueil : `#/sessions`

```
┌──────────────────────────────────────────────────────────────────┐
│  docx-mcp                                   [Dark mode] ● Live  │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─ Active Sessions ──────────────────────────────────────────┐  │
│  │                                                             │  │
│  │  <fluent-data-grid>                                         │  │
│  │  ID          │ Source            │ Modified    │ WAL  │ Pos  │  │
│  │──────────────│───────────────────│─────────────│──────│──────│  │
│  │  a1b2c3d4e5f │ /docs/spec.docx  │ 2 min ago   │  15  │  12  │  │
│  │  x9y8z7w6v5u │ /tmp/draft.docx  │ 1h ago      │   3  │   3  │  │
│  │  m4n5o6p7q8r │ (new document)   │ yesterday   │  42  │  42  │  │
│  │                                                             │  │
│  └─────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  Sessions dir: ~/.docx-mcp/sessions/                             │
└──────────────────────────────────────────────────────────────────┘
```

- `<fluent-data-grid>` genere dynamiquement depuis `GET /api/sessions`
- SSE `index.changed` → re-fetch + re-render du grid
- Clic sur une ligne → `location.hash = '#/session/{id}'`

### 7.2 Page session : `#/session/{id}`

Layout en 3 zones (CSS Grid + panneau redimensionnable) :

```
┌──────────────────────────────────────────────────────────────────┐
│  [← Sessions]  Session a1b2c3d4e5f — spec.docx       [Export ▼] │
├───────────────┬──────────────────────────────────────────────────┤
│ Document Tree │  DOCX Preview (docx-preview.js)                  │
│               │                                                  │
│ <fluent-tree- │  ┌────────────────────────────────────────────┐  │
│  view>        │  │                                            │  │
│               │  │   [Rendu natif DOCX :                      │  │
│ ▼ Body        │  │    marges, polices, tableaux,              │  │
│   §1 Intro    │  │    headers/footers, images,                │  │
│   §2 Specs    │  │    page breaks, styles, listes]            │  │
│   ▼ Table[0]  │  │                                            │  │
│     Row 0     │  │                                            │  │
│     Row 1     │  └────────────────────────────────────────────┘  │
│   §3 Concl.   │                                                  │
├───────────────┴──────────────────────────────────────────────────┤
│  Timeline                                                        │
│                                                                  │
│  ◆━━━○━━━○━━━●━━━○━━━○━━━◆━━━○━━━○━━━●━━━○━━━○━━━◆━━━○━━━▶   │
│  0   1   2   3   4   5   6   7   8   9  10  11  12  13  14    │
│  ↑                   ↑                   ↑              ↑      │
│  Baseline          Ckpt              Ckpt           Cursor     │
│                                                                  │
│  <fluent-slider min=0 max=15 value=12 step=1 />                │
│  Position: 12/15                      [◀ Prev] [Next ▶]        │
│                                                                  │
│  <fluent-data-grid>  (WAL entries)                              │
│  │ # │ Time     │ Description                    │ Actions    │  │
│  │ 12│ 14:25:30 │ replace /body/paragraph[0]     │ [Diff]     │  │
│  │ 11│ 14:24:02 │ remove /body/table[0]/row[2]   │ [Diff]     │  │
│  │ 10│ 14:23:15 │ add /body/paragraph[5]         │ [Diff]     │  │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

**Comportement** :
- Le `<fluent-slider>` emet `@change` → `renderDocxAtPosition()` avec debounce 200ms
- Le document tree se genere cote client en parsant le HTML produit par `docx-preview.js`
  (pas besoin d'un endpoint serveur : on inspecte le DOM genere)
- Clic tree → `element.scrollIntoView()` dans le conteneur preview
- SSE `session.patched` → slider max s'etend, badge "new" sur le dernier patch
- Bouton [Diff] → `location.hash = '#/diff/{id}/{position}'`
- Cache navigateur : les reponses `/api/sessions/{id}/docx?position=N` sont immutables (meme position = memes bytes), on peut les mettre en cache HTTP (`Cache-Control: immutable`)

### 7.3 Page diff : `#/diff/{id}/{position}`

```
┌──────────────────────────────────────────────────────────────────┐
│  [← Session]  Patch #5 : Position 4 → 5               [Export] │
│               "replace /body/paragraph[0]/run[0]"               │
├──────────────────────────┬───────────────────────────────────────┤
│                          │                                      │
│  Before (Pos 4)          │  After (Pos 5)                       │
│                          │                                      │
│  ┌────────────────────┐  │  ┌────────────────────┐             │
│  │ [docx-preview       │  │  │ [docx-preview       │             │
│  │  rendu natif DOCX]  │  │  │  rendu natif DOCX]  │             │
│  │                    │  │  │                    │             │
│  │  page 1            │  │  │  page 1            │             │
│  └────────────────────┘  │  └────────────────────┘             │
│                          │                                      │
├──────────────────────────┴───────────────────────────────────────┤
│  Patch Operations                                               │
│  <pre> formatted JSON des operations du patch </pre>            │
│                                                                  │
│  [◀ Prev Patch]  Patch 5 of 15  [Next Patch ▶]                 │
│  [Download Before .docx]  [Download After .docx]                │
└──────────────────────────────────────────────────────────────────┘
```

- Deux appels paralleles : `GET .../docx?position=4` et `GET .../docx?position=5`
- Deux conteneurs `docx-preview.js` cote a cote (CSS `display: grid; grid-template-columns: 1fr 1fr`)
- Le JSON du patch est lu depuis `GET /api/sessions/{id}/history` (entree a la position concernee)
- Boutons de telechargement : lien direct vers `/api/sessions/{id}/docx?position=N` (le navigateur telecharge le .docx)
- Navigation Prev/Next : change le hash → re-render

### 7.4 Document Tree (generation cote client)

Plutot qu'un endpoint serveur, le tree est genere **en inspectant le DOM produit par docx-preview.js** :

```javascript
// js/documentTree.js
export function buildTreeFromPreview(previewContainer) {
    const treeView = document.createElement('fluent-tree-view');
    const wrapper = previewContainer.querySelector('.docx-wrapper');
    if (!wrapper) return treeView;

    let paragraphIndex = 0;
    let tableIndex = 0;

    for (const child of wrapper.children) {
        const item = document.createElement('fluent-tree-item');

        if (child.tagName === 'P' || child.tagName.match(/^H[1-6]$/)) {
            const text = child.textContent?.substring(0, 40) || '(empty)';
            const level = child.tagName.match(/^H(\d)$/)?.[1];
            item.textContent = level
                ? `H${level}: ${text}`
                : `P[${paragraphIndex}]: ${text}`;
            item.dataset.target = `p-${paragraphIndex}`;
            child.id = `p-${paragraphIndex}`;
            paragraphIndex++;
        }
        else if (child.tagName === 'TABLE') {
            const rows = child.querySelectorAll('tr').length;
            item.textContent = `Table[${tableIndex}] (${rows} rows)`;
            item.dataset.target = `t-${tableIndex}`;
            child.id = `t-${tableIndex}`;
            tableIndex++;
        }
        else continue;

        item.addEventListener('click', () => {
            const target = previewContainer.querySelector(`#${item.dataset.target}`);
            target?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        });

        treeView.appendChild(item);
    }

    return treeView;
}
```

---

## 8. `Program.cs` complet (NativeAOT)

```csharp
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocxMcp.Persistence;
using DocxMcp.Ui.Models;
using DocxMcp.Ui.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Source-generated JSON (NativeAOT-safe)
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(UiJsonContext.Default));

// Sessions read-only
var sessionsDir = Environment.GetEnvironmentVariable("DOCX_MCP_SESSIONS_DIR")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".docx-mcp", "sessions");

builder.Services.AddSingleton(sp =>
    new SessionStore(sp.GetRequiredService<ILogger<SessionStore>>(), sessionsDir));
builder.Services.AddSingleton<SessionBrowserService>();
builder.Services.AddSingleton<EventBroadcaster>();

var port = builder.Configuration.GetValue("Port", 5200);
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

// Servir le SPA statique depuis wwwroot/
app.UseDefaultFiles();   // index.html comme fallback
app.UseStaticFiles();

// --- API Endpoints ---

app.MapGet("/api/sessions", (SessionBrowserService svc) =>
    Results.Ok(svc.ListSessions()));

app.MapGet("/api/sessions/{id}", (string id, SessionBrowserService svc) =>
{
    var detail = svc.GetSessionDetail(id);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/sessions/{id}/docx", (string id, int? position, SessionBrowserService svc) =>
{
    var pos = position ?? svc.GetCurrentPosition(id);
    var bytes = svc.GetDocxBytesAtPosition(id, pos);
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        $"{id}-pos{pos}.docx");
});

app.MapGet("/api/sessions/{id}/history",
    (string id, int? offset, int? limit, SessionBrowserService svc) =>
    Results.Ok(svc.GetHistory(id, offset ?? 0, limit ?? 50)));

app.MapGet("/api/events", async (HttpContext ctx, EventBroadcaster broadcaster) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var channel = Channel.CreateUnbounded<SessionEvent>();
    broadcaster.Subscribe(channel.Writer);
    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            var json = JsonSerializer.Serialize(evt, UiJsonContext.Default.SessionEvent);
            await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    finally { broadcaster.Unsubscribe(channel.Writer); }
});

// Demarrer la surveillance des sessions
app.Services.GetRequiredService<EventBroadcaster>().Start();

// Auto-ouvrir le navigateur
_ = Task.Run(async () =>
{
    await Task.Delay(800);
    try { Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true }); }
    catch { /* headless */ }
});

app.Run();
```

---

## 9. Integration CLI : `docx-cli server`

Le CLI NativeAOT lance le binaire `docx-ui` comme sous-processus :

```csharp
"server" => CmdStartServer(args),

string CmdStartServer(string[] a)
{
    var port = ParseInt(OptNamed(a, "--port"), 5200);

    // Chercher docx-ui a cote de docx-cli, puis dans PATH
    var myDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    var uiExe = Path.Combine(myDir, "docx-ui");
    if (!File.Exists(uiExe))
    {
        // Essayer avec extension .exe (Windows)
        uiExe = Path.Combine(myDir, "docx-ui.exe");
        if (!File.Exists(uiExe))
            return "Error: docx-ui binary not found next to docx-cli.";
    }

    var psi = new ProcessStartInfo
    {
        FileName = uiExe,
        Arguments = $"--Port {port}",
        UseShellExecute = false,
    };
    var dir = Environment.GetEnvironmentVariable("DOCX_MCP_SESSIONS_DIR");
    if (dir is not null) psi.Environment["DOCX_MCP_SESSIONS_DIR"] = dir;

    using var process = Process.Start(psi);
    Console.WriteLine($"UI server started: http://localhost:{port} (PID {process?.Id})");
    Console.WriteLine("Press Ctrl+C to stop.");
    process?.WaitForExit();
    return "";
}
```

---

## 10. Flux de donnees complet

### Flux 1 : scrubbing du slider

```
  User deplace le slider de position 8 → position 12

  Browser (vanilla JS + docx-preview.js)
    │
    ├── 1. <fluent-slider> @change → debounce 200ms
    │
    ├── 2. fetch(`/api/sessions/abc/docx?position=12`)
    │       │
    │       └── Serveur (Kestrel NativeAOT)
    │           ├── Cache LRU hit? → retourne byte[] directement
    │           └── Cache miss:
    │               ├── SessionStore.LoadNearestCheckpoint("abc", 12)
    │               │   → checkpoint pos 10, bytes du .ckpt.10.docx
    │               ├── SessionStore.ReadWalRange("abc", 10, 12)
    │               │   → 2 patches JSON
    │               ├── DocxSession.FromBytes(ckptBytes) temporaire
    │               ├── ReplayPatch(session, patch10)
    │               ├── ReplayPatch(session, patch11)
    │               ├── session.Stream.ToArray() → byte[]
    │               └── session.Dispose()
    │
    ├── 3. const blob = await response.blob()
    │
    └── 4. docx.renderAsync(blob, container, styleContainer, options)
            → Rendu DOCX natif dans le DOM du navigateur
```

### Flux 2 : notification temps reel d'un nouveau patch

```
  MCP Server ecrit un patch (via PatchTool)

  MCP Server (docx-mcp)
    ├── PatchTool applique le patch
    ├── SessionStore.AppendWal("abc", patchJson)
    └── SessionStore.SaveIndex(updatedIndex)  ← index.json modifie

  UI Server (docx-ui, NativeAOT)
    ├── FileSystemWatcher detecte le changement d'index.json
    │   (ou poll 2s detecte le hash different)
    ├── EventBroadcaster.Emit({ type: "index.changed" })
    └── SSE endpoint ecrit dans le stream :
        event: index.changed
        data: {"type":"index.changed","timestamp":"..."}

  Browser
    ├── EventSource recoit "index.changed"
    ├── Re-fetch GET /api/sessions/{id} → nouveau walCount
    ├── Slider max passe de 15 → 16
    └── Badge "new" apparait sur le dernier patch
```

---

## 11. Plan d'implementation par etapes

### Etape 1 : Scaffolding du projet NativeAOT
- [ ] Creer `src/DocxMcp.Ui/DocxMcp.Ui.csproj` (SDK Web, PublishAot=true)
- [ ] Ajouter a `DocxMcp.sln`
- [ ] `Program.cs` : `CreateSlimBuilder` + `UseStaticFiles` + page vide
- [ ] `UiJsonContext.cs` : source-generated JSON
- [ ] `wwwroot/index.html` minimal avec Fluent UI Web Components
- [ ] Vendoriser `docx-preview.min.js` et `fluent-web-components.min.js`
- [ ] Verifier : `dotnet run` demarre, page s'affiche, `dotnet publish -c Release` NativeAOT OK

### Etape 2 : SessionBrowserService + endpoints REST
- [ ] `SessionBrowserService` : ListSessions, GetSessionDetail, GetDocxBytesAtPosition, GetHistory
- [ ] `LruCache` pour les documents reconstruits
- [ ] 4 endpoints REST (`/api/sessions`, `/{id}`, `/{id}/docx`, `/{id}/history`)
- [ ] Tests : curl sur chaque endpoint, verifier les reponses JSON

### Etape 3 : EventBroadcaster + SSE
- [ ] `EventBroadcaster` : FileSystemWatcher + polling fallback
- [ ] Endpoint SSE `/api/events`
- [ ] `wwwroot/js/sseClient.js`
- [ ] Test : ouvrir 2 onglets, modifier index.json, verifier que les 2 recoivent l'evenement

### Etape 4 : Page Session List (JS)
- [ ] `wwwroot/js/views/sessionList.js`
- [ ] `<fluent-data-grid>` avec colonnes triables
- [ ] SSE `index.changed` → refresh auto
- [ ] Badge SSE connecte/deconnecte
- [ ] Navigation au clic

### Etape 5 : Document Preview (docx-preview.js)
- [ ] `wwwroot/js/docxRenderer.js` : fetch + renderAsync + debounce
- [ ] CSS pour le conteneur DOCX (scroll, dimensions, overflow)
- [ ] `<fluent-progress-ring>` pendant le chargement

### Etape 6 : Session Detail page (assemblage)
- [ ] `wwwroot/js/views/sessionDetail.js`
- [ ] Layout CSS Grid 3 zones (tree | preview | timeline)
- [ ] Toolbar avec `<fluent-button>` retour + export
- [ ] Integration preview + tree + timeline

### Etape 7 : Document Tree (generation depuis le DOM docx-preview)
- [ ] `wwwroot/js/documentTree.js`
- [ ] `<fluent-tree-view>` genere depuis le HTML rendu par docx-preview
- [ ] Clic → scrollIntoView dans le preview

### Etape 8 : History Timeline + Slider
- [ ] `<fluent-slider>` : position 0..max, step 1
- [ ] Visualisation graphique de la timeline (SVG ou canvas)
- [ ] Marqueurs : baseline, checkpoints, position courante
- [ ] `<fluent-data-grid>` : liste paginee des WAL entries
- [ ] Boutons Prev/Next
- [ ] Slider change → fetch + renderAsync (avec debounce)
- [ ] SSE → extension du slider, notification

### Etape 9 : Diff View
- [ ] `wwwroot/js/views/diffView.js`
- [ ] Deux conteneurs docx-preview cote a cote (CSS Grid 1fr 1fr)
- [ ] Fetch parallele Before + After
- [ ] JSON du patch dans `<pre>` stylise
- [ ] Navigation Prev/Next entre patches
- [ ] Boutons download (lien direct vers l'endpoint `/docx`)

### Etape 10 : Export + CLI + polish
- [ ] Boutons download dans toolbar (lien vers `/api/sessions/{id}/docx?position=N`)
- [ ] Commande `docx-cli server [--port N]`
- [ ] Dark/light theme toggle (`<fluent-switch>` → `base-layer-luminance`)
- [ ] Responsive (media queries)
- [ ] Gestion erreurs (session introuvable, SSE deconnecte, WAL corrompu)
- [ ] Cache HTTP `Cache-Control: public, max-age=31536000, immutable` sur les DOCX immutables

---

## 12. Considerations techniques

### Taille du binaire
- `CreateSlimBuilder` au lieu de `CreateBuilder` reduit les dependances
- `OptimizationPreference=Size` + `PublishAot=true`
- Pas de Blazor, pas de MVC, pas de SignalR : uniquement Kestrel + static files + endpoints
- Taille attendue : ~15-25 MB (comparable aux autres binaires du projet)

### Performance du scrubbing
- Debounce 200ms sur le slider : evite les rebuilds inutiles pendant le glissement
- Cache LRU serveur (20 entrees) : positions recemment visitees servies instantanement
- Cache HTTP navigateur : chaque position est immutable, `Cache-Control: immutable`
- Checkpoints tous les 10 patches : rebuild ne rejoue jamais plus de 10 patches
- Documents typiques : 50-500 KB, transfert quasi-instantane

### docx-preview.js : limites connues
- Pas de macros VBA (non pertinent)
- SmartArt partiellement rendu
- Polices non-standard : fallback systeme
- `breakPages: true` peut etre lent pour les tres gros documents (>100 pages)

### Securite
- Ecoute uniquement sur `localhost` (outil local)
- Pas d'authentification requise
- Read-only par design : aucun endpoint de mutation
- Les bytes DOCX proviennent uniquement du repertoire sessions configure

### Concurrence d'acces
- Le file lock n'est acquis que pendant les lectures d'index (tres bref)
- Les reconstructions utilisent des copies en memoire (sessions temporaires jetables)
- `EventBroadcaster` est thread-safe via `lock` + `Channel<T>`
- Le cache LRU est thread-safe (lock interne)

### Pas de build step JS
- Les librairies JS sont vendorisees (copiees) dans `wwwroot/lib/`
- Pas de `npm install`, pas de `webpack`, pas de `vite`
- Les fichiers JS utilisent des modules ES natifs (`import`/`export`)
- Cela garde le projet simple et sans dependances Node.js
