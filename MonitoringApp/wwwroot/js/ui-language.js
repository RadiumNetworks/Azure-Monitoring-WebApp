(() => {
    const original = new WeakMap();
    const edited = new Set();
    const controls = 'input:not([type="hidden"]):not([type="submit"]):not([type="button"]), textarea, select';
    const isEditor = element => element instanceof Element &&
        element.matches(controls) && !element.closest("[data-language-selector]");
    const value = element => element.type === "checkbox" || element.type === "radio"
        ? element.checked : element instanceof HTMLSelectElement && element.multiple
            ? JSON.stringify(Array.from(element.selectedOptions, option => option.value)) : element.value;
    const remember = element => {
        if (isEditor(element) && !original.has(element)) original.set(element, value(element));
    };
    const scan = (root, initial = false) => {
        if (root instanceof Element) remember(root);
        root.querySelectorAll(controls).forEach(element => {
            remember(element);
            if (!initial || !isEditor(element)) return;
            const baseline = element.type === "checkbox" || element.type === "radio"
                ? element.defaultChecked : element instanceof HTMLSelectElement
                    ? element.multiple
                        ? JSON.stringify(Array.from(element.options).filter(option => option.defaultSelected).map(option => option.value))
                        : (Array.from(element.options).find(option => option.defaultSelected) ?? element.options[0])?.value
                    : element.defaultValue;
            original.set(element, baseline);
            if (value(element) !== baseline) edited.add(element);
        });
    };

    scan(document, true);
    document.addEventListener("focusin", event => remember(event.target), true);
    for (const name of ["input", "change"]) {
        document.addEventListener(name, event => {
            if (!isEditor(event.target)) return;
            remember(event.target);
            if (value(event.target) === original.get(event.target)) edited.delete(event.target);
            else edited.add(event.target);
        }, true);
    }
    // Never infer that a Blazor submit succeeded; only an explicit reset discards the edits.
    document.addEventListener("reset", event => {
        setTimeout(() => {
            if (event.defaultPrevented) return;
            event.target.querySelectorAll(controls).forEach(element => {
                original.set(element, value(element));
                edited.delete(element);
            });
        }, 0);
    }, true);

    const initialize = root => {
        root.querySelectorAll("[data-language-selector]").forEach(selector => {
            if (selector.dataset.languageReady) return;
            const form = selector.querySelector("[data-language-form]");
            const select = form.elements.namedItem("Culture");
            const dialog = selector.querySelector("dialog");
            const cancel = selector.querySelector("[data-language-cancel]");
            const proceed = selector.querySelector("[data-language-continue]");
            const apply = form.querySelector('button[type="submit"]');
            const errors = selector.querySelectorAll("[data-language-error]");
            if (typeof dialog.showModal !== "function" || typeof dialog.close !== "function") {
                selector.querySelector("[data-language-unavailable]").hidden = false;
                return;
            }
            let submitting = false;
            const browserAction = action => {
                if (submitting) return;
                errors.forEach(message => { message.hidden = true; });
                try {
                    action();
                } catch (error) {
                    submitting = false;
                    apply.disabled = false;
                    proceed.disabled = false;
                    errors.forEach(message => { message.hidden = Boolean(message.closest("dialog")) !== dialog.open; });
                    console.error("UI language browser action could not be confirmed.", error);
                }
            };
            const resetSelection = () => { select.value = selector.dataset.currentCulture; };
            const submit = () => {
                form.elements.namedItem("ReturnUrl").value =
                    location.pathname + location.search + location.hash;
                submitting = true;
                apply.disabled = true;
                proceed.disabled = true;
                HTMLFormElement.prototype.submit.call(form);
            };
            form.addEventListener("submit", event => {
                event.preventDefault();
                browserAction(() => {
                    if (select.value === selector.dataset.currentCulture) return;
                    const dirty = Array.from(edited).some(element =>
                        element.isConnected && value(element) !== original.get(element));
                    if (dirty || document.querySelector('[data-language-dirty="true"]')) {
                        dialog.showModal();
                        cancel.focus();
                    } else submit();
                });
            });
            cancel.addEventListener("click", () => browserAction(() => { resetSelection(); dialog.close(); }));
            dialog.addEventListener("cancel", () => browserAction(resetSelection));
            dialog.addEventListener("keydown", event => {
                if (event.key !== "Escape") return;
                event.preventDefault();
                browserAction(() => { resetSelection(); dialog.close(); });
            });
            proceed.addEventListener("click", () => browserAction(() => { dialog.close(); submit(); }));
            selector.dataset.languageReady = "true";
            selector.querySelector("[data-language-unavailable]").hidden = true;
            select.disabled = false;
            apply.disabled = false;
        });
    };
    initialize(document);
    new MutationObserver(records => {
        for (const record of records) {
            record.addedNodes.forEach(node => {
                if (node instanceof Element) scan(node);
            });
        }
        initialize(document);
        for (const element of edited) if (!element.isConnected) edited.delete(element);
    }).observe(document.body, { childList: true, subtree: true });
    document.addEventListener("enhancedload", () => { scan(document); initialize(document); });
})();
