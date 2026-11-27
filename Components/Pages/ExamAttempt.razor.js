const states = new Map();

function keyFor(attemptId) {
    return `examops:attempt:${attemptId}:draft`;
}

export function init(attemptId, dotNet) {
    destroy(attemptId);
    const state = { dotNet, hasUnsaved: false, pendingConnection: null, pendingVisibility: false, timer: null, destroyed: false };
    states.set(attemptId, state);

    const notifyConnection = async online => {
        if (state.destroyed) return;
        try {
            await dotNet.invokeMethodAsync('ConnectionChanged', online);
            state.pendingConnection = null;
        } catch {
            if (!state.destroyed) state.pendingConnection = online;
        }
    };
    const notifyVisibility = async () => {
        if (state.destroyed) return;
        try {
            await dotNet.invokeMethodAsync('PageHidden');
            state.pendingVisibility = false;
        } catch {
            if (!state.destroyed) state.pendingVisibility = true;
        }
    };
    state.online = () => notifyConnection(true);
    state.offline = () => notifyConnection(false);
    state.visibility = () => { if (!state.destroyed && document.visibilityState === 'hidden') notifyVisibility(); };
    state.beforeUnload = event => {
        if (state.destroyed || !state.hasUnsaved) return;
        event.preventDefault();
        event.returnValue = '';
    };
    window.addEventListener('online', state.online);
    window.addEventListener('offline', state.offline);
    document.addEventListener('visibilitychange', state.visibility);
    window.addEventListener('beforeunload', state.beforeUnload);
    state.timer = window.setInterval(() => {
        if (state.pendingConnection !== null) notifyConnection(state.pendingConnection);
        if (state.pendingVisibility) notifyVisibility();
    }, 5000);
    notifyConnection(navigator.onLine);
}

export function storeDraft(attemptId, answers) {
    const state = states.get(attemptId);
    if (!state) return false;
    try {
        sessionStorage.setItem(keyFor(attemptId), JSON.stringify(answers));
        state.hasUnsaved = true;
        return true;
    } catch {
        return false;
    }
}

export function restoreDraft(attemptId) {
    try {
        const raw = sessionStorage.getItem(keyFor(attemptId));
        if (!raw) return {};
        const value = JSON.parse(raw);
        if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
        return Object.fromEntries(Object.entries(value).filter(([key, item]) => /^\d+$/.test(key) && (item === null || typeof item === 'string')));
    } catch {
        try { sessionStorage.removeItem(keyFor(attemptId)); } catch { /* storage unavailable */ }
        return {};
    }
}

export function clearDraft(attemptId) {
    try { sessionStorage.removeItem(keyFor(attemptId)); } catch { /* server state is authoritative */ }
    const state = states.get(attemptId);
    if (state) state.hasUnsaved = false;
}

export function destroy(attemptId) {
    const state = states.get(attemptId);
    if (!state) return;
    state.destroyed = true;
    window.clearInterval(state.timer);
    window.removeEventListener('online', state.online);
    window.removeEventListener('offline', state.offline);
    document.removeEventListener('visibilitychange', state.visibility);
    window.removeEventListener('beforeunload', state.beforeUnload);
    states.delete(attemptId);
}
