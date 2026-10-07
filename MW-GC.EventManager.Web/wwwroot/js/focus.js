// Focus return for dialogs (Services/FocusReturn.cs). remember() keeps the focused element when a
// dialog opens; restore() puts focus back once the dialog is gone. If the element left the page (its
// row re-rendered), the element with the same data-focus-key takes focus, else the page heading.
window.emFocus = (() => {
    let element = null;
    let key = null;
    // Each restore() gets a number; a newer remember() or restore() stops an older one.
    let run = 0;

    const lost = () => !document.activeElement || document.activeElement === document.body;

    function target() {
        if (element && element.isConnected) return element;
        if (key) {
            const match = document.querySelector(`[data-focus-key="${CSS.escape(key)}"]`);
            if (match) return match;
        }
        return document.querySelector("h1");
    }

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
        }
    };
})();
