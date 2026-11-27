const states = new Map();

export function init(id, dotNet) {
    destroy(id);
    const panel = document.getElementById(id);
    if (!panel) return;
    const previous = document.activeElement;
    const onKeyDown = async event => {
        if (event.key === 'Escape') {
            event.preventDefault();
            await dotNet.invokeMethodAsync('CloseFromKeyboard');
            return;
        }
        if (event.key !== 'Tab') return;
        const focusable = [...panel.querySelectorAll('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])')];
        if (focusable.length === 0) {
            event.preventDefault();
            panel.focus();
            return;
        }
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    };
    panel.addEventListener('keydown', onKeyDown);
    states.set(id, { panel, previous, onKeyDown });
    requestAnimationFrame(() => {
        const target = panel.querySelector('[autofocus], input:not([disabled]), select:not([disabled]), button:not([disabled])');
        (target || panel).focus();
    });
}

export function destroy(id) {
    const state = states.get(id);
    if (!state) return;
    state.panel.removeEventListener('keydown', state.onKeyDown);
    if (state.previous && state.previous.isConnected) state.previous.focus();
    states.delete(id);
}
