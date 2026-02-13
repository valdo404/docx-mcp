# Plan d'Intégration SharePoint pour docx-mcp

## Contexte

Le serveur MCP `docx-mcp` permet actuellement de manipuler des documents Word (.docx) localement. Cette extension vise à ajouter la capacité de :
1. **Publier** des documents vers SharePoint/OneDrive de manière **transparente**
2. **Recevoir des notifications** lorsqu'un document est modifié sur SharePoint

**Principe fondamental:** L'intégration SharePoint est **invisible pour le LLM**. Aucun nouvel outil MCP n'est exposé. Tout fonctionne via les outils existants (`document_open`, `document_save`) et la configuration plateforme.

---

## Architecture Transparente

```
┌─────────────────────────────────────────────────────────────────┐
│                        docx-mcp Server                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              DocumentTools (existant - étendu)              │ │
│  │                                                             │ │
│  │  document_open(path)                                        │ │
│  │    ├─ path local    → ouvre directement                     │ │
│  │    └─ URL SharePoint → télécharge puis ouvre (transparent)  │ │
│  │                                                             │ │
│  │  document_save(sessionId, path?)                            │ │
│  │    ├─ session locale      → sauvegarde locale               │ │
│  │    └─ session SharePoint  → upload automatique (transparent)│ │
│  │                                                             │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │         SharePointBackend (INTERNE - non exposé au LLM)     │ │
│  │                                                             │ │
│  │  - Détection automatique des URLs SharePoint/OneDrive       │ │
│  │  - Download transparent via Graph API                       │ │
│  │  - Upload transparent sur save                              │ │
│  │  - Gestion des versions SharePoint                          │ │
│  │  - Cache local pour performance                             │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │         NotificationService (INTERNE - background)          │ │
│  │                                                             │ │
│  │  - FileSystemWatcher sur dossier OneDrive sync (desktop)    │ │
│  │  - Delta polling via Graph API (fallback)                   │ │
│  │  - Webhooks (environnement serveur)                         │ │
│  │  - Émet des événements MCP sampling/notifications           │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘

        ▲                                           │
        │ Liste des fichiers                        │ Notifications
        │ (outil externe)                           ▼
┌───────┴───────────┐                    ┌─────────────────────────┐
│  Autre outil MCP  │                    │   Client MCP / Host     │
│  (hors scope)     │                    │   (reçoit événements)   │
└───────────────────┘                    └─────────────────────────┘
```

---

## Partie 1: Publication Transparente

### Comportement de `document_open`

Le tool existant `document_open` détecte automatiquement le type de chemin:

```csharp
// Pseudocode - logique interne
public async Task<OpenResult> DocumentOpen(string path)
{
    if (SharePointUrlDetector.IsSharePointUrl(path))
    {
        // Télécharge le document en local (cache temporaire)
        var localPath = await _sharePointBackend.DownloadToCache(path);

        // Ouvre la session avec métadonnées SharePoint
        var session = await OpenLocalDocument(localPath);
        session.Metadata.SourceType = SourceType.SharePoint;
        session.Metadata.RemoteUrl = path;
        session.Metadata.RemoteETag = await _sharePointBackend.GetETag(path);

        return session;
    }

    // Comportement local inchangé
    return await OpenLocalDocument(path);
}
```

**URLs supportées:**
- `https://tenant.sharepoint.com/sites/Site/Shared%20Documents/file.docx`
- `https://tenant-my.sharepoint.com/personal/user/Documents/file.docx`
- `https://onedrive.com/...` (OneDrive personnel)

### Comportement de `document_save`

Le tool existant `document_save` détecte automatiquement la source:

```csharp
// Pseudocode - logique interne
public async Task<SaveResult> DocumentSave(string sessionId, string? newPath = null)
{
    var session = GetSession(sessionId);

    if (session.Metadata.SourceType == SourceType.SharePoint && newPath == null)
    {
        // Upload automatique vers SharePoint
        await _sharePointBackend.Upload(
            session.GetContent(),
            session.Metadata.RemoteUrl,
            session.Metadata.RemoteETag  // Pour détection de conflits
        );

        // Met à jour l'ETag
        session.Metadata.RemoteETag = await _sharePointBackend.GetETag(
            session.Metadata.RemoteUrl
        );

        return new SaveResult {
            Location = session.Metadata.RemoteUrl,
            SourceType = SourceType.SharePoint
        };
    }

    // Comportement local inchangé
    return await SaveLocalDocument(session, newPath);
}
```

