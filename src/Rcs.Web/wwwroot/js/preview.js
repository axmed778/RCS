(() => {
    'use strict';
    const dialog = document.querySelector('#document-preview');
    if (!dialog) return;
    const viewport = dialog.querySelector('[data-preview-viewport]');
    const status = dialog.querySelector('[data-preview-status]');
    const pageLabel = dialog.querySelector('[data-preview-page]');
    const retry = dialog.querySelector('[data-preview-action="retry"]');
    const states = { PENDING: 'Ön baxış hazırlanır…', PROCESSING: 'Ön baxış hazırlanır…', UNSUPPORTED: 'Bu format üçün ön baxış mümkün deyil. Orijinalı endirə bilərsiniz.', FAILED: 'Ön baxış hazırlanmadı. Orijinal sənəd dəyişməyib.', READY: 'Ön baxış hazırdır' };
    let base, artifacts = [], page = 0, zoom = 1, timer, request, geometry;
    async function get(url, signal) {
        const response = await fetch(url, { signal, cache: 'no-store', credentials: 'same-origin' });
        if (!response.ok) throw new Error('Sənəd mövcud deyil və ya giriş icazəsi yoxdur.');
        return response.json();
    }
    function drawGeometry(data) {
        const canvas = document.createElement('canvas');
        canvas.width = Math.round(Math.max(200, viewport.clientWidth - 40) * zoom);
        canvas.height = Math.round(Math.max(200, viewport.clientHeight - 40) * zoom);
        canvas.setAttribute('aria-label', 'Həndəsə görünüşü');
        const ctx = canvas.getContext('2d');
        const [x0, y0, x1, y1] = data.bounds;
        const scale = Math.min((canvas.width - 60) / Math.max(x1 - x0, 1e-9), (canvas.height - 60) / Math.max(y1 - y0, 1e-9));
        const point = p => [30 + (p[0] - x0) * scale, canvas.height - 30 - (p[1] - y0) * scale];
        ctx.strokeStyle = '#72b8ff'; ctx.fillStyle = '#72b8ff'; ctx.lineWidth = 1.5; ctx.font = '14px sans-serif';
        for (const f of data.features) {
            if (f.t === 'point' || f.t === 'text') {
                const [x, y] = point(f.c); ctx.beginPath(); ctx.arc(x, y, 3, 0, Math.PI * 2); ctx.fill();
                if (f.n || f.s) ctx.fillText(f.n || f.s, x + 6, y - 5);
            } else {
                ctx.beginPath();
                for (const ring of f.t === 'polygon' ? f.c : [f.c]) {
                    ring.forEach((p, i) => { const [x, y] = point(p); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
                    if (f.t === 'polygon') ctx.closePath();
                }
                ctx.stroke();
                if (f.t === 'polygon') { ctx.globalAlpha = 0.15; ctx.fill('evenodd'); ctx.globalAlpha = 1; }
            }
        }
        viewport.replaceChildren(canvas);
        status.textContent = `${data.crs} · ${data.bounds.join(', ')} · ${data.features.length} obyekt${data.truncated ? ' · Natamam görünüş' : ''}`;
    }
    function render() {
        dialog.querySelector('[data-preview-action="previous"]').disabled = page <= 0;
        dialog.querySelector('[data-preview-action="next"]').disabled = page >= artifacts.length - 1;
        pageLabel.textContent = artifacts.length ? `${page + 1} / ${artifacts.length}` : '';
        if (geometry) { drawGeometry(geometry); return; }
        const a = artifacts[page];
        if (!a) return;
        const img = document.createElement('img'); img.src = `${base}/artifacts/${a.id}`; img.alt = `Səhifə ${page + 1}`;
        const fit = Math.min(1, Math.max(100, viewport.clientWidth - 40) / (a.width || 1000), Math.max(100, viewport.clientHeight - 40) / (a.height || 1000));
        img.width = Math.round((a.width || 1000) * fit * zoom);
        img.addEventListener('error', () => { status.textContent = 'Ön baxış faylı açıla bilmədi.'; });
        viewport.replaceChildren(img);
    }
    async function load() {
        clearTimeout(timer); request?.abort(); request = new AbortController();
        const signal = request.signal;
        try {
            const data = await get(base, signal);
            dialog.querySelector('[data-preview-title]').textContent = data.title;
            status.textContent = states[data.status] || 'Ön baxış mümkün deyil';
            retry.hidden = !data.canRetry;
            artifacts = data.artifacts.filter(a => ['PAGE', 'IMAGE', 'GEOMETRY'].includes(a.kind)).sort((a, b) => (a.pageNumber || 0) - (b.pageNumber || 0));
            if (data.status === 'READY') {
                if (data.type === 'GEOMETRY') geometry = await get(`${base}/artifacts/${artifacts[0].id}`, signal);
                render();
                if (data.pageCount > data.pagesRendered) status.textContent += ` · İlk ${data.pagesRendered} / ${data.pageCount} səhifə`;
            } else if (['PENDING', 'PROCESSING'].includes(data.status)) timer = setTimeout(load, 3000);
        } catch (error) { if (error.name !== 'AbortError') status.textContent = error.message; }
    }
    document.addEventListener('click', event => {
        const button = event.target.closest('[data-preview]');
        if (!button) return;
        base = button.dataset.preview; page = 0; zoom = 1; geometry = null; artifacts = []; viewport.replaceChildren();
        pageLabel.textContent = ''; retry.hidden = true;
        dialog.querySelector('[data-preview-title]').textContent = 'Sənədin ön baxışı';
        status.textContent = 'Ön baxış yüklənir…';
        dialog.showModal(); load();
    });
    dialog.addEventListener('close', () => {
        clearTimeout(timer); request?.abort(); viewport.replaceChildren(); dialog.classList.remove('is-fullscreen');
        dialog.querySelector('[data-preview-action="fullscreen"]').textContent = 'Tam ekran';
        if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    });
    document.addEventListener('fullscreenchange', render);
    new ResizeObserver(() => { if (dialog.open) render(); }).observe(viewport);
    dialog.addEventListener('click', async event => {
        const action = event.target.closest('[data-preview-action]')?.dataset.previewAction;
        if (!action) return;
        if (action === 'close') { dialog.close(); return; }
        if (action === 'fullscreen') {
            const expanded = dialog.classList.toggle('is-fullscreen');
            event.target.textContent = expanded ? 'Tam ekrandan çıx' : 'Tam ekran';
            // The full-window viewer also works in embedded browsers that do not grant native fullscreen.
            if (expanded) dialog.querySelector('.preview-surface').requestFullscreen?.().catch(() => {});
            else if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
            render(); return;
        }
        if (action === 'retry') {
            retry.disabled = true;
            try {
                const token = dialog.querySelector('[name="__RequestVerificationToken"]').value;
                const response = await fetch(`${base}/retry`, { method: 'POST', headers: { RequestVerificationToken: token } });
                if (!response.ok) throw new Error('Təkrar cəhd mümkün deyil.');
                await load();
            } catch (error) { status.textContent = error.message; } finally { retry.disabled = false; }
            return;
        }
        if (action === 'previous') page = Math.max(0, page - 1);
        if (action === 'next') page = Math.min(artifacts.length - 1, page + 1);
        if (action === 'in') zoom = Math.min(4, zoom * 1.25);
        if (action === 'out') zoom = Math.max(0.25, zoom / 1.25);
        if (action === 'fit') zoom = 1;
        render();
    });
})();
