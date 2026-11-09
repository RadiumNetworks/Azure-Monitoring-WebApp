import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../MonitoringApp/wwwroot/js/inbox-dashboard.js", import.meta.url), "utf8");
const moduleUrl = source => `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
const inboxUrl = moduleUrl(source);
const dashboard = await import(inboxUrl);
const analysisSource = await readFile(new URL("../MonitoringApp/wwwroot/js/analysis-dashboard.js", import.meta.url), "utf8");
const analysisDashboard = await import(moduleUrl(
    analysisSource.replace('from "./inbox-dashboard.js"', `from "${inboxUrl}"`)));
globalThis.getComputedStyle = () => ({
    columnGap: "0", rowGap: "0", paddingLeft: "0", paddingRight: "0", paddingTop: "0"
});

class Target {
    listeners = new Map();
    captures = new Set();
    addEventListener(name, listener) {
        if (!this.listeners.has(name)) this.listeners.set(name, new Set());
        this.listeners.get(name).add(listener);
    }
    removeEventListener(name, listener) { this.listeners.get(name)?.delete(listener); }
    async fire(name, event) {
        for (const listener of [...(this.listeners.get(name) ?? [])]) await listener(event);
    }
    count() { return [...this.listeners.values()].reduce((sum, listeners) => sum + listeners.size, 0); }
    setPointerCapture(id) { this.captures.add(id); }
    hasPointerCapture(id) { return this.captures.has(id); }
    releasePointerCapture(id) { this.captures.delete(id); }
}

function analysisFixture() {
    const root = new Target();
    const handle = new Target();
    const classes = new Set();
    const previews = new Set();
    const panel = {
        dataset: { analysisPanel: "queries" },
        style: { width: "100%", height: "300px" },
        parentElement: { clientWidth: 1000 },
        getBoundingClientRect() {
            return { width: this.style.width.endsWith("px") ? parseFloat(this.style.width) : 400,
                height: parseFloat(this.style.height) };
        }
    };
    const grid = {
        clientWidth: 400, dataset: { columns: "4", rows: "12", rowHeight: "80" },
        getBoundingClientRect: () => ({ left: 0, top: 0 }),
        querySelectorAll: () => [],
        append: preview => previews.add(preview),
        contains: target => target === grid || previews.has(target),
        closest: selector => selector === "[data-inbox-dashboard-grid]" ? grid : null
    };
    const card = {
        dataset: { analysisQueryId: "Original<query>", columnSpan: "2", rowSpan: "2" },
        closest: selector => selector === '[data-analysis-query-id][draggable="true"]' ? card : null
    };
    root.classList = { add: name => classes.add(name), remove: name => classes.delete(name) };
    root.contains = target => [handle, grid, card].includes(target);
    root.querySelector = selector => selector === "[data-inbox-dashboard-grid]" ? grid : null;
    handle.closest = selector => selector === "[data-analysis-panel-resize]" ? handle :
        selector === "[data-analysis-panel]" ? panel : null;
    globalThis.document = {
        createElement: () => {
            const preview = { style: {}, attributes: {}, setAttribute(name, value) { this.attributes[name] = value; },
                remove() { previews.delete(preview); } };
            return preview;
        }
    };
    const calls = [];
    const reference = { invokeMethodAsync: async (...arguments_) => calls.push(arguments_) };
    const event = { target: handle, button: 0, pointerId: 7, clientX: 10, clientY: 10,
        preventDefault() {}, stopPropagation() {} };
    analysisDashboard.initialize(reference, root);
    return { root, handle, panel, grid, card, classes, previews, calls, reference, event };
}

test("analysis panel: dispose releases capture, restores relative styles and prevents late commits", async () => {
    const item = analysisFixture();
    const original = { ...item.panel.style };
    await item.root.fire("pointerdown", item.event);
    await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
    assert.deepEqual(item.panel.style, { width: "500px", height: "380px" });
    analysisDashboard.dispose(item.root);
    analysisDashboard.dispose(item.root);
    assert.equal(item.root.count(), 0);
    assert.equal(item.handle.count(), 0);
    assert.equal(item.handle.hasPointerCapture(7), false);
    assert.deepEqual(item.panel.style, original);
    await item.handle.fire("pointerup", item.event);
    await item.root.fire("keydown", { target: item.handle, key: "ArrowRight", preventDefault() {} });
    assert.deepEqual(item.calls, []);
});

for (const completion of ["pointerup", "pointercancel"]) {
    test(`analysis panel: ${completion} preserves commit/cancel semantics and releases capture`, async () => {
        const item = analysisFixture();
        const original = { ...item.panel.style };
        await item.root.fire("pointerdown", item.event);
        await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
        await item.handle.fire(completion, item.event);
        assert.deepEqual(item.calls, completion === "pointerup"
            ? [["CommitAnalysisPanelResize", "queries", 500, 380]] : []);
        assert.deepEqual(item.panel.style, completion === "pointerup"
            ? { width: "500px", height: "380px" } : original);
        assert.equal(item.handle.count(), 0);
        assert.equal(item.handle.hasPointerCapture(7), false);
        analysisDashboard.dispose(item.root);
    });
}

test("analysis panel: a replacement gesture restores the prior preview before measuring and recapturing", async () => {
    const item = analysisFixture();
    await item.root.fire("pointerdown", item.event);
    await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
    await item.root.fire("pointerdown", { ...item.event, clientX: 110, clientY: 90 });
    assert.deepEqual(item.panel.style, { width: "100%", height: "300px" });
    assert.equal(item.handle.hasPointerCapture(7), true);
    assert.equal(item.handle.count(), 3);
    await item.handle.fire("pointermove", { clientX: 160, clientY: 130 });
    await item.handle.fire("pointerup", item.event);
    assert.deepEqual(item.calls, [["CommitAnalysisPanelResize", "queries", 450, 340]]);
    assert.equal(item.handle.hasPointerCapture(7), false);
    analysisDashboard.dispose(item.root);
});

test("analysis panel: reinitialization removes all old root listeners and active resize", async () => {
    const item = analysisFixture();
    const original = { ...item.panel.style };
    await item.root.fire("pointerdown", item.event);
    await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
    analysisDashboard.initialize(item.reference, item.root);
    assert.equal(item.root.count(), 8);
    assert.equal(item.handle.count(), 0);
    assert.equal(item.handle.hasPointerCapture(7), false);
    assert.deepEqual(item.panel.style, original);
    await item.root.fire("keydown", { target: item.handle, key: "ArrowRight", preventDefault() {} });
    assert.deepEqual(item.calls, [["CommitAnalysisPanelResize", "queries", 420, 300]]);
    analysisDashboard.dispose(item.root);
    assert.equal(item.root.count(), 0);
});

for (const kind of ["move", "resize"]) {
    test(`analysis ${kind}: reinitialization also cancels the shared widget gesture`, async () => {
        const item = fixture(kind);
        const original = { ...item.widget.style };
        item.root.classList = { remove() {} };
        analysisDashboard.initialize(item.reference, item.root);
        await item.root.fire("pointerdown", item.event);
        await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
        analysisDashboard.initialize(item.reference, item.root);
        assert.equal(item.root.count(), 8);
        assert.equal(item.handle.count(), 0);
        assert.equal(item.handle.hasPointerCapture(7), false);
        assert.deepEqual(item.widget.style, original);
        assert.deepEqual(item.calls, []);
        await item.root.fire("pointerdown", item.event);
        await item.handle.fire("pointerup", item.event);
        assert.equal(item.calls.length, 1);
        analysisDashboard.dispose(item.root);
        assert.equal(item.root.count(), 0);
    });
}

async function beginQueryDrag(item) {
    const transfer = { setData(type, value) {
        assert.equal(type, "application/x-analysis-query");
        assert.equal(value, "Original<query>");
    } };
    await item.root.fire("dragstart", { target: item.card, dataTransfer: transfer });
    const event = { target: item.grid, clientX: 150, clientY: 90, dataTransfer: transfer, preventDefault() {} };
    await item.root.fire("dragover", event);
    assert.equal(item.previews.size, 1);
    const preview = [...item.previews][0];
    assert.deepEqual(preview.style, { gridColumn: "2 / span 2", gridRow: "2 / span 2" });
    assert.equal(preview.attributes["aria-hidden"], "true");
    assert.equal(item.classes.has("is-query-dragging"), true);
    return event;
}

test("analysis query: a normal drop retains exact query identifier and placement once", async () => {
    const item = analysisFixture();
    const event = await beginQueryDrag(item);
    await item.root.fire("drop", event);
    assert.deepEqual(item.calls, [["PlaceDashboardQuery", "Original<query>", 2, 2]]);
    assert.equal(item.previews.size, 0);
    assert.equal(item.classes.size, 0);
    await item.root.fire("drop", event);
    assert.equal(item.calls.length, 1);
    analysisDashboard.dispose(item.root);
});

for (const key of ["Enter", " "]) {
    test(`analysis query: ${JSON.stringify(key)} retains keyboard placement exactly once`, async () => {
        const item = analysisFixture();
        await item.root.fire("keydown", { target: item.card, key, preventDefault() {} });
        assert.deepEqual(item.calls, [["PlaceDashboardQuery", "Original<query>", 1, 1]]);
        analysisDashboard.dispose(item.root);
    });
}

for (const action of ["dispose", "reinitialize"]) {
    test(`analysis query: ${action} clears the active drop preview without committing`, async () => {
        const item = analysisFixture();
        const event = await beginQueryDrag(item);
        if (action === "dispose") analysisDashboard.dispose(item.root);
        else analysisDashboard.initialize(item.reference, item.root);
        assert.equal(item.root.count(), action === "dispose" ? 0 : 8);
        assert.equal(item.previews.size, 0);
        assert.equal(item.classes.size, 0);
        await item.root.fire("drop", event);
        assert.deepEqual(item.calls, []);
        if (action === "reinitialize") {
            const next = await beginQueryDrag(item);
            await item.root.fire("drop", next);
            assert.deepEqual(item.calls, [["PlaceDashboardQuery", "Original<query>", 2, 2]]);
        }
        analysisDashboard.dispose(item.root);
    });
}
function fixture(kind) {
    const root = new Target();
    const handle = new Target();
    const classes = new Set();
    const grid = { clientWidth: 400, dataset: { columns: "4", rows: "12", rowHeight: "80" } };
    const widget = {
        dataset: { inboxWidgetId: "Original<widget>", columnSpan: "2", rowSpan: "2" },
        style: { gridColumn: "1 / span 2", gridRow: "1 / span 2", gridColumnEnd: "span 2", gridRowEnd: "span 2", transform: "" },
        classList: { add: name => classes.add(name), remove: name => classes.delete(name) },
        closest: selector => selector === "[data-inbox-dashboard-grid]" ? grid : null
    };
    root.contains = target => target === handle;
    handle.closest = selector => {
        if (selector === "[data-inbox-widget-id]") return widget;
        if (selector === "[data-inbox-resize-handle]" && kind === "resize") return handle;
        if (selector === "[data-inbox-drag-handle]" && kind === "move") return handle;
        return null;
    };
    const calls = [];
    const reference = { invokeMethodAsync: async (...arguments_) => calls.push(arguments_) };
    const event = { target: handle, button: 0, pointerId: 7, clientX: 10, clientY: 10,
        preventDefault() {}, stopPropagation() {} };
    dashboard.initialize(reference, root);
    return { root, handle, widget, classes, calls, reference, event };
}

for (const kind of ["move", "resize"]) {
    test(`${kind}: dispose cancels active gesture, restores preview and prevents callbacks`, async () => {
        const item = fixture(kind);
        const original = { ...item.widget.style };
        await item.root.fire("pointerdown", item.event);
        assert.equal(item.handle.count(), 3);
        assert.equal(item.handle.hasPointerCapture(7), true);
        await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
        assert.notDeepEqual(item.widget.style, original);
        dashboard.dispose(item.root);
        dashboard.dispose(item.root);
        assert.equal(item.root.count(), 0);
        assert.equal(item.handle.count(), 0);
        assert.equal(item.handle.hasPointerCapture(7), false);
        assert.equal(item.classes.size, 0);
        assert.deepEqual(item.widget.style, original);
        await item.handle.fire("pointermove", { clientX: 310, clientY: 310 });
        await item.handle.fire("pointerup", item.event);
        await item.handle.fire("pointercancel", item.event);
        assert.deepEqual(item.calls, []);
    });

    for (const completion of ["pointerup", "pointercancel"]) {
        test(`${kind}: ${completion} retains the existing callback and coordinates exactly once`, async () => {
            const item = fixture(kind);
            await item.root.fire("pointerdown", item.event);
            await item.handle.fire("pointermove", { clientX: 110, clientY: 90 });
            await item.handle.fire(completion, item.event);
            assert.deepEqual(item.calls, [[kind === "move" ? "CommitDashboardMove" : "CommitDashboardResize",
                "Original<widget>", kind === "move" ? 2 : 3, kind === "move" ? 2 : 3]]);
            assert.equal(item.handle.count(), 0);
            assert.equal(item.handle.hasPointerCapture(7), false);
            dashboard.dispose(item.root);
            assert.equal(item.calls.length, 1);
        });
    }

    test(`${kind}: reinitialization removes previous root and gesture listeners`, async () => {
        const item = fixture(kind);
        await item.root.fire("pointerdown", item.event);
        dashboard.initialize(item.reference, item.root);
        assert.equal(item.root.count(), 1);
        assert.equal(item.handle.count(), 0);
        assert.equal(item.handle.hasPointerCapture(7), false);
        assert.deepEqual(item.calls, []);
        await item.root.fire("pointerdown", item.event);
        await item.handle.fire("pointerup", item.event);
        assert.equal(item.calls.length, 1);
        dashboard.dispose(item.root);
    });
}
