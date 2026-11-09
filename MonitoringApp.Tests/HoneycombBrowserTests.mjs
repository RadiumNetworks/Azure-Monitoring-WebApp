import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../MonitoringApp/wwwroot/js/analysis-honeycomb.js", import.meta.url), "utf8");

function fixture({ fail, count = 3, width = 501, height = 190, roots = 1 } = {}) {
    const logs = [];
    const observers = [];
    const frames = [];
    const failures = Object.create(null);
    const document = {
        body: {}, listeners: new Map(), roots: [],
        addEventListener(name, listener) { this.listeners.set(name, listener); },
        querySelectorAll() { return this.roots; },
        click(target) { this.listeners.get("click")({ target }); }
    };
    function addRoot() {
        const root = { isConnected: true, failures: {}, controls: [], querySelectorAll: () => root.controls };
        const error = { hidden: true, textContent: "Localized honeycomb error" };
        const output = { textContent: "100%" };
        function makeButton(action, retry = false) {
            const buttonObject = { disabled: true, hidden: retry, dataset: { honeycombZoom: action } };
            buttonObject.closest = selector => selector === "[data-analysis-honeycomb]" ? root :
                selector === (retry ? "[data-honeycomb-retry]" : "[data-honeycomb-zoom]") ? buttonObject : null;
            return buttonObject;
        }
        const out = makeButton("out");
        const zoomIn = makeButton("in");
        const fit = makeButton("fit");
        const retry = makeButton(undefined, true);
        root.controls = [out, zoomIn, fit];
        const cells = Array.from({ length: count }, (_, index) => ({
            attributes: {}, textContent: `Original <result> ${index}`,
            setAttribute(name, value) { this.attributes[name] = value; }
        }));
        const svg = {
            attributes: {}, style: {}, querySelectorAll: () => cells,
            setAttribute(name, value) { this.attributes[name] = value; }
        };
        const viewport = () => ({
            root, clientWidth: width, clientHeight: height, scrollTop: 30, scrollLeft: 40,
            querySelector() { if (root.failures.layout) throw root.failures.layout; return svg; }
        });
        root.viewport = viewport();
        root.querySelector = selector => ({
            ".analysis-honeycomb-viewport": root.viewport, output,
            '[data-honeycomb-zoom="out"]': out, '[data-honeycomb-zoom="in"]': zoomIn,
            '[data-honeycomb-zoom="fit"]': fit,
            "[data-honeycomb-error]": root.missingNotices ? null : error,
            "[data-honeycomb-retry]": root.missingNotices ? null : retry
        })[selector];
        Object.assign(root, { error, output, out, zoomIn, fit, retry, cells, svg,
            replaceViewport() { const old = root.viewport; root.viewport = viewport(); return old; } });
        document.roots.push(root);
        return root;
    }
    const root = addRoot();
    if (fail) root.failures[fail] = new Error("PRIVATE_BROWSER_DETAIL");
    for (let index = 1; index < roots; index++) addRoot();
    let mutation;
    const context = {
        document, console: { error: (...arguments_) => logs.push(arguments_) },
        requestAnimationFrame(callback) {
            if (failures.frame) throw failures.frame;
            frames.push(callback);
        },
        ResizeObserver: class {
            constructor(callback) {
                if (failures.constructor) throw failures.constructor;
                this.callback = callback; observers.push(this); this.disconnected = false;
            }
            observe(viewport) {
                this.viewport = viewport;
                if (viewport.root.failures.observe) throw viewport.root.failures.observe;
            }
            disconnect() {
                if (this.viewport?.root.failures.disconnect) throw this.viewport.root.failures.disconnect;
                this.disconnected = true;
            }
            fire() { this.callback(); }
        },
        MutationObserver: class {
            constructor(callback) { mutation = callback; }
            observe() {}
        }
    };
    return { root, document, logs, observers, frames, failures, addRoot,
        start() { vm.runInNewContext(source, context, { filename: "analysis-honeycomb.js" }); },
        mutate() { mutation(); },
        flush() { for (const callback of frames.splice(0)) callback(); },
        click(button) { document.click(button); } };
}