### Détection des Conflits

Lors du save vers SharePoint, vérification automatique:

```csharp
public async Task Upload(byte[] content, string remoteUrl, string expectedETag)
{
    var currentETag = await GetETag(remoteUrl);

    if (currentETag != expectedETag)
    {
        throw new ConflictException(
            "Le document a été modifié sur SharePoint depuis son ouverture. " +
            "Utilisez document_close puis document_open pour obtenir la dernière version."
        );
    }

    await UploadContent(content, remoteUrl);
}
```

---

## Partie 2: Notifications (Background)

Les notifications fonctionnent **en arrière-plan** sans intervention du LLM. Le service émet des événements que le client MCP peut traiter.

### Option A: FileSystemWatcher (Desktop avec OneDrive sync)

**Configuration:**
```json
{
  "SharePoint": {
    "NotificationMode": "FileSystemWatcher",
    "OneDriveSyncPath": "C:\\Users\\User\\OneDrive - Company"
  }
}
```

**Implémentation:**
```csharp
public class LocalSyncWatcher : IHostedService
{
    private FileSystemWatcher _watcher;
    private readonly IMcpNotificationEmitter _notifier;

    public Task StartAsync(CancellationToken ct)
    {
        _watcher = new FileSystemWatcher(_syncPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            Filter = "*.docx",
            IncludeSubdirectories = true,
            InternalBufferSize = 65536
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileCreated;
        _watcher.Deleted += OnFileDeleted;
        _watcher.EnableRaisingEvents = true;

        return Task.CompletedTask;
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Émet une notification MCP
        await _notifier.EmitNotification(new DocumentChangedNotification
        {
            Path = e.FullPath,
            ChangeType = "modified",
            Timestamp = DateTime.UtcNow
        });
    }
}
```

### Option B: Delta Polling (Fallback universel)

**Configuration:**
```json
{
  "SharePoint": {
    "NotificationMode": "DeltaPolling",
    "PollingIntervalSeconds": 60,
    "WatchedPaths": [
      "https://tenant.sharepoint.com/sites/Site/Documents"
    ]
  }
}
```

**Implémentation:**
```csharp
public class DeltaPollingService : BackgroundService
{
    private readonly Dictionary<string, string?> _deltaLinks = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var watchedPath in _config.WatchedPaths)
            {
                var changes = await PollDelta(watchedPath);

                foreach (var change in changes)
                {
                    await _notifier.EmitNotification(new DocumentChangedNotification
                    {
                        Path = change.WebUrl,
                        ChangeType = MapChangeType(change),
                        Timestamp = change.LastModifiedDateTime
                    });
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.PollingIntervalSeconds), ct);
        }
    }

    private async Task<List<DriveItem>> PollDelta(string path)
    {
        var (siteId, driveId) = ParseSharePointPath(path);

        IDeltaResponse response;
        if (_deltaLinks.TryGetValue(path, out var deltaLink) && deltaLink != null)
        {
            response = await _graph.Sites[siteId].Drives[driveId]
                .Root.Delta.WithUrl(deltaLink).GetAsync();
        }
        else
        {
            response = await _graph.Sites[siteId].Drives[driveId]
                .Root.Delta.GetAsync();
        }

        _deltaLinks[path] = response.OdataDeltaLink;
        return response.Value.Where(IsDocx).ToList();
    }
}
```

### Option C: Webhooks (Environnement Serveur)

**Prérequis:** URL HTTPS publique accessible par Microsoft Graph

**Configuration:**
```json
{
  "SharePoint": {
    "NotificationMode": "Webhook",
    "WebhookEndpoint": "https://myserver.com/webhook/sharepoint",
    "WatchedResources": [
      "sites/{site-id}/drives/{drive-id}/root"
    ]
  }
}
```

**Gestion automatique des subscriptions:**
```csharp
public class WebhookSubscriptionManager : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var resource in _config.WatchedResources)
        {
            await CreateOrRenewSubscription(resource);
        }

        // Renouvellement automatique avant expiration (max 30 jours)
        _renewalTimer = new Timer(
            async _ => await RenewAllSubscriptions(),
            null,
            TimeSpan.FromDays(25),
            TimeSpan.FromDays(25)
        );
    }
}
```

---

## Partie 3: Authentification (Configuration Plateforme)

L'authentification est configurée au niveau plateforme, **jamais exposée au LLM**.

