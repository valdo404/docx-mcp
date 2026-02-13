/** Session List page — #/sessions */
export async function renderSessionList(container, sse) {
    container.innerHTML = `
        <div class="session-list">
            <h2>Active Sessions</h2>
            <div id="session-grid-container">
                <div class="loading-container"><fluent-progress-ring></fluent-progress-ring></div>
            </div>
            <div class="footer-info" id="sessions-footer"></div>
        </div>`;

    await loadSessions();

    // Live refresh via SSE
    const onIndexChanged = () => loadSessions();
    sse.on('index.changed', onIndexChanged);

    // Cleanup when navigating away (container gets cleared)
    const observer = new MutationObserver(() => {
        if (!document.contains(container.querySelector('.session-list'))) {
            sse.off('index.changed', onIndexChanged);
            observer.disconnect();
        }
    });
    observer.observe(container, { childList: true });
}

async function loadSessions() {
    const gridContainer = document.getElementById('session-grid-container');
    if (!gridContainer) return;

    try {
        const resp = await fetch('/api/sessions');
        const sessions = await resp.json();

        if (sessions.length === 0) {
            gridContainer.innerHTML = `
                <div class="empty-state">
                    <h3>No active sessions</h3>
                    <p>Open a document with docx-mcp or docx-cli to see it here.</p>
                </div>`;
            return;
        }

        gridContainer.innerHTML = `
            <table class="sessions-table">
                <thead>
                    <tr>
                        <th>ID</th>
                        <th>Source</th>
                        <th>Modified</th>
                        <th>WAL</th>
                        <th>Pos</th>
                    </tr>
                </thead>
                <tbody>
                    ${sessions.map(s => `
                        <tr data-id="${s.id}">
                            <td class="session-id">${s.id}</td>
                            <td class="session-path" title="${s.sourcePath || '(new document)'}">${s.sourcePath ? fileName(s.sourcePath) : '<em>(new document)</em>'}</td>
                            <td>${timeAgo(s.lastModifiedAt)}</td>
                            <td>${s.walCount}</td>
                            <td>${s.cursorPosition >= 0 ? s.cursorPosition : s.walCount}</td>
                        </tr>`).join('')}
                </tbody>
            </table>`;

        // Click handler
        gridContainer.querySelectorAll('tr[data-id]').forEach(tr => {
            tr.addEventListener('click', () => {
                location.hash = `#/session/${tr.dataset.id}`;
            });
        });

        const footer = document.getElementById('sessions-footer');
        if (footer) footer.textContent = `${sessions.length} session(s)`;
    } catch (err) {
        gridContainer.innerHTML = `<p class="error">Failed to load sessions: ${err.message}</p>`;
    }
}

function fileName(path) {
    return path?.split('/').pop()?.split('\\').pop() || path;
}

function timeAgo(isoStr) {
    if (!isoStr) return '—';
    const date = new Date(isoStr);
    const now = new Date();
    const diffMs = now - date;
    const diffS = Math.floor(diffMs / 1000);
    if (diffS < 60) return `${diffS}s ago`;
    const diffM = Math.floor(diffS / 60);
    if (diffM < 60) return `${diffM}m ago`;
    const diffH = Math.floor(diffM / 60);
    if (diffH < 24) return `${diffH}h ago`;
    const diffD = Math.floor(diffH / 24);
    return `${diffD}d ago`;
}
