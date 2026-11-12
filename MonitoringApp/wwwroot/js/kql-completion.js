(() => {
    const editors = new Map();
    const dictionaryUrl = new URL("kql-completion.json?v=20261112-2", document.currentScript.src);
    let dictionary;
    let tooltipId = 0;
    const language = globalThis.kqlLanguage;

    const loadDictionary = () => dictionary ??= fetch(dictionaryUrl).then(async response => {
        if (!response.ok) throw new Error(`KQL dictionary request failed (${response.status}).`);
        if (!language) throw new Error("The KQL language analysis script could not be loaded.");
        return language.normalizeEntries(await response.json());
    });

    const completion = (input, entries, tableEntries, catalog) => language.completion({
        value: input.value, selectionStart: input.selectionStart, selectionEnd: input.selectionEnd,
        disabled: input.matches(":disabled"), readOnly: input.readOnly, maxLength: input.maxLength
    }, entries, tableEntries, catalog);

    const selectEditBoundary = (input, start, end) => {
        // A selection change separates native undo groups without changing text or focus.
        if (start === end && start > 0) input.setSelectionRange(start - 1, end);
        input.setSelectionRange(start, end);
    };

    const infoText = entry => {
        if (typeof entry?.info === "string") return entry.info;
        const language = document.documentElement.lang.toLowerCase();
        const variants = Object.entries(entry?.info ?? {});
        return variants.find(([key]) => key.toLowerCase() === language)?.[1] ??
            variants.find(([key]) => key.toLowerCase() === language.split("-")[0])?.[1] ??
            variants.find(([key]) => key.toLowerCase() === "en")?.[1] ?? "";
    };

    const attach = root => {
        const input = root.querySelector("textarea");
        const overlay = root.querySelector(".free-kql-completion");
        const error = root.querySelector("[data-kql-completion-error]");
        const tooltip = root.querySelector("[data-kql-info-tooltip]");
        const schemaError = root.querySelector("[data-kql-schema-error]");
        const hints = root.querySelector("[data-kql-hints]");
        const hintError = root.querySelector("[data-kql-diagnostics-error]");
        const keywordBrowser = root.querySelector("[data-kql-keywords]");
        const keywordToggle = root.querySelector("[data-kql-keyword-toggle]");
        const keywordPanel = root.querySelector("[data-kql-keyword-panel]");
        const keywordList = root.querySelector("[data-kql-keyword-list]");
        const keywordHelp = root.querySelector("[data-kql-keyword-help]");
        const keywordPrompt = keywordHelp?.textContent ?? "";
        if (tooltip.id) {
            input.setAttribute("aria-describedby", (input.getAttribute("aria-describedby") ?? "").split(/\s+/)
                .filter(id => id && id !== tooltip.id).join(" "));
        }
        tooltip.id = `kql-word-info-${++tooltipId}`;
        if (hints) {
            hints.id = `kql-local-hints-${tooltipId}`;
            const descriptions = new Set((input.getAttribute("aria-describedby") ?? "").split(/\s+/).filter(Boolean));
            descriptions.add(hints.id);
            input.setAttribute("aria-describedby", [...descriptions].join(" "));
        }
        root.classList.remove("free-kql-highlighted");
        const handlers = new AbortController();
        let entries = [];
        let tableEntries = [];
        let tableNames = new Set();
        let schemaReceiver = null;
        let catalogVersion = root.dataset.kqlCatalogVersion ?? "0";
        let schemaWorkspace = root.dataset.kqlSchemaWorkspace ?? "";
        const schemas = new Map();
        let infoTarget = null;
        const wordIndex = new Map();
        let keywordButtons = [];
        let suggestion = null;
        let dismissed = false;
        let composing = false;
        let disposed = false;
        const currentMetadata = () => {
            const workspace = root.dataset.kqlSchemaWorkspace ?? "";
            const version = root.dataset.kqlCatalogVersion ?? "0";
            const current = version === catalogVersion && workspace.toLowerCase() === schemaWorkspace.toLowerCase();
            return { tables: current ? tableEntries : [], catalog: {
                workspaceKey: workspace, workspaceCount: Number(root.dataset.kqlWorkspaceCount ?? "0"),
                catalogVersion: Number(version), schemas: current ? [...schemas.values()] : []
            } };
        };
        const currentCompletion = () => {
            const metadata = currentMetadata();
            return completion(input, entries, metadata.tables, metadata.catalog);
        };
        const refreshSuggestion = () => {
            suggestion = !dismissed && !composing && document.activeElement === input ? currentCompletion() : null;
        };

        const hideInfo = () => {
            infoTarget = null;
            tooltip.hidden = true;
            const descriptions = (input.getAttribute("aria-describedby") ?? "").split(/\s+/)
                .filter(id => id && id !== tooltip.id);
            if (descriptions.length) input.setAttribute("aria-describedby", descriptions.join(" "));
            else input.removeAttribute("aria-describedby");
        };
        const showInfo = (element, bounds = element.getBoundingClientRect()) => {
            const text = element.dataset.kqlInfo;
            if (!text || input.matches(":disabled") || composing) { hideInfo(); return; }
            infoTarget = { start: element.dataset.kqlStart, ghost: element.classList.contains("free-kql-completion-ghost"), bounds };
            tooltip.textContent = text;
            tooltip.hidden = false;
            const origin = root.getBoundingClientRect();
            const size = tooltip.getBoundingClientRect();
            tooltip.style.left = `${Math.max(0, Math.min(bounds.left - origin.left, root.clientWidth - size.width))}px`;
            const top = bounds.bottom + size.height + 6 <= window.innerHeight ? bounds.bottom + 6 : bounds.top - size.height - 6;
            tooltip.style.top = `${Math.max(8, Math.min(top, window.innerHeight - size.height - 8)) - origin.top}px`;
            const descriptions = new Set((input.getAttribute("aria-describedby") ?? "").split(/\s+/).filter(Boolean));
            descriptions.add(tooltip.id);
            input.setAttribute("aria-describedby", [...descriptions].join(" "));
        };
        const synchronize = () => {
            overlay.style.left = `${input.offsetLeft + input.clientLeft}px`;
            overlay.style.top = `${input.offsetTop + input.clientTop}px`;
            overlay.style.width = `${input.clientWidth}px`;
            overlay.style.height = `${input.clientHeight}px`;
            overlay.scrollTop = input.scrollTop;
            overlay.scrollLeft = input.scrollLeft;
        };
        const schemaKey = (workspace, name) => JSON.stringify([workspace.toLowerCase(), name]);
        const schemaInfo = name => {
            const schema = schemas.get(schemaKey(schemaWorkspace, name));
            if (!schema) return schemaReceiver && schemaWorkspace ? root.dataset.kqlSchemaLoading : "";
            const title = root.dataset.kqlSchemaTitle.replace(/\{([01])\}/g,
                (_, index) => index === "0" ? schema.tableName : schema.workspaceKey);
            const details = schema.status === "Ready"
                ? schema.columns.map(column => `${column.name}: ${column.type}`).join("\n")
                : ({
                    Loading: root.dataset.kqlSchemaLoading, Empty: root.dataset.kqlSchemaEmpty,
                    Unavailable: root.dataset.kqlSchemaUnavailable, Failed: root.dataset.kqlSchemaFailed
                })[schema.status];
            return `${title}\n${details}`;
        };
        let hintSignature = null;
        const renderDiagnostics = () => {
            if (!hints || disposed) return;
            if (composing) { hints.hidden = true; return; }
            try {
                const messages = JSON.parse(root.dataset.kqlDiagnosticMessages);
                const message = (key, ...values) => {
                    if (typeof messages?.[key] !== "string" || !messages[key].trim())
                        throw new Error("Missing localized KQL hint message.");
                    return messages[key].replace(/\{(\d+)\}/g, (_, index) => values[Number(index)] ?? "");
                };
                const metadata = currentMetadata();
                const report = language.diagnostics(input.value, entries, metadata.tables, metadata.catalog,
                    document.activeElement === input ? input.selectionStart : null);
                const schemaMessage = report.schemaStatus === "NotApplicable" ? "" :
                    message(({ Ready: "SchemaReady", Unknown: "SchemaPending", Loading: "SchemaPending",
                        Failed: "SchemaFailed", MultipleWorkspaces: "SchemaMultiple" })[report.schemaStatus] ?? "SchemaLimited");
                const summary = [message("Title"), message("Limited"),
                    report.completeness === "Draft" ? message("Draft") : "", schemaMessage,
                    report.truncated ? message("Truncated") : ""].filter(Boolean).join(" ");
                const rows = report.diagnostics.map(issue => {
                    const before = input.value.slice(0, issue.start).split("\n");
                    return { ...issue, text: `${message(issue.severity)}: ${message("Location", before.length, before.at(-1).length + 1)} ${message(issue.code, issue.name ?? "")}` };
                });
                const disabled = input.matches(":disabled");
                const signature = JSON.stringify([summary, rows, disabled]);
                hintError.hidden = true;
                hints.hidden = false;
                if (signature === hintSignature) return;
                hintSignature = signature;
                const fragment = document.createDocumentFragment();
                const heading = document.createElement("small");
                heading.textContent = summary;
                fragment.append(heading);
                const list = document.createElement("span");
                list.setAttribute("role", "list");
                for (const row of rows) {
                    const item = document.createElement("span");
                    item.setAttribute("role", "listitem");
                    const button = document.createElement("button");
                    button.type = "button";
                    button.className = "kql-editor-hint";
                    button.dataset.kqlSeverity = row.severity;
                    button.dataset.kqlHintStart = row.start;
                    button.dataset.kqlHintEnd = row.end;
                    button.textContent = row.text;
                    button.disabled = disabled;
                    item.append(button);
                    list.append(item);
                }
                fragment.append(list);
                hints.replaceChildren(fragment);
            } catch (exception) {
                hints.hidden = true;
                if (hintError?.hidden) console.error("Could not analyze local KQL editor hints.", exception);
                if (hintError) hintError.hidden = false;
                hintSignature = null;
            }
        };
        const render = () => {
            const fragment = document.createDocumentFragment();
            const tokens = language.identifierTokens(input.value);
            const appendRange = (start, end) => {
                let position = start;
                for (const token of tokens) {
                    if (token.start < start || token.end > end) continue;
                    fragment.append(document.createTextNode(input.value.slice(position, token.start)));
                    const element = document.createElement("span");
                    element.textContent = token.text;
                    const name = token.name ?? token.text;
                    if (tableNames.has(name)) element.className = "free-kql-table";
                    else if (wordIndex.has(token.text)) element.className = "free-kql-keyword";
                    element.dataset.kqlStart = token.start;
                    element.dataset.kqlEnd = token.end;
                    const entry = suggestion && token.end === input.selectionStart ? suggestion.entry : wordIndex.get(token.text);
                    const info = tableNames.has(name)
                        ? schemaInfo(name)
                        : infoText(entry);
                    if (info) element.dataset.kqlInfo = info;
                    fragment.append(element);
                    position = token.end;
                }
                fragment.append(document.createTextNode(input.value.slice(position, end)));
            };
            if (suggestion && suggestion.replaceStart < input.selectionStart) {
                appendRange(0, suggestion.replaceStart);
                const replaced = document.createElement("span");
                replaced.style.visibility = "hidden";
                replaced.textContent = input.value.slice(suggestion.replaceStart, input.selectionStart);
                fragment.append(replaced);
                appendRange(input.selectionStart, input.value.length);
            } else appendRange(0, input.value.length);
            fragment.append(document.createTextNode("\n"));
            if (suggestion) {
                // Keep the preview out of text flow so a wrapped suffix never shifts later query lines.
                const preview = document.createElement("span");
                preview.className = "free-kql-preview";
                preview.append(document.createTextNode(input.value.slice(0, suggestion.replaceStart)));
                const ghost = document.createElement("span");
                ghost.className = "free-kql-completion-ghost";
                ghost.textContent = suggestion.suffix;
                const info = infoText(suggestion.entry) || infoText(wordIndex.get(suggestion.entry.word));
                if (info) ghost.dataset.kqlInfo = info;
                preview.append(ghost);
                fragment.append(preview);
            }
            overlay.replaceChildren(fragment);
            synchronize();
            const lineEnd = suggestion ? input.value.indexOf("\n", input.selectionStart) : -1;
            if (lineEnd >= 0) {
                // A wrapped preview must not cover the next existing query line.
                const walker = document.createTreeWalker(overlay, NodeFilter.SHOW_TEXT);
                let offset = lineEnd;
                let node;
                while ((node = walker.nextNode())) {
                    if (offset >= node.length) { offset -= node.length; continue; }
                    const range = document.createRange();
                    range.setStart(node, offset);
                    range.setEnd(node, offset + 1);
                    overlay.querySelector(".free-kql-preview").style.maxHeight =
                        `${range.getBoundingClientRect().bottom - overlay.getBoundingClientRect().top + overlay.scrollTop}px`;
                    break;
                }
            }
            loadSchemas(tokens);
            renderDiagnostics();
        };
        const setSchemaInfo = (key, schema) => {
            schemas.set(key, schema);
            const active = infoTarget;
            refreshSuggestion();
            render();
            if (active) {
                const element = active.ghost ? overlay.querySelector(".free-kql-completion-ghost")
                    : [...overlay.querySelectorAll("[data-kql-start]")]
                        .find(item => item.dataset.kqlStart === active.start);
                if (element) showInfo(element, active.bounds);
                else hideInfo();
            }
        };
        const loadSchemas = tokens => {
            if (!schemaReceiver || !schemaWorkspace || composing || disposed) return;
            const receiver = schemaReceiver;
            const version = catalogVersion;
            const workspace = schemaWorkspace;
            const current = () => !disposed && version === catalogVersion && receiver === schemaReceiver &&
                workspace.toLowerCase() === schemaWorkspace.toLowerCase() && root.dataset.kqlCatalogVersion === version;
            for (const name of new Set(tokens.map(token => token.name ?? token.text).filter(name => tableNames.has(name)))) {
                const key = schemaKey(workspace, name);
                if (schemas.has(key)) continue;
                const state = status => ({
                    workspaceKey: workspace, tableName: name, catalogVersion: Number(version), status, columns: []
                });
                schemas.set(key, state("Loading"));
                Promise.resolve().then(() => receiver.invokeMethodAsync("GetTableSchemaAsync", name, Number(version)))
                    .then(schema => {
                        if (!current() || schema === null) return;
                        const normalized = language.normalizeTableSchema(schema, {
                            workspaceKey: workspace, tableName: name, catalogVersion: Number(version)
                        });
                        if (normalized.status === "Failed" && schemaError) schemaError.hidden = false;
                        setSchemaInfo(key, normalized);
                    }).catch(exception => {
                        if (!current()) return;
                        if (schemaError) schemaError.hidden = false;
                        console.error("Could not request the KQL table schema.", exception);
                        setSchemaInfo(key, state("Failed"));
                    });
            }
        };
        const closeKeywords = (restoreFocus = false) => {
            if (!keywordPanel) return;
            keywordPanel.hidden = true;
            keywordToggle.setAttribute("aria-expanded", "false");
            if (restoreFocus && !keywordToggle.disabled) keywordToggle.focus();
        };
        const update = () => {
            hideInfo();
            if (keywordToggle) {
                keywordToggle.disabled = entries.length === 0 || input.matches(":disabled");
                if (keywordToggle.disabled) closeKeywords();
            }
            refreshSuggestion();
            render();
        };
        const readTables = () => {
            const version = root.dataset.kqlCatalogVersion ?? "0";
            const workspace = root.dataset.kqlSchemaWorkspace ?? "";
            if (version !== catalogVersion || workspace.toLowerCase() !== schemaWorkspace.toLowerCase()) {
                catalogVersion = version;
                schemas.clear();
                if (schemaError) schemaError.hidden = true;
            }
            schemaWorkspace = workspace;
            try {
                const names = JSON.parse(root.dataset.kqlTables ?? "[]");
                if (!Array.isArray(names) || names.some(name => typeof name !== "string" || !name.trim())) {
                    throw new Error("The KQL table catalog must contain non-empty table names.");
                }
                tableNames = new Set(names);
                tableEntries = [...tableNames].map(word => ({ word }));
            } catch (exception) {
                tableNames = new Set();
                tableEntries = [];
                error.hidden = false;
                console.error("Could not read the KQL workspace table catalog.", exception);
            }
        };
        readTables();
        const changed = () => { dismissed = false; update(); };
        const listen = (element, name, handler) =>
            element.addEventListener(name, handler, { signal: handlers.signal });

        const renderKeywords = () => {
            if (!keywordList) return;
            const fragment = document.createDocumentFragment();
            const words = [...wordIndex.values()].sort((left, right) => left.word.localeCompare(right.word));
            const showKeyword = (entry, button) => {
                for (const item of keywordButtons) {
                    item.tabIndex = item === button ? 0 : -1;
                    item.classList.remove("kql-keyword-active");
                }
                button.classList.add("kql-keyword-active");
                keywordHelp.textContent = `${entry.word}\n${infoText(entry) || root.dataset.kqlKeywordNoInfo}`;
            };
            keywordButtons = words.map((entry, index) => {
                const button = document.createElement("button");
                button.type = "button";
                button.textContent = entry.word;
                button.tabIndex = index === 0 ? 0 : -1;
                listen(button, "mouseenter", () => showKeyword(entry, button));
                listen(button, "focus", () => showKeyword(entry, button));
                listen(button, "click", () => showKeyword(entry, button));
                listen(button, "keydown", event => {
                    const target = ({
                        ArrowDown: Math.min(words.length - 1, index + 1),
                        ArrowUp: Math.max(0, index - 1), Home: 0, End: words.length - 1
                    })[event.key];
                    if (target === undefined || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
                    event.preventDefault();
                    keywordButtons[target].focus();
                });
                fragment.append(button);
                return button;
            });
            keywordList.replaceChildren(fragment);
        };
        if (keywordToggle) {
            listen(keywordToggle, "click", () => {
                if (keywordToggle.disabled) return;
                const open = keywordPanel.hidden;
                keywordPanel.hidden = !open;
                keywordToggle.setAttribute("aria-expanded", String(open));
            });
            listen(keywordToggle, "keydown", event => {
                if (event.key !== "ArrowDown" || keywordToggle.disabled || event.altKey || event.ctrlKey || event.metaKey) return;
                event.preventDefault();
                keywordPanel.hidden = false;
                keywordToggle.setAttribute("aria-expanded", "true");
                keywordButtons.find(button => button.tabIndex === 0)?.focus();
            });
            listen(keywordBrowser, "keydown", event => {
                if (event.key !== "Escape" || keywordPanel.hidden) return;
                event.preventDefault();
                closeKeywords(true);
            });
            listen(document, "click", event => {
                if (!keywordPanel.hidden && !keywordBrowser.contains(event.target)) closeKeywords();
            });
        }
        if (hints) listen(hints, "click", event => {
            const button = event.target.closest("[data-kql-hint-start]");
            if (!button || input.matches(":disabled")) return;
            input.focus();
            input.setSelectionRange(Number(button.dataset.kqlHintStart), Number(button.dataset.kqlHintEnd));
            update();
        });
        listen(input, "input", changed);
        listen(input, "change", changed);
        listen(input, "focus", changed);
        listen(input, "blur", update);
        listen(input, "click", changed);
        listen(input, "mousemove", event => {
            const viewport = input.getBoundingClientRect();
            if (event.clientX < viewport.left + input.clientLeft ||
                event.clientX >= viewport.left + input.clientLeft + input.clientWidth ||
                event.clientY < viewport.top + input.clientTop ||
                event.clientY >= viewport.top + input.clientTop + input.clientHeight) { hideInfo(); return; }
            for (const element of overlay.querySelectorAll("[data-kql-info]")) {
                for (const bounds of element.getClientRects()) {
                    if (event.clientX >= bounds.left && event.clientX <= bounds.right &&
                        event.clientY >= bounds.top && event.clientY <= bounds.bottom) {
                        showInfo(element, bounds);
                        return;
                    }
                }
            }
            hideInfo();
        });
        listen(input, "mouseleave", event => {
            if (!(event.relatedTarget instanceof Node) || !tooltip.contains(event.relatedTarget)) hideInfo();
        });
        listen(tooltip, "mouseleave", hideInfo);
        listen(input, "keyup", event => {
            if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown"].includes(event.key)) changed();
        });
        listen(document, "selectionchange", () => {
            if (document.activeElement === input) update();
        });
        listen(input, "scroll", () => { hideInfo(); synchronize(); });
        listen(input, "compositionstart", () => { composing = true; update(); });
        listen(input, "compositionend", () => { composing = false; changed(); });
        listen(input, "keydown", event => {
            if (event.isComposing || composing) return;
            if (event.key === "F1") {
                const element = [...overlay.querySelectorAll("[data-kql-info][data-kql-start]")].find(item =>
                    Number(item.dataset.kqlStart) <= input.selectionStart && Number(item.dataset.kqlEnd) >= input.selectionStart) ??
                    overlay.querySelector(".free-kql-completion-ghost[data-kql-info]");
                if (element) { event.preventDefault(); showInfo(element); }
            } else if (event.key === "Escape" && (suggestion || !tooltip.hidden)) {
                event.preventDefault();
                dismissed = true;
                update();
            } else if (event.key === "Tab" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
                const current = currentCompletion();
                if (!suggestion || !current || current.suffix !== suggestion.suffix ||
                    current.entry.word !== suggestion.entry.word || current.entry.kind !== suggestion.entry.kind ||
                    current.replaceStart !== suggestion.replaceStart) {
                    suggestion = null;
                    render();
                    return;
                }
                const start = input.selectionStart, end = input.selectionEnd;
                const expected = input.value.slice(0, current.replaceStart) + current.suffix + input.value.slice(end);
                let accepted = false;
                try {
                    selectEditBoundary(input, current.replaceStart, end);
                    // Unlike setRangeText, insertText participates in the browser's native undo history.
                    accepted = typeof document.execCommand === "function" &&
                        document.execCommand("insertText", false, current.suffix) && input.value === expected;
                } catch (exception) {
                    console.error("Could not insert the KQL completion into the native edit history.", exception);
                }
                if (!accepted) {
                    input.setSelectionRange(start, end);
                    error.hidden = false;
                    console.error("The browser could not accept the KQL completion with native undo support.");
                    dismissed = true;
                    update();
                    return;
                }
                event.preventDefault();
                selectEditBoundary(input, input.selectionStart, input.selectionEnd);
                error.hidden = true;
                dismissed = true;
                update();
                // Blazor's existing onchange binding must receive the accepted text even before blur.
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }
        });
        const resize = new ResizeObserver(() => { hideInfo(); synchronize(); });
        resize.observe(input);
        const attributes = new MutationObserver(records => {
            if (records.some(record => ["data-kql-tables", "data-kql-catalog-version", "data-kql-schema-workspace"]
                .includes(record.attributeName))) readTables();
            update();
        });
        attributes.observe(root, { attributes: true,
            attributeFilter: ["data-kql-version", "data-kql-tables", "data-kql-catalog-version", "data-kql-schema-workspace", "data-kql-workspace-count"] });
        attributes.observe(input, { attributes: true, attributeFilter: ["disabled", "readonly"] });
        for (let ancestor = root.parentElement; ancestor; ancestor = ancestor.parentElement) {
            if (ancestor.tagName === "FIELDSET") {
                attributes.observe(ancestor, { attributes: true, attributeFilter: ["disabled"] });
            }
        }
        editors.set(root, { connect: receiver => {
            schemaReceiver = receiver;
            schemas.clear();
            update();
        }, dispose: () => {
            disposed = true;
            schemaReceiver = null;
            handlers.abort();
            resize.disconnect();
            attributes.disconnect();
            hideInfo();
            closeKeywords();
            if (keywordToggle) {
                keywordToggle.disabled = true;
                keywordList.replaceChildren();
                keywordHelp.textContent = keywordPrompt;
                keywordButtons = [];
            }
            root.classList.remove("free-kql-highlighted");
            overlay.replaceChildren();
            if (hints) {
                hints.replaceChildren();
                hints.hidden = true;
                input.setAttribute("aria-describedby", (input.getAttribute("aria-describedby") ?? "").split(/\s+/)
                    .filter(id => id && id !== hints.id).join(" "));
            }
        } });
        loadDictionary().then(result => {
            if (disposed) return;
            entries = result;
            const indexEntries = items => {
                for (const entry of items) {
                    if (!wordIndex.has(entry.word) || (!wordIndex.get(entry.word).info && entry.info)) wordIndex.set(entry.word, entry);
                    if (entry.next) indexEntries(entry.next);
                }
            };
            indexEntries(entries);
            renderKeywords();
            update();
            root.classList.add("free-kql-highlighted");
        }).catch(exception => {
            if (disposed) return;
            error.hidden = false;
            console.error("Could not initialize KQL completion.", exception);
        });
    };

    const scan = () => {
        for (const [root, { dispose }] of editors) {
            if (!root.isConnected) { dispose(); editors.delete(root); }
        }
        document.querySelectorAll("[data-kql-editor]").forEach(root => {
            if (!editors.has(root)) attach(root);
        });
    };
    window.kqlCompletion = {
        connect: (root, receiver) => {
            if (!root?.isConnected) throw new Error("Cannot connect schema help to a missing KQL editor.");
            if (!editors.has(root)) attach(root);
            editors.get(root).connect(receiver);
        }
    };
    scan();
    new MutationObserver(records => {
        if (records.some(record => [...record.addedNodes, ...record.removedNodes]
            .some(node => node instanceof Element && !node.closest(".free-kql-completion")))) scan();
    }).observe(document.body, { childList: true, subtree: true });
    document.addEventListener("enhancedload", scan);
})();
