import { renderDocxImmediate } from '../docxRenderer.js';

/** Diff View page — #/diff/{sessionId}/{position} */
export async function renderDiffView(container, sessionId, position) {
    const beforePos = Math.max(0, position - 1);
    const afterPos = position;

    // Fetch history entry for this patch
    let patchEntry = null;
    try {
        const resp = await fetch(`/api/sessions/${sessionId}/history?limit=200`);
        const entries = await resp.json();
        patchEntry = entries.find(e => e.position === afterPos);
    } catch { /* ok */ }

    // Fetch total count
    let detail = null;
    try {
        const resp = await fetch(`/api/sessions/${sessionId}`);
        detail = await resp.json();
    } catch { /* ok */ }

    const walCount = detail?.walCount || afterPos;
    const desc = patchEntry?.description || `Position ${beforePos} to ${afterPos}`;
    const patchJson = patchEntry?.patches || '[]';

    let prettyJson;
    try {
        prettyJson = JSON.stringify(JSON.parse(patchJson), null, 2);
    } catch {
        prettyJson = patchJson;
    }

    container.innerHTML = `
        <div class="diff-view" style="padding:0">
            <div class="diff-toolbar">
                <fluent-button appearance="lightweight" id="btn-back-diff">Back</fluent-button>
                <h2>Patch #${afterPos}: Position ${beforePos} &rarr; ${afterPos}</h2>
                <span style="font-size:12px;font-family:monospace;color:#707070">${escapeHtml(desc)}</span>
                <div style="flex:1"></div>
                <fluent-button size="small" id="btn-dl-before" title="Download before state">Before .docx</fluent-button>
                <fluent-button size="small" id="btn-dl-after" title="Download after state">After .docx</fluent-button>
            </div>
            <div class="diff-panels">
                <div class="diff-panel">
                    <div class="diff-panel-label">Before (Position ${beforePos})</div>
                    <div id="diff-before"></div>
                </div>
                <div class="diff-panel">
                    <div class="diff-panel-label">After (Position ${afterPos})</div>
                    <div id="diff-after"></div>
                </div>
            </div>
            <div class="diff-bottom">
                <details open>
                    <summary style="cursor:pointer;font-weight:600;margin-bottom:8px">Patch Operations</summary>
                    <pre class="patch-json">${escapeHtml(prettyJson)}</pre>
                </details>
                <div class="diff-nav">
                    <fluent-button size="small" id="btn-prev-patch" ${afterPos <= 1 ? 'disabled' : ''}>Prev Patch</fluent-button>
                    <span style="font-size:13px">Patch ${afterPos} of ${walCount}</span>
                    <fluent-button size="small" id="btn-next-patch" ${afterPos >= walCount ? 'disabled' : ''}>Next Patch</fluent-button>
                </div>
            </div>
        </div>`;

    // Render both documents in parallel
    const beforeContainer = document.getElementById('diff-before');
    const afterContainer = document.getElementById('diff-after');

    await Promise.all([
        renderDocxImmediate(beforeContainer, sessionId, beforePos),
        renderDocxImmediate(afterContainer, sessionId, afterPos)
    ]);

    // Navigation
    document.getElementById('btn-back-diff').addEventListener('click', () => {
        location.hash = `#/session/${sessionId}`;
    });

    document.getElementById('btn-dl-before').addEventListener('click', () => {
        window.open(`/api/sessions/${sessionId}/docx?position=${beforePos}`, '_blank');
    });

    document.getElementById('btn-dl-after').addEventListener('click', () => {
        window.open(`/api/sessions/${sessionId}/docx?position=${afterPos}`, '_blank');
    });

    document.getElementById('btn-prev-patch').addEventListener('click', () => {
        if (afterPos > 1) location.hash = `#/diff/${sessionId}/${afterPos - 1}`;
    });

    document.getElementById('btn-next-patch').addEventListener('click', () => {
        if (afterPos < walCount) location.hash = `#/diff/${sessionId}/${afterPos + 1}`;
    });
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
}
