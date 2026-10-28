(() => {
    const views = new Map();
    const minimumZoom = 0.5;
    const maximumZoom = 2;
    let pending = false;

    function layout(root, state) {
        const viewport = root.querySelector(".analysis-honeycomb-viewport");
        const svg = viewport.querySelector(".analysis-honeycomb");
        const cells = [...svg.querySelectorAll(".analysis-honeycomb-cell")];
        const availableWidth = viewport.clientWidth - 1;
        const availableHeight = viewport.clientHeight - 1;
        if (!cells.length || availableWidth <= 0 || availableHeight <= 0) return;

        let best = null;
        for (let columns = 1; columns <= cells.length; columns++) {
            const lastRow = Math.floor((cells.length - 1) / columns);
            const lastRowCount = (cells.length - 1) % columns + 1;
            const width = 148 + (columns - 1) * 105;
            const height = 128 + lastRow * 122 + (lastRowCount > 1 ? 61 : 0);
            const scale = Math.min(availableWidth / width, availableHeight / height);
            if (!best || scale > best.scale) best = { columns, width, height, scale };
        }

        cells.forEach((cell, index) => {
            const column = index % best.columns;
            const x = 74 + column * 105;
            const y = 64 + Math.floor(index / best.columns) * 122 + (column % 2) * 61;
            cell.setAttribute("transform", `translate(${x} ${y})`);
        });
        svg.setAttribute("viewBox", `0 0 ${best.width} ${best.height}`);
        svg.style.width = `${best.width * best.scale * state.zoom}px`;
        svg.style.height = `${best.height * best.scale * state.zoom}px`;

        const output = root.querySelector("output");
        const label = `${Math.round(state.zoom * 100)}%`;
        if (output.textContent !== label) output.textContent = label;
        root.querySelector('[data-honeycomb-zoom="out"]').disabled = state.zoom <= minimumZoom;
        root.querySelector('[data-honeycomb-zoom="in"]').disabled = state.zoom >= maximumZoom;
    }

    function refresh() {
        pending = false;
        for (const [root, state] of views) {
            if (!root.isConnected) {
                state.observer.disconnect();
                views.delete(root);
            }
        }
        document.querySelectorAll("[data-analysis-honeycomb]").forEach(root => {
            let state = views.get(root);
            if (!state) {
                state = { zoom: 1, observer: new ResizeObserver(() => layout(root, state)) };
                views.set(root, state);
                state.observer.observe(root.querySelector(".analysis-honeycomb-viewport"));
            }
            layout(root, state);
        });
    }

    function scheduleRefresh() {
        if (pending) return;
        pending = true;
        requestAnimationFrame(refresh);
    }

    document.addEventListener("click", event => {
        const button = event.target.closest?.("[data-honeycomb-zoom]");
        if (!button) return;
        const root = button.closest("[data-analysis-honeycomb]");
        const state = views.get(root);
        if (!state) return;
        const action = button.dataset.honeycombZoom;
        state.zoom = action === "fit" ? 1 :
            Math.min(maximumZoom, Math.max(minimumZoom, state.zoom + (action === "in" ? 0.25 : -0.25)));
        const viewport = root.querySelector(".analysis-honeycomb-viewport");
        viewport.scrollTop = 0;
        viewport.scrollLeft = 0;
        layout(root, state);
    });

    new MutationObserver(scheduleRefresh).observe(document.body, { childList: true, subtree: true });
    refresh();
})();
