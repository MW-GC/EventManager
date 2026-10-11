// Focus return for dialogs (Services/FocusReturn.cs). remember() keeps the focused element when a
// dialog opens; restore() puts focus back once the dialog is gone. If the element left the page (its
// row re-rendered), the element with the same data-focus-key takes focus, else the page heading.
// question() and answered() hand focus to a ConfirmDialog over a form dialog and back again.
window.emFocus = (() => {
    let element = null;
    let key = null;
    // Each restore() gets a number; a newer remember() or restore() stops an older one.
    let run = 0;
    // Where focus was in the form dialog behind the open question; asks counts question()/answered().
    let behind = null;
    let asks = 0;

    const lost = () => !document.activeElement || document.activeElement === document.body;

    function target() {
        if (element && element.isConnected) return element;
        if (key) {
            const match = document.querySelector(`[data-focus-key="${CSS.escape(key)}"]`);
            if (match) return match;
        }
        return document.querySelector("h1");
    }

    const questions = () => document.querySelectorAll("fluent-dialog.confirm-dialog");
    const forms = () => [...document.querySelectorAll("fluent-dialog:not(.confirm-dialog)")];
    const controls = "fluent-text-field, fluent-text-area, fluent-select, fluent-combobox, fluent-checkbox, fluent-switch, " +
        "fluent-radio, fluent-button, fluent-anchor, input, textarea, select, button, a[href], [tabindex]:not([tabindex='-1'])";
    const firstControl = (root) => [...root.querySelectorAll(controls)]
        .find((c) => !c.disabled && !c.hasAttribute("disabled") && c.getClientRects().length > 0);

    // Fluent UI 4.14.4's dialog reads trap-focus only when it connects: a later change of the attribute
    // never reaches updateTrapFocus() (trapFocusChanged is an instance field, so FAST does not see it as
    // a change callback). Without this, a form dialog set to trap-focus="false" still pulls focus back,
    // and against the question's own trap that loops into "Maximum call stack size exceeded".
    const syncTrap = (dialog) => dialog.updateTrapFocus?.();

    return {
        remember(k) {
            run++;
            const active = document.activeElement;
            element = active && active !== document.body ? active : null;
            key = k || null;
        },
        restore() {
            const mine = ++run;
            let tries = 0;
            let focused = null;
            // A page closes its dialog before a save finishes, and the dialog leaves the page only
            // with the next render: wait (up to 2 s) until no dialog is left. Focus moves only if it
            // was lost with the dialog, so a user who already moved on keeps their place.
            const settle = () => {
                if (mine !== run) return;
                if (document.querySelector("fluent-dialog") && tries++ < 40) {
                    setTimeout(settle, 50);
                    return;
                }
                if (!lost()) return;
                focused = target();
                focused?.focus();
                tries = 0;
                setTimeout(watch, 100);
            };
            // A save reloads the list after the dialog closed; the reload can replace the row whose
            // button just took focus. For 3 s, follow the key to the new button if that happens.
            const watch = () => {
                if (mine !== run || !focused) return;
                if (!focused.isConnected && lost()) {
                    focused = target();
                    focused?.focus();
                }
                if (tries++ < 30) setTimeout(watch, 100);
            };
            setTimeout(settle, 0);
        },
        // A question has rendered. The form dialog behind it (already trap-focus="false") lets go of
        // focus, and focus moves to the question's first button, so Tab, Enter and Escape reach the
        // question and never the form behind it.
        question() {
            asks++;
            const question = [...questions()].pop();
            if (!question) return;
            const active = document.activeElement;
            behind = forms().some((f) => f.contains(active)) ? active : null;
            forms().forEach(syncTrap);
            if (!question.contains(document.activeElement)) question.querySelector("fluent-button")?.focus();
        },
        // The question is going away. Once it has left the page, a form dialog that is still open
        // (Keep editing) traps focus again and gets it back: the element that had it, else its first
        // control. With no form dialog left (Discard, a delete), restore() does the rest.
        answered() {
            const mine = ++asks;
            const was = behind;
            behind = null;
            let tries = 0;
            const settle = () => {
                if (mine !== asks) return;
                if (questions().length && tries++ < 40) {
                    setTimeout(settle, 50);
                    return;
                }
                const form = forms().pop();
                if (!form) return;
                if (!form.contains(document.activeElement)) {
                    const to = was && was.isConnected && form.contains(was) ? was : firstControl(form);
                    to?.focus();
                }
                syncTrap(form);
            };
            setTimeout(settle, 0);
        }
    };
})();
