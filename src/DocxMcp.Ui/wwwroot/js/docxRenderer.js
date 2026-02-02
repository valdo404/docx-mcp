/**
 * Render DOCX bytes from the API into a container using docx-preview.js.
 * Includes debounce for slider scrubbing and a loading spinner.
 */

let renderTimeout = null;

export async function renderDocxAtPosition(container, sessionId, position, debounceMs = 200) {
    clearTimeout(renderTimeout);

    return new Promise((resolve) => {
        renderTimeout = setTimeout(async () => {
            container.innerHTML = '<div class="loading-container"><fluent-progress-ring></fluent-progress-ring></div>';

            try {
                const resp = await fetch(`/api/sessions/${sessionId}/docx?position=${position}`);
                if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
                const blob = await resp.blob();

                container.innerHTML = '';
                const styleEl = document.createElement('style');
                container.appendChild(styleEl);

                await window.docx.renderAsync(blob, container, styleEl, {
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
                container.innerHTML = `<p class="error">Failed to render document: ${err.message}</p>`;
            }

            resolve();
        }, debounceMs);
    });
}

/** Immediate render (no debounce) — used for diff view. */
export async function renderDocxImmediate(container, sessionId, position) {
    return renderDocxAtPosition(container, sessionId, position, 0);
}
