import { renderSessionList } from './views/sessionList.js';
import { renderSessionDetail } from './views/sessionDetail.js';
import { renderDiffView } from './views/diffView.js';
import { connectSSE } from './sseClient.js';

const content = document.getElementById('app-content');
const sse = connectSSE('/api/events');

function route() {
    const hash = location.hash || '#/sessions';
    const parts = hash.replace('#/', '').split('/');
    const page = parts[0];

    content.innerHTML = '';

    switch (page) {
        case 'session':
            if (parts[1]) {
                renderSessionDetail(content, parts[1], sse);
            } else {
                renderSessionList(content, sse);
            }
            break;
        case 'diff':
            if (parts[1] && parts[2]) {
                renderDiffView(content, parts[1], parseInt(parts[2]));
            } else {
                renderSessionList(content, sse);
            }
            break;
        case 'sessions':
        default:
            renderSessionList(content, sse);
            break;
    }
}

window.addEventListener('hashchange', route);
route();

// Theme toggle
const themeToggle = document.getElementById('theme-toggle');
if (themeToggle) {
    themeToggle.addEventListener('change', (e) => {
        const provider = document.getElementById('design-provider');
        if (provider) {
            const isDark = e.target.checked;
            provider.setAttribute('base-layer-luminance', isDark ? '0.15' : '1');
            document.body.style.background = isDark ? '#1a1a1a' : '#fafafa';
        }
    });
}