test("normal layout preserves exact geometry and original result labels", () => {
    const item = fixture();
    item.start();
    assert.equal(item.root.svg.attributes.viewBox, "0 0 358 189");
    assert.deepEqual(item.root.svg.style, { width: "358px", height: "189px" });
    assert.deepEqual(item.root.cells.map(cell => cell.attributes.transform),
        ["translate(74 64)", "translate(179 125)", "translate(284 64)"]);
    assert.deepEqual(item.root.cells.map(cell => cell.textContent),
        ["Original <result> 0", "Original <result> 1", "Original <result> 2"]);
    assert.equal(item.root.error.hidden, true);
    assert.equal(item.root.retry.hidden, true);
    assert.equal(item.root.fit.disabled, false);
});

test("zoom retains exact quarter steps, 50%-200% bounds and button limits", () => {
    const item = fixture();
    item.start();
    item.click(item.root.zoomIn);
    assert.equal(item.root.output.textContent, "125%");
    assert.deepEqual(item.root.svg.style, { width: "447.5px", height: "236.25px" });
    for (let index = 0; index < 10; index++) item.click(item.root.zoomIn);
    assert.equal(item.root.output.textContent, "200%");
    assert.equal(item.root.zoomIn.disabled, true);
    for (let index = 0; index < 10; index++) item.click(item.root.out);
    assert.equal(item.root.output.textContent, "50%");
    assert.equal(item.root.out.disabled, true);
    assert.equal(item.root.zoomIn.disabled, false);
});

test("fit retains zoom reset and both scroll offsets", () => {
    const item = fixture();
    item.start();
    item.click(item.root.zoomIn);
    item.root.viewport.scrollTop = 80;
    item.root.viewport.scrollLeft = 90;
    item.click(item.root.fit);
    assert.equal(item.root.output.textContent, "100%");
    assert.equal(item.root.viewport.scrollTop, 0);
    assert.equal(item.root.viewport.scrollLeft, 0);
});

for (const options of [{ width: 0 }, { count: 0 }]) {
    test(`unmeasured/empty view ${JSON.stringify(options)} waits normally without a browser error`, () => {
        const item = fixture(options);
        item.start();
        assert.equal(item.root.error.hidden, true);
        assert.deepEqual(item.root.svg.attributes, {});
        assert.deepEqual(item.logs, []);
    });
}

for (const phase of ["observe", "layout"]) {
    test(`${phase} initialization failure is localized, cleans partial observer and retries only explicitly`, () => {
        const item = fixture({ fail: phase });
        item.start();
        assert.equal(item.root.error.hidden, false);
        assert.equal(item.root.error.textContent, "Localized honeycomb error");
        assert.equal(item.root.retry.hidden, false);
        assert.equal(item.observers[0].disconnected, true);
        assert.equal(item.logs[0][1], item.root.failures[phase]);
        item.mutate(); item.flush();
        assert.equal(item.observers.length, 1);
        delete item.root.failures[phase];
        item.click(item.root.retry);
        assert.equal(item.observers.length, 2);
        assert.equal(item.root.error.hidden, true);
        assert.equal(item.root.fit.disabled, false);
    });
}

test("observer constructor failure still owns retry state and can explicitly recover", () => {
    const item = fixture();
    item.failures.constructor = new Error("PRIVATE_CONSTRUCTOR_DETAIL");
    item.start();
    assert.equal(item.root.error.hidden, false);
    assert.equal(item.observers.length, 0);
    delete item.failures.constructor;
    item.click(item.root.retry);
    assert.equal(item.observers.length, 1);
    assert.equal(item.root.error.hidden, true);
});

test("failed zoom never confirms the new zoom and does not auto-retry through observer or mutation", () => {
    const item = fixture();
    item.start();
    item.click(item.root.zoomIn);
    const failure = new Error("PRIVATE_LAYOUT_DETAIL");
    item.root.failures.layout = failure;
    item.click(item.root.zoomIn);
    assert.equal(item.root.output.textContent, "125%");
    assert.equal(item.root.error.hidden, false);
    assert.equal(item.root.controls.every(button => button.disabled), true);
    const logCount = item.logs.length;
    item.observers[0].fire(); item.mutate(); item.flush();
    item.click(item.root.fit);
    assert.equal(item.logs.length, logCount);
    delete item.root.failures.layout;
    item.click(item.root.retry);
    assert.equal(item.root.output.textContent, "125%");
    assert.equal(item.observers[0].disconnected, true);
    assert.equal(item.root.error.hidden, true);
});

