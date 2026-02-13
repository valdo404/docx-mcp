/** SSE client with auto-reconnect and typed event dispatch. */
export function connectSSE(url) {
    const listeners = {};
    const source = new EventSource(url);
    const statusBadge = document.getElementById('sse-status');

    function updateStatus(connected) {
        if (statusBadge) {
            statusBadge.setAttribute('color', connected ? 'success' : 'danger');
            statusBadge.textContent = connected ? 'Live' : 'Disconnected';
        }
    }

    source.onopen = () => updateStatus(true);
    source.onerror = () => updateStatus(false);

    return {
        on(eventType, callback) {
            if (!listeners[eventType]) {
                listeners[eventType] = [];
                source.addEventListener(eventType, (e) => {
                    let data;
                    try { data = JSON.parse(e.data); } catch { data = e.data; }
                    listeners[eventType].forEach(cb => cb(data));
                });
            }
            listeners[eventType].push(callback);
        },
        off(eventType, callback) {
            if (listeners[eventType]) {
                listeners[eventType] = listeners[eventType].filter(cb => cb !== callback);
            }
        },
        close() {
            source.close();
        }
    };
}
