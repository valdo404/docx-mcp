import { renderDocxAtPosition } from '../docxRenderer.js';
import { buildTreeFromPreview } from '../documentTree.js';

/** Session Detail page — #/session/{id} */
export async function renderSessionDetail(container, sessionId, sse) {
    container.innerHTML = '<div class="loading-container"><fluent-progress-ring></fluent-progress-ring></div>';

    // Fetch session detail
    let detail;
    try {
        const resp = await fetch(`/api/sessions/${sessionId}`);
        if (!resp.ok) throw new Error(`Session not found`);
        detail = await resp.json();
    } catch (err) {
        container.innerHTML = `<p class="error">${err.message}</p>`;
        return;
    }

    const walCount = detail.walCount;
    const cursorPos = detail.cursorPosition >= 0 ? detail.cursorPosition : walCount;
    let currentPos = cursorPos;

    // Fetch history
    let history = [];
    try {
        const hResp = await fetch(`/api/sessions/${sessionId}/history?limit=200`);
        history = await hResp.json();
    } catch { /* ok */ }

    const sourceName = detail.sourcePath
        ? detail.sourcePath.split('/').pop().split('\\').pop()
        : '(new document)';

    container.innerHTML = `
        <div class="session-detail" style="padding:0">
            <div class="detail-toolbar">
                <fluent-button appearance="lightweight" id="btn-back" title="Back to sessions">Back</fluent-button>
                <h2 id="detail-title">${sessionId}</h2>
                <span class="session-source">${sourceName}</span>
                <fluent-button appearance="accent" id="btn-export" title="Download .docx at current position">Export .docx</fluent-button>
            </div>
            <div class="detail-tree tree-container" id="tree-panel"></div>
            <div class="detail-preview" id="preview-panel"></div>
            <div class="detail-timeline" id="timeline-panel">
                <div class="timeline-controls">
                    <span class="pos-label" id="pos-label">Position: ${currentPos}/${walCount}</span>
                    <fluent-button size="small" id="btn-prev" ${currentPos <= 0 ? 'disabled' : ''}>Prev</fluent-button>
                    <input type="range" id="pos-slider" min="0" max="${walCount}" value="${currentPos}" step="1"
                        style="flex:1;accent-color:#0078d4" />
                    <fluent-button size="small" id="btn-next" ${currentPos >= walCount ? 'disabled' : ''}>Next</fluent-button>
                </div>
                <div class="timeline-track" id="timeline-track"></div>
                <div id="history-container"></div>
            </div>
        </div>`;

    // Refs
    const previewPanel = document.getElementById('preview-panel');
    const treePanel = document.getElementById('tree-panel');
    const slider = document.getElementById('pos-slider');
    const posLabel = document.getElementById('pos-label');
    const btnPrev = document.getElementById('btn-prev');
    const btnNext = document.getElementById('btn-next');

    // Build timeline track
    buildTimelineTrack(walCount, detail.checkpointPositions, currentPos);

    // Build history table
    buildHistoryTable(history, sessionId);

    // --- Render document at current position ---
    async function renderAtPosition(pos) {
        currentPos = pos;
        posLabel.textContent = `Position: ${pos}/${walCount}`;
        slider.value = pos;
        btnPrev.disabled = pos <= 0;
        btnNext.disabled = pos >= walCount;

        // Update timeline track highlights
        buildTimelineTrack(walCount, detail.checkpointPositions, pos);

        await renderDocxAtPosition(previewPanel, sessionId, pos);

        // Rebuild tree from rendered preview
        treePanel.innerHTML = '';
        const tree = buildTreeFromPreview(previewPanel);
        treePanel.appendChild(tree);
    }

    await renderAtPosition(currentPos);

    // --- Event handlers ---
    document.getElementById('btn-back').addEventListener('click', () => {
        location.hash = '#/sessions';
    });

    document.getElementById('btn-export').addEventListener('click', () => {
        window.open(`/api/sessions/${sessionId}/docx?position=${currentPos}`, '_blank');
    });

    slider.addEventListener('input', (e) => {
        const newPos = parseInt(e.target.value);
        posLabel.textContent = `Position: ${newPos}/${walCount}`;
    });

    slider.addEventListener('change', (e) => {
        renderAtPosition(parseInt(e.target.value));
    });

    btnPrev.addEventListener('click', () => {
        if (currentPos > 0) renderAtPosition(currentPos - 1);
    });

    btnNext.addEventListener('click', () => {
        if (currentPos < walCount) renderAtPosition(currentPos + 1);
    });

    // SSE: live update when new patches arrive
    const onChanged = async () => {
        try {
            const resp = await fetch(`/api/sessions/${sessionId}`);
            if (!resp.ok) return;
            const updated = await resp.json();
            if (updated.walCount > walCount) {
                detail.walCount = updated.walCount;
                slider.max = updated.walCount;

                // Refresh history
                const hResp = await fetch(`/api/sessions/${sessionId}/history?limit=200`);
                const newHistory = await hResp.json();
                buildHistoryTable(newHistory, sessionId);
                buildTimelineTrack(updated.walCount, updated.checkpointPositions, currentPos);
            }
        } catch { /* ignore */ }
    };
    sse.on('index.changed', onChanged);

    // Cleanup
    const observer = new MutationObserver(() => {
        if (!document.contains(previewPanel)) {
            sse.off('index.changed', onChanged);
            observer.disconnect();
        }
    });
    observer.observe(container, { childList: true });
}

function buildTimelineTrack(walCount, checkpoints, currentPos) {
    const track = document.getElementById('timeline-track');
    if (!track) return;
    track.innerHTML = '';

    const ckptSet = new Set(checkpoints || []);

    for (let i = 0; i <= walCount; i++) {
        const dot = document.createElement('span');
        dot.className = 'timeline-dot';
        dot.title = `Position ${i}`;

        if (i === 0) dot.classList.add('baseline');
        else if (ckptSet.has(i)) dot.classList.add('checkpoint');
        if (i === currentPos) dot.classList.add('current');

        dot.addEventListener('click', () => {
            const slider = document.getElementById('pos-slider');
            if (slider) {
                slider.value = i;
                slider.dispatchEvent(new Event('change'));
            }
        });

        track.appendChild(dot);

        if (i < walCount) {
            const seg = document.createElement('span');
            seg.className = 'timeline-dot segment';
            track.appendChild(seg);
        }
    }
}

function buildHistoryTable(history, sessionId) {
    const hc = document.getElementById('history-container');
    if (!hc) return;

    if (history.length === 0) {
        hc.innerHTML = '<p style="font-size:13px;color:#707070">No patches yet.</p>';
        return;
    }

    hc.innerHTML = `
        <table class="history-table">
            <thead>
                <tr><th>#</th><th>Time</th><th>Description</th><th></th></tr>
            </thead>
            <tbody>
                ${history.map(h => `
                    <tr>
                        <td class="pos-badge">${h.position}${h.isCheckpoint ? ' <span class="ckpt-badge">ckpt</span>' : ''}</td>
                        <td>${new Date(h.timestamp).toLocaleTimeString()}</td>
                        <td style="font-family:monospace;font-size:12px;max-width:400px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(h.description)}</td>
                        <td><fluent-button size="small" appearance="lightweight" data-pos="${h.position}" class="btn-diff">Diff</fluent-button></td>
                    </tr>`).join('')}
            </tbody>
        </table>`;

    hc.querySelectorAll('.btn-diff').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            const pos = btn.dataset.pos;
            location.hash = `#/diff/${sessionId}/${pos}`;
        });
    });
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text || '';
    return div.innerHTML;
}
