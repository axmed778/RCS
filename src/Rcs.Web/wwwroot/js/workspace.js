// Local, progressive enhancement of the authoritative Razor relationship tree.
// Original detail elements (including forms and antiforgery tokens) are moved, never copied or reconstructed.
(function () {
    "use strict";

    const filter = document.querySelector('[data-list-filter]');
    if (filter) {
        const rows = Array.from(document.querySelectorAll('[data-filter-table] tbody tr'));
        const count = document.querySelector('[data-list-count]');
        const update = function () {
            const query = filter.value.trim().toLocaleLowerCase('az');
            rows.forEach(row => { row.hidden = !row.textContent.toLocaleLowerCase('az').includes(query); });
            const visible = rows.filter(row => !row.hidden).length;
            count.textContent = visible + ' / ' + rows.length;
            document.querySelector('[data-list-empty]').hidden = visible !== 0;
        };
        filter.addEventListener('input', update);
        update();
    }
    document.querySelectorAll('[data-case-row]').forEach(row => {
        row.addEventListener('click', event => {
            if (event.target.closest('a, button, input') || window.getSelection().toString()) return;
            const link = row.querySelector('a');
            if (link) window.location.assign(link.href);
        });
    });

    const workspace = document.querySelector('[data-workspace]');
    if (!workspace) return;
    const source = workspace.querySelector('.workspace-source');
    const stage = workspace.querySelector('.graph-stage');
    const viewport = workspace.querySelector('.graph-viewport');
    const inspector = workspace.querySelector('.inspector');
    const details = workspace.querySelector('[data-inspector-details]');
    const historyPanel = workspace.querySelector('[data-inspector-history]');
    const activity = source.querySelector('[data-case-activity]');
    const nodes = Array.from(source.querySelectorAll('[data-graph-node]'));
    const byId = new Map();
    let selected;
    let activeTab = 'details';
    let zoom = 1;
    let fitMode = true;
    let explicitFit = false;
    let connectorLayer;

    function element(tag, className, text) {
        const result = document.createElement(tag);
        if (className) result.className = className;
        if (text !== undefined) result.textContent = text;
        return result;
    }

    // Capture relationships before moving the original detail panels out of their recursive tree.
    nodes.forEach(node => {
        const parent = node.parentElement.closest('[data-graph-node]');
        byId.set(node.id, { node, parentId: parent?.id, children: [] });
    });
    const caseEntry = nodes.find(node => node.dataset.kind === 'case');
    const letterEntry = nodes.find(node => node.dataset.kind === 'letter');
    if (!caseEntry) return;
    byId.forEach(entry => {
        // Requests and final results hang off the case: both are things the case produced, not things inside
        // another node. Everything else is parented by where it sits in the rendered tree.
        if ((entry.node.dataset.kind === 'request' || entry.node.dataset.kind === 'result') && !entry.parentId) entry.parentId = caseEntry.id;
        if (entry.node === caseEntry && letterEntry) entry.parentId = letterEntry.id;
        if (entry.parentId) byId.get(entry.parentId)?.children.push(entry);
    });

    function select(entry, updateHash) {
        selected = entry;
        byId.forEach(item => {
            const isSelected = item === entry;
            item.button.classList.toggle('is-selected', isSelected);
            item.button.setAttribute('aria-pressed', String(isSelected));
            item.node.hidden = !isSelected;
        });
        workspace.querySelector('[data-inspector-title]').textContent = entry.node.dataset.title;
        workspace.querySelector('[data-inspector-type]').textContent = entry.node.dataset.label;
        const events = activity ? Array.from(activity.children) : [];
        let count = 0;
        events.forEach(event => {
            event.hidden = entry.node !== caseEntry && event.dataset.entity !== entry.node.dataset.entity;
            if (!event.hidden) count++;
        });
        workspace.querySelector('[data-history-count]').textContent = count ? String(count) : '';
        workspace.querySelector('[data-inspector-tab="history"]').hidden = count === 0;
        if (!count) activeTab = 'details';
        showTab(activeTab);
        if (updateHash) window.history.replaceState(null, '', '#' + entry.node.id);
    }

    function showTab(name) {
        activeTab = name;
        details.hidden = name !== 'details';
        historyPanel.hidden = name !== 'history';
        workspace.querySelectorAll('[data-inspector-tab]').forEach(button => {
            const active = button.dataset.inspectorTab === name;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-pressed', String(active));
        });
    }

    const symbols = { case: '▣', letter: '▤', request: '↗', response: '↙', requirement: '◇' };
    const depth = entry => entry.children.length ? 1 + Math.max(...entry.children.map(depth)) : 0;
    function branch(entry) {
        const data = entry.node.dataset;
        const li = element('li', 'graph-branch');
        const button = element('button', 'graph-node tone-' + data.tone + ' graph-node--' + data.kind);
        button.type = 'button';
        button.id = 'graph-' + entry.node.id;
        button.setAttribute('aria-controls', entry.node.id);
        button.setAttribute('aria-pressed', 'false');
        if (data.blocking === 'true') button.classList.add('is-blocking');
        const heading = element('span', 'graph-node-heading');
        const icon = element('span', 'graph-icon', symbols[data.kind]);
        icon.setAttribute('aria-hidden', 'true');
        heading.append(icon, element('span', 'graph-kind', data.label));
        button.append(heading, element('strong', 'graph-title', data.title), element('span', 'graph-meta', data.meta));
        if (data.summary) button.append(element('span', 'graph-summary', data.summary));
        const status = element('span', 'graph-status', data.status);
        status.prepend(element('i', 'status-dot'));
        button.append(status);
        if (data.kind === 'case') button.append(element('span', 'graph-case-summary', workspace.querySelector('.summary-line')?.textContent));
        entry.button = button;
        entry.li = li;
        button.addEventListener('click', () => select(entry, true));
        li.append(button);
        if (entry.children.length) {
            const children = element('ul', 'graph-children');
            // Long dependency chains use the available width while keeping every domain edge explicit.
            if (data.kind === 'request' && depth(entry) >= 3) children.classList.add('graph-flow');
            children.id = 'children-' + entry.node.id;
            entry.children.forEach(child => children.append(branch(child)));
            const toggle = element('button', 'branch-toggle', '−');
            toggle.type = 'button';
            toggle.setAttribute('aria-label', workspace.dataset.collapse + ': ' + data.title);
            toggle.setAttribute('aria-expanded', 'true');
            toggle.setAttribute('aria-controls', children.id);
            toggle.addEventListener('click', () => {
                children.hidden = !children.hidden;
                toggle.textContent = children.hidden ? '+' : '−';
                toggle.setAttribute('aria-expanded', String(!children.hidden));
                toggle.setAttribute('aria-label', (children.hidden ? workspace.dataset.expand : workspace.dataset.collapse) + ': ' + data.title);
                if (children.hidden && selected && children.contains(selected.button)) select(entry, true);
                if (fitMode) fit();
                requestAnimationFrame(drawConnectors);
            });
            entry.toggle = toggle;
            entry.childrenList = children;
            li.append(toggle, children);
        }
        return li;
    }

    const tree = element('ul', 'graph-tree');
    tree.append(branch(byId.get((letterEntry || caseEntry).id)));
    stage.append(tree);
    const svgNamespace = 'http://www.w3.org/2000/svg';
    connectorLayer = document.createElementNS(svgNamespace, 'svg');
    connectorLayer.classList.add('graph-connectors');
    connectorLayer.setAttribute('aria-hidden', 'true');
    stage.prepend(connectorLayer);
    // Moving a descendant first or last is safe: the node references were captured above.
    nodes.forEach(node => {
        node.querySelectorAll(':scope > .tree').forEach(nested => nested.remove());
        const entry = byId.get(node.id);
        if (entry.children.length) {
            const related = element('div', 'related-nodes');
            related.append(element('h3', 'eyebrow', workspace.dataset.related));
            entry.children.forEach(child => {
                const link = element('a', 'related-node', child.node.dataset.label + ' · ' + child.node.dataset.title);
                link.href = '#' + child.node.id;
                related.append(link);
            });
            node.append(related);
        }
        details.append(node);
    });
    if (activity) historyPanel.append(activity);
    source.hidden = true;
    workspace.querySelector('.workspace-tools').hidden = false;
    workspace.querySelector('.canvas').hidden = false;
    inspector.hidden = false;
    workspace.classList.add('is-enhanced');

    function applyZoom(value) {
        zoom = Math.max(0.35, Math.min(1.5, value));
        tree.style.zoom = zoom;
        workspace.querySelector('[data-zoom-value]').textContent = Math.round(zoom * 100) + '%';
        requestAnimationFrame(drawConnectors);
    }
    function fit() {
        const naturalWidth = tree.getBoundingClientRect().width / zoom;
        const naturalHeight = tree.getBoundingClientRect().height / zoom;
        const fitted = Math.min(1, (viewport.clientWidth - 48) / naturalWidth, (viewport.clientHeight - 40) / naturalHeight);
        applyZoom(explicitFit ? fitted : Math.max(0.8, fitted));
    }
    function drawConnectors() {
        connectorLayer.replaceChildren();
        const origin = stage.getBoundingClientRect();
        connectorLayer.setAttribute('viewBox', '0 0 ' + origin.width + ' ' + origin.height);
        byId.forEach(entry => {
            const parent = byId.get(entry.parentId);
            if (!parent || !entry.button.getClientRects().length) return;
            const from = parent.button.getBoundingClientRect();
            const to = entry.button.getBoundingClientRect();
            const horizontal = parent.button.closest('.graph-flow') !== null;
            const x1 = (horizontal ? from.right : from.left + from.width / 2) - origin.left;
            const y1 = (horizontal ? from.top + from.height / 2 : from.bottom) - origin.top;
            const x2 = (horizontal ? to.left : to.left + to.width / 2) - origin.left;
            const y2 = (horizontal ? to.top + to.height / 2 : to.top) - origin.top;
            const path = document.createElementNS(svgNamespace, 'path');
            const middle = horizontal ? (x1 + x2) / 2 : (y1 + y2) / 2;
            path.setAttribute('d', horizontal
                ? `M ${x1} ${y1} H ${middle} V ${y2} H ${x2} m -4 -3 l 4 3 -4 3`
                : `M ${x1} ${y1} V ${middle} H ${x2} V ${y2} m -3 -4 l 3 4 3 -4`);
            connectorLayer.append(path);
        });
    }
    workspace.querySelectorAll('[data-zoom]').forEach(button => button.addEventListener('click', () => {
        const action = button.dataset.zoom;
        fitMode = action === 'fit';
        explicitFit = fitMode;
        if (fitMode) fit();
        else applyZoom(action === 'reset' ? 1 : zoom + (action === 'in' ? 0.1 : -0.1));
    }));
    workspace.querySelectorAll('[data-inspector-tab]').forEach(button => button.addEventListener('click', () => showTab(button.dataset.inspectorTab)));
    function selectHash() {
        const entry = byId.get(window.location.hash.slice(1));
        if (!entry) return false;
        let parent = byId.get(entry.parentId);
        while (parent) {
            if (parent.childrenList?.hidden) parent.toggle.click();
            parent = byId.get(parent.parentId);
        }
        select(entry, false);
        return true;
    }
    window.addEventListener('hashchange', selectHash);
    if (!selectHash()) select(byId.get(caseEntry.id), false);
    new ResizeObserver(() => { if (fitMode) fit(); }).observe(viewport);
    new ResizeObserver(drawConnectors).observe(tree);
    requestAnimationFrame(fit);
})();
