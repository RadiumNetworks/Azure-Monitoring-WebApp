(() => {
    function getSvg(id) {
        return document.getElementById(id);
    }

    function applyView(svg, view) {
        svg.setAttribute("viewBox", `${view.x} ${view.y} ${view.width} ${view.height}`);
    }

    function constrainView(state, view) {
        const width = Math.min(state.base.width, Math.max(state.base.width * 0.2, view.width));
        const height = state.base.height * width / state.base.width;
        const maximumX = state.base.x + state.base.width - width;
        const maximumY = state.base.y + state.base.height - height;

        return {
            x: Math.min(maximumX, Math.max(state.base.x, view.x)),
            y: Math.min(maximumY, Math.max(state.base.y, view.y)),
            width,
            height
        };
    }

    function zoomView(svg, factor, clientX, clientY) {
        const state = svg?._alertGraph;
        if (!state) {
            return;
        }

        const bounds = svg.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0 || !Number.isFinite(factor) || factor <= 0) {
            return;
        }

        const anchorX = Math.min(1, Math.max(0, clientX === undefined ? 0.5 : (clientX - bounds.left) / bounds.width));
        const anchorY = Math.min(1, Math.max(0, clientY === undefined ? 0.5 : (clientY - bounds.top) / bounds.height));
        const width = state.view.width * factor;
        const height = state.base.height * width / state.base.width;
        const graphX = state.view.x + state.view.width * anchorX;
        const graphY = state.view.y + state.view.height * anchorY;

        state.view = constrainView(state, {
            x: graphX - width * anchorX,
            y: graphY - height * anchorY,
            width,
            height
        });
        applyView(svg, state.view);
    }

    window.alertGraph = {
        initialize(id) {
            const svg = getSvg(id);
            if (!svg) {
                return;
            }

            this.dispose(id);
            const values = svg.getAttribute("viewBox").split(/\s+/).map(Number);
            const controller = new AbortController();
            const base = { x: values[0], y: values[1], width: values[2], height: values[3] };
            const state = { base, view: { ...base }, controller, drag: null };
            svg._alertGraph = state;

            svg.addEventListener("wheel", event => {
                event.preventDefault();
                zoomView(svg, event.deltaY < 0 ? 0.86 : 1.16, event.clientX, event.clientY);
            }, { passive: false, signal: controller.signal });

            svg.addEventListener("pointerdown", event => {
                if (event.button !== 0) {
                    return;
                }

                event.preventDefault();
                svg.setPointerCapture(event.pointerId);
                state.drag = { clientX: event.clientX, clientY: event.clientY, x: state.view.x, y: state.view.y };
                svg.classList.add("dragging");
            }, { signal: controller.signal });

            svg.addEventListener("pointermove", event => {
                if (!state.drag) {
                    return;
                }

                const bounds = svg.getBoundingClientRect();
                if (bounds.width <= 0 || bounds.height <= 0) {
                    return;
                }

                state.view = constrainView(state, {
                    ...state.view,
                    x: state.drag.x - (event.clientX - state.drag.clientX) * state.view.width / bounds.width,
                    y: state.drag.y - (event.clientY - state.drag.clientY) * state.view.height / bounds.height
                });
                applyView(svg, state.view);
            }, { signal: controller.signal });

            const stopDragging = () => {
                state.drag = null;
                svg.classList.remove("dragging");
            };
            svg.addEventListener("pointerup", stopDragging, { signal: controller.signal });
            svg.addEventListener("pointercancel", stopDragging, { signal: controller.signal });
        },

        zoom(id, factor) {
            zoomView(getSvg(id), factor);
        },

        reset(id) {
            const svg = getSvg(id);
            const state = svg?._alertGraph;
            if (!state) {
                return;
            }

            state.view = { ...state.base };
            applyView(svg, state.view);
        },

        dispose(id) {
            const svg = getSvg(id);
            if (svg?._alertGraph) {
                svg._alertGraph.controller.abort();
                delete svg._alertGraph;
            }
        }
    };
})();