test("resize callback failures are isolated from another healthy view", () => {
    const item = fixture({ roots: 2 });
    item.start();
    item.root.failures.layout = new Error("PRIVATE_RESIZE_DETAIL");
    item.observers[0].fire();
    assert.equal(item.root.error.hidden, false);
    item.click(item.document.roots[1].zoomIn);
    assert.equal(item.document.roots[1].output.textContent, "125%");
    assert.equal(item.document.roots[1].error.hidden, true);
});

test("a failed initial view does not abort setup of later healthy views", () => {
    const item = fixture({ fail: "observe", roots: 2 });
    item.start();
    assert.equal(item.observers.length, 2);
    assert.equal(item.root.error.hidden, false);
    assert.equal(item.document.roots[1].error.hidden, true);
    assert.equal(item.document.roots[1].fit.disabled, false);
});

test("cleanup failure blocks a replacement observer until explicit successful cleanup", () => {
    const item = fixture();
    item.start();
    item.root.failures.disconnect = new Error("PRIVATE_CLEANUP_DETAIL");
    item.click(item.root.retry);
    assert.equal(item.root.error.hidden, false);
    assert.equal(item.observers.length, 1);
    delete item.root.failures.disconnect;
    item.click(item.root.retry);
    assert.equal(item.observers[0].disconnected, true);
    assert.equal(item.observers.length, 2);
});

test("removed view disconnects immediately without waiting for RAF and ignores late callbacks", () => {
    const item = fixture();
    item.start();
    const before = JSON.stringify(item.root.svg);
    item.root.isConnected = false;
    item.document.roots = [];
    item.mutate();
    assert.equal(item.observers[0].disconnected, true);
    item.observers[0].fire();
    assert.equal(JSON.stringify(item.root.svg), before);
    item.flush();
    assert.equal(item.observers.length, 1);
});

test("connected-root viewport replacement rebinds its observer, keeps zoom and ignores stale callbacks", () => {
    const item = fixture();
    item.start();
    item.click(item.root.zoomIn);
    const old = item.root.replaceViewport();
    item.mutate(); item.flush();
    assert.equal(item.observers[0].disconnected, true);
    assert.equal(item.observers[1].viewport, item.root.viewport);
    assert.notEqual(old, item.root.viewport);
    assert.equal(item.root.output.textContent, "125%");
    item.root.failures.layout = new Error("PRIVATE_STALE_CALLBACK_DETAIL");
    item.observers[0].fire();
    assert.equal(item.root.error.hidden, true);
    assert.deepEqual(item.logs, []);
});

test("RAF failure is visible, resets pending state and allows explicit recovery without RAF", () => {
    const item = fixture();
    item.start();
    item.failures.frame = new Error("PRIVATE_FRAME_DETAIL");
    item.mutate();
    assert.equal(item.root.error.hidden, false);
    item.click(item.root.retry);
    assert.equal(item.root.error.hidden, true);
    delete item.failures.frame;
    item.mutate(); item.flush();
    assert.equal(item.root.error.hidden, true);
});

test("multiple DOM mutations coalesce to one frame and never duplicate an observer", () => {
    const item = fixture();
    item.start();
    item.mutate(); item.mutate(); item.mutate();
    assert.equal(item.frames.length, 1);
    item.flush();
    assert.equal(item.observers.length, 1);
});

test("unknown action is rejected explicitly rather than silently treated as zoom out", () => {
    const item = fixture();
    item.start();
    item.root.zoomIn.dataset.honeycombZoom = "unknown";
    item.click(item.root.zoomIn);
    assert.equal(item.root.error.hidden, false);
    assert.equal(item.root.output.textContent, "100%");
    assert.equal(item.logs.length, 1);
});

test("cleanup on a removed root without notice markup still logs and does not abort other views", () => {
    const item = fixture({ roots: 2 });
    item.start();
    item.root.missingNotices = true;
    item.root.isConnected = false;
    item.root.failures.disconnect = new Error("PRIVATE_REMOVED_CLEANUP_DETAIL");
    item.document.roots = [item.document.roots[1]];
    item.mutate(); item.flush();
    assert.equal(item.logs[0][1], item.root.failures.disconnect);
    item.click(item.document.roots[0].zoomIn);
    assert.equal(item.document.roots[0].output.textContent, "125%");
});
