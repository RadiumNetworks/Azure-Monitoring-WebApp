(() => {
    const storagePrefix = "alert-console-column-width-";
    const sessions = new Map();
    let activeResize = null;

    const elements = (root, selector) => [
        ...(root.matches?.(selector) ? [root] : []), ...root.querySelectorAll(selector)
    ];
    const message = (region, kind, visible) => {
        const notice = region?.querySelector(`[data-table-${kind}-error]`);
        if (notice) notice.hidden = !visible;
        const retry = region?.querySelector("[data-table-retry]");
        if (retry) retry.hidden = Array.from(region.querySelectorAll("[data-table-storage-error], [data-table-browser-error]"))
            .every(notice => notice.hidden);
    };
    const failed = (region, kind, error) => {
        message(region, kind, true);
        console.error(`Table ${kind} browser action failed.`, error);
    };
    const browserAction = (region, action) => {
        try {
            action();
            return true;
        } catch (error) {
            failed(region, "browser", error);
            return false;
        }
    };

    function setColumnWidth(resizer, width) {
        const table = resizer.closest("table");
        const region = resizer.closest(".table-region");
        const index = Number(resizer.dataset.columnIndex);
        const minimum = Number(resizer.dataset.minWidth);
        const column = table?.querySelectorAll("col")[index];
        if (!column || !Number.isInteger(index) || !Number.isFinite(minimum) ||
            minimum <= 0 || !Number.isFinite(width)) {
            throw new Error("Invalid table column resize metadata or width.");
        }

        const constrainedWidth = Math.max(minimum, Math.round(width));
        column.style.width = `${constrainedWidth}px`;
        resizer.setAttribute("aria-valuenow", constrainedWidth.toString());
        try {
            localStorage.setItem(storagePrefix + resizer.dataset.columnKey, constrainedWidth.toString());
        } catch (error) {
            failed(region, "storage", error);
        }
    }

    function restoreColumnWidths(root = document) {
        elements(root, ".column-resizer").forEach(resizer => {
            const region = resizer.closest(".table-region");
            let savedWidth;
            try {
                const stored = localStorage.getItem(storagePrefix + resizer.dataset.columnKey);
                savedWidth = Number(stored);
                if (stored !== null && (!Number.isFinite(savedWidth) || savedWidth <= 0))
                    throw new Error("Invalid stored table column width.");
            } catch (error) {
                failed(region, "storage", error);
                return;
            }
            if (savedWidth > 0)
                browserAction(region, () => setColumnWidth(resizer, savedWidth));
        });
    }

    function initializeScrollbars(root = document) {
        elements(root, ".table-region").forEach(region => {
            const scrollbar = region.querySelector(".table-scrollbar");
            const scrollContainer = region.querySelector(".table-scroll");
            const table = scrollContainer?.querySelector(".alert-table");
            if (!scrollbar || !scrollContainer || !table || sessions.has(region)) {
                return;
            }

            const spacer = scrollbar.firstElementChild;
            let observer = null;
            let frame = null;
            const updateWidth = () => {
                spacer.style.width = `${table.scrollWidth}px`;
                scrollbar.scrollLeft = scrollContainer.scrollLeft;
            };
            let syncing = false;
            const synchronize = (source, target) => {
                if (syncing) {
                    return;
                }
                syncing = true;
                target.scrollLeft = source.scrollLeft;
                frame = requestAnimationFrame(() => { syncing = false; frame = null; });
            };
            const onScrollbar = () => browserAction(region, () => synchronize(scrollbar, scrollContainer));
            const onContainer = () => browserAction(region, () => synchronize(scrollContainer, scrollbar));
            const session = { scrollbar, scrollContainer, table, cleanup: () => {
                let released = true;
                for (const action of [
                    () => { observer?.disconnect(); observer = null; },
                    () => { if (frame !== null) cancelAnimationFrame(frame); frame = null; syncing = false; },
                    () => scrollbar.removeEventListener("scroll", onScrollbar),
                    () => scrollContainer.removeEventListener("scroll", onContainer)
                ]) {
                    if (!browserAction(region, action)) released = false;
                }
                return released;
            } };
            sessions.set(region, session);
            if (!browserAction(region, () => {
                scrollbar.addEventListener("scroll", onScrollbar);
                scrollContainer.addEventListener("scroll", onContainer);
                observer = new ResizeObserver(() => browserAction(region, updateWidth));
                observer.observe(table);
                updateWidth();
                scrollbar.dataset.initialized = "true";
                message(region, "browser", false);
            })) session.cleanup();
        });
    }

    function cleanupRemovedTables() {
        if (activeResize && !activeResize.resizer.isConnected)
            browserAction(activeResize.region, activeResize.cleanup);
        for (const [region, session] of sessions) {
            if ((!region.isConnected || !session.table.isConnected ||
                !session.scrollbar.isConnected || !session.scrollContainer.isConnected) && session.cleanup()) {
                delete session.scrollbar.dataset.initialized;
                sessions.delete(region);
            }
        }
    }

    document.addEventListener("pointerdown", event => {
        const resizer = event.target.closest?.(".column-resizer");
        if (!resizer) {
            return;
        }

        const region = resizer.closest(".table-region");
        if (activeResize && !browserAction(activeResize.region, activeResize.cleanup)) return;
        if (!browserAction(region, () => {
            event.preventDefault();
            const startX = event.clientX;
            const startWidth = resizer.parentElement.getBoundingClientRect().width;
            const resize = moveEvent => {
                if (!browserAction(region, () => setColumnWidth(resizer, startWidth + moveEvent.clientX - startX)))
                    browserAction(region, stop);
            };
            const stop = () => {
                resizer.removeEventListener("pointermove", resize);
                resizer.removeEventListener("pointerup", stop);
                resizer.removeEventListener("pointercancel", stop);
                if (resizer.hasPointerCapture(event.pointerId)) resizer.releasePointerCapture(event.pointerId);
                activeResize = null;
            };
            activeResize = { resizer, region, cleanup: stop };
            resizer.setPointerCapture(event.pointerId);
            resizer.addEventListener("pointermove", resize);
            resizer.addEventListener("pointerup", stop);
            resizer.addEventListener("pointercancel", stop);
        }) && activeResize?.resizer === resizer) browserAction(region, activeResize.cleanup);
    });

    document.addEventListener("keydown", event => {
        const resizer = event.target.closest?.(".column-resizer");
        if (!resizer || !["ArrowLeft", "ArrowRight"].includes(event.key)) {
            return;
        }

        event.preventDefault();
        const direction = event.key === "ArrowRight" ? 1 : -1;
        browserAction(resizer.closest(".table-region"),
            () => setColumnWidth(resizer, resizer.parentElement.getBoundingClientRect().width + direction * 10));
    });

    document.addEventListener("click", event => {
        const retry = event.target.closest?.("[data-table-retry]");
        if (!retry) return;
        const region = retry.closest(".table-region");
        if (activeResize?.region === region && !browserAction(region, activeResize.cleanup)) return;
        const session = sessions.get(region);
        if (session && !session.cleanup()) return;
        if (session) delete session.scrollbar.dataset.initialized;
        sessions.delete(region);
        message(region, "storage", false);
        restoreColumnWidths(region);
        initializeScrollbars(region);
    });

    restoreColumnWidths();
    initializeScrollbars();
    new MutationObserver(mutations => {
        cleanupRemovedTables();
        for (const mutation of mutations) {
            mutation.addedNodes.forEach(node => {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    restoreColumnWidths(node);
                }
            });
        }
        initializeScrollbars();
    }).observe(document.body, { childList: true, subtree: true });
})();