### Configuration

```json
// appsettings.json ou variables d'environnement
{
  "SharePoint": {
    "TenantId": "your-tenant-id",
    "ClientId": "your-app-registration-id",

    // Option 1: Client credentials (service/daemon)
    "ClientSecret": "your-secret",

    // Option 2: Device code flow (desktop interactif)
    "AuthMode": "DeviceCode",

    // Option 3: Chemin OneDrive sync local (pas d'auth Graph nécessaire)
    "OneDriveSyncPath": "C:\\Users\\User\\OneDrive - Company"
  }
}
```

### Modes d'authentification

| Mode | Cas d'usage | Configuration |
|------|-------------|---------------|
| **OneDrive Sync** | Desktop avec sync configuré | `OneDriveSyncPath` uniquement |
| **Device Code** | Desktop sans sync | `TenantId` + `ClientId` + prompt utilisateur |
| **Client Credentials** | Serveur/daemon | `TenantId` + `ClientId` + `ClientSecret` |

### Token Management (interne)

```csharp
public class SharePointAuthProvider
{
    private readonly TokenCache _cache = new();

    public async Task<string> GetAccessToken()
    {
        if (_cache.TryGetValid(out var token))
            return token;

        token = _config.AuthMode switch
        {
            "ClientCredentials" => await AcquireClientCredentials(),
            "DeviceCode" => await AcquireDeviceCode(),
            _ => throw new InvalidOperationException()
        };

        _cache.Set(token);
        return token;
    }
}
```

---

## Partie 4: Structure des Fichiers

```
src/DocxMcp/
├── SharePoint/                          # Nouveau module (INTERNE)
│   ├── SharePointBackend.cs             # Upload/Download via Graph
│   ├── SharePointUrlDetector.cs         # Détection URLs SharePoint/OneDrive
│   ├── SharePointAuthProvider.cs        # Gestion tokens
│   ├── Notifications/
│   │   ├── INotificationService.cs      # Interface commune
│   │   ├── FileSystemWatcherService.cs  # Option desktop
│   │   ├── DeltaPollingService.cs       # Option polling
│   │   └── WebhookService.cs            # Option serveur
│   └── SharePointConfig.cs              # Configuration
├── Tools/
│   └── DocumentTools.cs                 # MODIFIÉ: détection transparente
├── DocxSession.cs                       # MODIFIÉ: métadonnées SharePoint
└── Program.cs                           # MODIFIÉ: enregistrement services
```

---

## Partie 5: Plan d'Implémentation

### Phase 1: Backend SharePoint

1. **SharePointUrlDetector** - Détection des URLs SharePoint/OneDrive
2. **SharePointAuthProvider** - Gestion authentification (device code + client credentials)
3. **SharePointBackend** - Upload/Download via Graph API

### Phase 2: Intégration Transparente

4. **Modifier DocxSession** - Ajouter métadonnées source (SourceType, RemoteUrl, ETag)
5. **Modifier DocumentTools.Open** - Détection et download transparent
6. **Modifier DocumentTools.Save** - Upload transparent si source SharePoint

### Phase 3: Notifications

7. **FileSystemWatcherService** - Pour desktop avec OneDrive sync
8. **DeltaPollingService** - Fallback universel
9. **WebhookService** - Pour environnement serveur (optionnel)

---

## Dépendances

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Graph" Version="5.56.0" />
  <PackageReference Include="Azure.Identity" Version="1.13.0" />
</ItemGroup>
```

---

## Résumé

| Aspect | Approche |
|--------|----------|
| **Outils MCP** | Aucun nouveau - tout transparent via `document_open`/`document_save` |
| **Publication** | Automatique sur `document_save` si source = SharePoint |
| **Download** | Automatique sur `document_open` si URL SharePoint |
| **Liste fichiers** | Hors scope - fourni par outil externe |
| **Notifications** | Background service (FileSystemWatcher / Delta / Webhook) |
| **Auth** | Configuration plateforme uniquement |

---

## Sources et Références

- [Upload files - Microsoft Graph](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content)
- [Delta query](https://learn.microsoft.com/en-us/graph/delta-query-overview)
- [SharePoint webhooks](https://learn.microsoft.com/en-us/sharepoint/dev/apis/webhooks/overview-sharepoint-webhooks)
- [How OneDrive sync works](https://learn.microsoft.com/en-us/sharepoint/sync-process)
- [FileSystemWatcher .NET](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher)
