(() => {
    const views = new Map();
    const minimumZoom = 0.5;
    const maximumZoom = 2;
    let pending = false;

    function failed(root, state, error) {
        state.failed = true;
        const notice = root.querySelector("[data-honeycomb-error]");
        const retry = root.querySelector("[data-honeycomb-retry]");
        if (notice) notice.hidden = false;
        if (retry) retry.hidden = false;
        root.querySelectorAll("[data-honeycomb-zoom]").forEach(button => { button.disabled = true; });
        console.error("Honeycomb browser controls could not be updated.", error);
    }

    function release(root, state) {
        try {
            state.observer?.disconnect();
            state.observer = null;
            return true;
        } catch (error) {
            failed(root, state, error);
            return false;
        }
    }

    function update(root, state, zoom = state.zoom) {
        try {
            layout(root, { zoom });
            state.zoom = zoom;
            return true;
        } catch (error) {
            failed(root, state, error);
            return false;
        }
    }

    function initialize(root, state) {
        state.failed = false;
        try {
            state.viewport = root.querySelector(".analysis-honeycomb-viewport");
            if (!state.viewport) throw new Error("The honeycomb viewport is missing.");
            const observer = new ResizeObserver(() => {
                if (state.failed || state.disposed || !root.isConnected || state.observer !== observer) return;
                if (root.querySelector(".analysis-honeycomb-viewport") !== state.viewport) scheduleRefresh();
                else update(root, state);
            });
            state.observer = observer;
            observer.observe(state.viewport);
            if (update(root, state)) {
                root.querySelector("[data-honeycomb-error]").hidden = true;
                root.querySelector("[data-honeycomb-retry]").hidden = true;
            } else release(root, state);
        } catch (error) {
            failed(root, state, error);
            release(root, state);
        }
    }

    function layout(root, state) {
        const viewport = root.querySelector(".analysis-honeycomb-viewport");
        const svg = viewport.querySelector(".analysis-honeycomb");
        const cells = [...svg.querySelectorAll(".analysis-honeycomb-cell")];
        const availableWidth = viewport.clientWidth - 1;
        const availableHeight = viewport.clientHeight - 1;
        if (!cells.length || availableWidth <= 0 || availableHeight <= 0) return;
        const output = root.querySelector("output");
        const zoomOut = root.querySelector('[data-honeycomb-zoom="out"]');
        const zoomIn = root.querySelector('[data-honeycomb-zoom="in"]');
        const fit = root.querySelector('[data-honeycomb-zoom="fit"]');
        if (!output || !zoomOut || !zoomIn || !fit)
            throw new Error("The honeycomb zoom controls are missing.");

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

        const label = `${Math.round(state.zoom * 100)}%`;
        if (output.textContent !== label) output.textContent = label;
        zoomOut.disabled = state.zoom <= minimumZoom;
        zoomIn.disabled = state.zoom >= maximumZoom;
        fit.disabled = false;
    }

    function cleanupRemovedViews() {
        for (const [root, state] of views) {
            if (!root.isConnected) {
                state.disposed = true;
                if (release(root, state)) views.delete(root);
            }
        }
    }

    function refresh() {
        pending = false;
        cleanupRemovedViews();
        document.querySelectorAll("[data-analysis-honeycomb]").forEach(root => {
            let state = views.get(root);
            if (!state) {
                state = { zoom: 1, observer: null, viewport: null, failed: false, disposed: false };
                views.set(root, state);
                initialize(root, state);
            } else if (!state.failed && !state.disposed) {
                if (root.querySelector(".analysis-honeycomb-viewport") !== state.viewport) {
                    if (release(root, state)) initialize(root, state);
                } else update(root, state);
            }
        });
    }

    function scheduleRefresh() {
        if (pending) return;
        pending = true;
        try {
            requestAnimationFrame(refresh);
        } catch (error) {
            pending = false;
            document.querySelectorAll("[data-analysis-honeycomb]").forEach(root => {
                let state = views.get(root);
                if (!state) {
                    state = { zoom: 1, observer: null, viewport: null, failed: false, disposed: false };
                    views.set(root, state);
                }
                failed(root, state, error);
            });
        }
    }

    document.addEventListener("click", event => {
        const retry = event.target.closest?.("[data-honeycomb-retry]");
        if (retry) {
            const root = retry.closest("[data-analysis-honeycomb]");
            const state = views.get(root);
            if (state && !state.disposed && root.isConnected && release(root, state)) initialize(root, state);
            return;
        }
        const button = event.target.closest?.("[data-honeycomb-zoom]");
        if (!button) return;
        const root = button.closest("[data-analysis-honeycomb]");
        const state = views.get(root);
        if (!state || state.failed || state.disposed || !root.isConnected) return;
        const action = button.dataset.honeycombZoom;
        if (!["fit", "in", "out"].includes(action)) {
            failed(root, state, new Error("Unknown honeycomb zoom action."));
            return;
        }
        const zoom = action === "fit" ? 1 :
            Math.min(maximumZoom, Math.max(minimumZoom, state.zoom + (action === "in" ? 0.25 : -0.25)));
        try {
            const viewport = root.querySelector(".analysis-honeycomb-viewport");
            viewport.scrollTop = 0;
            viewport.scrollLeft = 0;
            update(root, state, zoom);
        } catch (error) {
            failed(root, state, error);
        }
    });

    new MutationObserver(() => { cleanupRemovedViews(); scheduleRefresh(); })
        .observe(document.body, { childList: true, subtree: true });
    refresh();
})();
