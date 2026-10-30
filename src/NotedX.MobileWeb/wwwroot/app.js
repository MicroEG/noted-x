'use strict';

// noted-x mobile PWA. All data stays on the paired desktop; this file only
// talks to the local server and keeps a small offline cache in localStorage.

const MAX_UPLOAD_BYTES = 2 * 1024 * 1024 * 1024;
const DEFAULT_UPLOAD_CHUNK = 1024 * 1024;
const DOWNLOAD_CHUNK = 1024 * 1024;
const SAVE_DEBOUNCE_MS = 900;
const DRAFT_DEBOUNCE_MS = 500;
const SEARCH_DEBOUNCE_MS = 260;
const OWN_SAVE_SUPPRESS_MS = 2200;

const KEYS = {
    session: 'nbut.session',
    theme: 'nbut.theme',
    deviceName: 'nbut.deviceName',
    pages: 'nbut.cache.pages',
    trash: 'nbut.cache.trash',
    files: 'nbut.cache.files',
    drafts: 'nbut.drafts',
    pairToken: 'nbut.pairToken',
    pagePrefix: 'nbut.cache.page.'
};

const BLOCK_TYPES = [
    'paragraph',
    'heading1',
    'heading2',
    'heading3',
    'bulletedList',
    'numberedList',
    'toDo',
    'quote',
    'code',
    'divider',
    'image',
    'file',
    'table'
];

const TEXT_BLOCK_TYPES = new Set([
    'paragraph',
    'heading1',
    'heading2',
    'heading3',
    'bulletedList',
    'numberedList',
    'toDo',
    'quote'
]);

const LIST_BLOCK_TYPES = new Set(['bulletedList', 'numberedList', 'toDo']);

const BLOCK_LABELS = {
    paragraph: 'Text',
    heading1: 'Heading 1',
    heading2: 'Heading 2',
    heading3: 'Heading 3',
    bulletedList: 'Bulleted list',
    numberedList: 'Numbered list',
    toDo: 'To-do',
    quote: 'Quote',
    code: 'Code',
    divider: 'Divider',
    image: 'Image',
    file: 'File',
    table: 'Table'
};

const BLOCK_ICONS = {
    paragraph: 'text',
    heading1: 'text',
    heading2: 'text',
    heading3: 'text',
    bulletedList: 'list',
    numberedList: 'list-ordered',
    toDo: 'todo',
    quote: 'quote',
    code: 'code',
    divider: 'divider',
    image: 'image',
    file: 'file',
    table: 'table'
};

const BLOCK_PLACEHOLDERS = {
    paragraph: 'Type something…',
    heading1: 'Heading 1',
    heading2: 'Heading 2',
    heading3: 'Heading 3',
    bulletedList: 'List item',
    numberedList: 'List item',
    toDo: 'To-do',
    quote: 'Quote',
    code: 'Code'
};

const INSERTABLE_TYPES = [
    'paragraph',
    'heading1',
    'heading2',
    'heading3',
    'bulletedList',
    'numberedList',
    'toDo',
    'quote',
    'code',
    'divider',
    'table'
];

const storage = {
    get(key, fallback = null) {
        try {
            const raw = localStorage.getItem(key);
            return raw === null ? fallback : JSON.parse(raw);
        } catch {
            return fallback;
        }
    },
    set(key, value) {
        try {
            localStorage.setItem(key, JSON.stringify(value));
            return true;
        } catch {
            return false;
        }
    },
    remove(key) {
        try {
            localStorage.removeItem(key);
        } catch {
            // Storage can be unavailable in private modes; the app still works online.
        }
    }
};

const sessionStore = {
    get(key) {
        try {
            return sessionStorage.getItem(key);
        } catch {
            return null;
        }
    },
    set(key, value) {
        try {
            sessionStorage.setItem(key, value);
        } catch {
            // Ignore; the URL can still carry the token until pairing completes.
        }
    },
    remove(key) {
        try {
            sessionStorage.removeItem(key);
        } catch {
            // Ignore.
        }
    }
};

const dom = {
    app: document.getElementById('app'),
    appbar: document.getElementById('appbar'),
    view: document.getElementById('view'),
    tabbar: document.getElementById('tabbar'),
    offlineBanner: document.getElementById('offline-banner'),
    uploadDock: document.getElementById('upload-dock'),
    layerRoot: document.getElementById('layer-root'),
    toastRoot: document.getElementById('toast-root'),
    themeMeta: document.getElementById('theme-color')
};

const themeMedia = window.matchMedia('(prefers-color-scheme: dark)');

const state = {
    session: readStoredSession(),
    themePreference: storage.get(KEYS.theme, 'system'),
    realtime: 'offline',
    currentView: null,
    pagesCache: storage.get(KEYS.pages, []),
    filesCache: storage.get(KEYS.files, []),
    editor: null,
    disposeCurrent: null,
    liveRefresh: null,
    livePageChanged: null,
    liveTransferCompleted: null,
    liveOnline: null,
    installPrompt: null,
    currentHash: ''
};

let routeSeq = 0;
let sessionExpiredHandled = false;
let pendingHash = null;

// ---------- Small helpers ----------

function $(selector, root = document) {
    return root.querySelector(selector);
}

function $$(selector, root = document) {
    return Array.from(root.querySelectorAll(selector));
}

function append(parent, children) {
    for (const child of children.flat(Infinity)) {
        if (child === null || child === undefined || child === false) {
            continue;
        }

        parent.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }

    return parent;
}

function h(tag, attributes, ...children) {
    const element = document.createElement(tag);

    if (attributes) {
        for (const [key, value] of Object.entries(attributes)) {
            if (value === null || value === undefined || value === false) {
                continue;
            }

            if (key === 'class') {
                element.className = value;
            } else if (key === 'dataset') {
                Object.assign(element.dataset, value);
            } else if (key === 'style') {
                Object.assign(element.style, value);
            } else if (key === 'text') {
                element.textContent = value;
            } else if (key.startsWith('on') && typeof value === 'function') {
                element.addEventListener(key.slice(2).toLowerCase(), value);
            } else if (key in element && typeof value !== 'object') {
                try {
                    element[key] = value;
                } catch {
                    element.setAttribute(key, value);
                }
            } else {
                element.setAttribute(key, value);
            }
        }
    }

    return append(element, children);
}

function icon(name, className = 'icon') {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', className);
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');

    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#i-${name}`);
    use.setAttributeNS('http://www.w3.org/1999/xlink', 'xlink:href', `#i-${name}`);
    svg.append(use);
    return svg;
}

function iconButton(name, label, onClick, extraClass = '') {
    const button = h('button', {
        class: `icon-button ${extraClass}`.trim(),
        type: 'button',
        'aria-label': label,
        title: label
    }, icon(name));

    if (onClick) {
        button.addEventListener('click', onClick);
    }

    return button;
}

function brandMark(small = false) {
    return h('img', {
        class: `brand-mark${small ? ' small' : ''}`,
        src: '/icons/mark.png',
        alt: ''
    });
}

function clear(element) {
    if (element) {
        element.replaceChildren();
    }
}

function uid() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }

    return `b-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function sameId(left, right) {
    return String(left || '').toLowerCase() === String(right || '').toLowerCase();
}

function readStoredSession() {
    const value = storage.get(KEYS.session, null);
    if (!value || typeof value.token !== 'string' || typeof value.deviceId !== 'string') {
        return null;
    }

    return value;
}

function saveSession(session) {
    state.session = session;
    storage.set(KEYS.session, session);
    if (session?.deviceName) {
        storage.set(KEYS.deviceName, session.deviceName);
    }
}

function clearSession() {
    state.session = null;
    storage.remove(KEYS.session);
}

function pageCacheKey(id) {
    return `${KEYS.pagePrefix}${id}`;
}

function formatBytes(bytes) {
    const value = Number(bytes);
    if (!Number.isFinite(value) || value < 0) {
        return '—';
    }

    if (value < 1024) {
        return `${value} B`;
    }

    const units = ['KB', 'MB', 'GB', 'TB'];
    let size = value / 1024;
    let unit = 0;
    while (size >= 1024 && unit < units.length - 1) {
        size /= 1024;
        unit += 1;
    }

    const digits = size >= 100 ? 0 : size >= 10 ? 1 : 2;
    return `${size.toFixed(digits)} ${units[unit]}`;
}

function formatClock(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) {
        return '';
    }

    const whole = Math.ceil(seconds);
    if (whole < 60) {
        return `0:${String(whole).padStart(2, '0')}`;
    }

    const minutes = Math.floor(whole / 60);
    const rest = whole % 60;
    return `${minutes}:${String(rest).padStart(2, '0')}`;
}

function parseDate(value) {
    if (!value) {
        return null;
    }

    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date;
}

function relativeTime(value) {
    const date = parseDate(value);
    if (!date) {
        return '';
    }

    const diff = Date.now() - date.getTime();
    const future = diff < 0;
    const seconds = Math.abs(diff) / 1000;

    if (seconds < 45) {
        return future ? 'in a moment' : 'just now';
    }

    if (seconds < 90) {
        return future ? 'in a minute' : '1 min ago';
    }

    const minutes = Math.round(seconds / 60);
    if (minutes < 60) {
        return future ? `in ${minutes} min` : `${minutes} min ago`;
    }

    const hours = Math.round(minutes / 60);
    if (hours < 24) {
        return future ? `in ${hours} h` : `${hours} h ago`;
    }

    const days = Math.round(hours / 24);
    if (days === 1) {
        return future ? 'tomorrow' : 'yesterday';
    }

    if (days < 7) {
        return future ? `in ${days} days` : `${days} days ago`;
    }

    return date.toLocaleDateString(undefined, {
        month: 'short',
        day: 'numeric',
        year: date.getFullYear() === new Date().getFullYear() ? undefined : 'numeric'
    });
}

function formatDate(value) {
    const date = parseDate(value);
    if (!date) {
        return '';
    }

    return date.toLocaleDateString(undefined, {
        month: 'short',
        day: 'numeric',
        year: 'numeric'
    });
}

function debounce(fn, wait) {
    let timer = null;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), wait);
    };
}

function normalizeBlockType(type) {
    return BLOCK_TYPES.includes(type) ? type : 'paragraph';
}

function defaultBlockData(type) {
    switch (type) {
        case 'toDo':
            return { text: '', checked: false };
        case 'code':
            return { text: '', language: '' };
        case 'image':
            return { attachmentId: null, name: '', url: null };
        case 'file':
            return { attachmentId: null, name: '' };
        case 'table':
            return { rows: [['Column 1', 'Column 2', 'Column 3']] };
        default:
            return { text: '' };
    }
}

function cloneData(value) {
    try {
        return JSON.parse(JSON.stringify(value ?? {}));
    } catch {
        return {};
    }
}

function parseBlockContent(content) {
    if (!content) {
        return {};
    }

    try {
        const parsed = JSON.parse(content);
        return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
    } catch {
        return {};
    }
}

function normalizeTableRows(rows) {
    if (!Array.isArray(rows)) {
        return [['Column 1', 'Column 2', 'Column 3']];
    }

    const normalized = rows
        .filter((row) => Array.isArray(row))
        .map((row) => row.map((cell) => (cell === null || cell === undefined ? '' : String(cell))));

    if (normalized.length === 0) {
        return [['Column 1', 'Column 2', 'Column 3']];
    }

    const columns = Math.max(1, ...normalized.map((row) => row.length));
    return normalized.map((row) => {
        while (row.length < columns) {
            row.push('');
        }
        return row;
    });
}

function createLocalBlock(type, data) {
    const normalizedType = normalizeBlockType(type);
    const block = {
        key: uid(),
        type: normalizedType,
        data: { ...defaultBlockData(normalizedType), ...cloneData(data) }
    };

    if (normalizedType === 'table') {
        block.data.rows = normalizeTableRows(block.data.rows);
    }

    return block;
}

function serializeBlockData(block) {
    const data = block.data || {};
    switch (block.type) {
        case 'toDo':
            return { text: String(data.text ?? ''), checked: Boolean(data.checked) };
        case 'code':
            return { text: String(data.text ?? ''), language: String(data.language ?? '') };
        case 'image':
            return {
                attachmentId: data.attachmentId ? String(data.attachmentId) : null,
                name: String(data.name ?? ''),
                url: data.url ? String(data.url) : null
            };
        case 'file':
            return {
                attachmentId: data.attachmentId ? String(data.attachmentId) : null,
                name: String(data.name ?? '')
            };
        case 'table':
            return {
                rows: normalizeTableRows(data.rows).map((row) => row.map((cell) => String(cell ?? '')))
            };
        case 'divider':
            return {};
        default:
            return { text: String(data.text ?? '') };
    }
}

function blockToInput(block) {
    return {
        type: block.type,
        content: JSON.stringify(serializeBlockData(block))
    };
}

function blockToText(block) {
    const data = block.data || {};
    switch (block.type) {
        case 'toDo':
            return `${data.checked ? '- [x]' : '- [ ]'} ${data.text ?? ''}`.trimEnd();
        case 'bulletedList':
            return `- ${data.text ?? ''}`;
        case 'numberedList':
            return `1. ${data.text ?? ''}`;
        case 'quote':
            return `> ${data.text ?? ''}`;
        case 'heading1':
            return `# ${data.text ?? ''}`;
        case 'heading2':
            return `## ${data.text ?? ''}`;
        case 'heading3':
            return `### ${data.text ?? ''}`;
        case 'code':
            return `\`\`\`${data.language || ''}\n${data.text ?? ''}\n\`\`\``;
        case 'divider':
            return '---';
        case 'image':
            return `[Image: ${data.name || 'image'}]`;
        case 'file':
            return `[File: ${data.name || 'file'}]`;
        case 'table':
            return normalizeTableRows(data.rows)
                .map((row) => row.join(' | '))
                .join('\n');
        default:
            return String(data.text ?? '');
    }
}

function extractOffset(iso, localDate) {
    const date = parseDate(iso);
    if (!date) {
        return 0;
    }

    return date.getTime();
}

async function copyText(value) {
    const textValue = String(value ?? '');
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(textValue);
            return true;
        }
    } catch {
        // Fall through to the legacy copy path.
    }

    try {
        const area = h('textarea', {
            style: {
                position: 'fixed',
                left: '-9999px',
                top: '0',
                opacity: '0'
            },
            readonly: true
        });
        area.value = textValue;
        document.body.append(area);
        area.select();
        const copied = document.execCommand('copy');
        area.remove();
        return copied;
    } catch {
        return false;
    }
}

function downloadBlob(blob, name) {
    const url = URL.createObjectURL(blob);
    const anchor = h('a', {
        href: url,
        download: name || 'download',
        style: { display: 'none' }
    });
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function describeError(error, fallback = 'Something went wrong.') {
    if (!error) {
        return fallback;
    }

    if (error.detail) {
        return error.detail;
    }

    if (error.message && error.message !== 'Failed to fetch') {
        return error.message;
    }

    return fallback;
}

function isAbort(error) {
    return error && (error.name === 'AbortError' || error.code === 20);
}

function isAuthError(error) {
    return error && error.status === 401;
}

function fileKind(mime, name) {
    const type = String(mime || '').toLowerCase();
    const extension = String(name || '').toLowerCase().split('.').pop();
    if (type.startsWith('image/')) {
        return 'image';
    }

    if (type === 'application/pdf' || extension === 'pdf') {
        return 'pdf';
    }

    if (type.startsWith('audio/')) {
        return 'audio';
    }

    if (type.startsWith('text/') || ['md', 'txt', 'json', 'csv', 'log'].includes(extension)) {
        return 'text';
    }

    return 'other';
}

function fileOrb(mime, name, size = 34) {
    const kind = fileKind(mime, name);
    const orb = h('div', { class: 'file-orb', dataset: { kind } }, icon(kind === 'image' ? 'image' : 'file'));
    if (size) {
        orb.style.width = `${size}px`;
        orb.style.height = `${size}px`;
    }
    return orb;
}

function isIos() {
    return /iPad|iPhone|iPod/.test(navigator.userAgent) ||
        (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone() {
    return window.matchMedia('(display-mode: standalone)').matches ||
        window.navigator.standalone === true;
}

function guessDeviceName() {
    const saved = storage.get(KEYS.deviceName, null);
    if (typeof saved === 'string' && saved.trim()) {
        return saved.trim().slice(0, 100);
    }

    const ua = navigator.userAgent;
    if (/iPhone/.test(ua)) {
        return 'iPhone';
    }

    if (/iPad/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)) {
        return 'iPad';
    }

    if (/Android/.test(ua)) {
        const match = ua.match(/Android[^;]*;\s*([^;)]+?)(?:\s+Build\/|\))/i);
        const model = match?.[1]?.trim();
        if (model && model.length <= 40 && !/^wv$/i.test(model)) {
            return model.replace(/\s+Build.*$/i, '').slice(0, 100);
        }

        return 'Android phone';
    }

    if (/Macintosh/.test(ua)) {
        return 'Mac browser';
    }

    if (/Windows/.test(ua)) {
        return 'Windows browser';
    }

    return 'Phone';
}

// ---------- Theme ----------

function initializeTheme() {
    if (state.themePreference !== 'light' &&
        state.themePreference !== 'dark' &&
        state.themePreference !== 'system') {
        state.themePreference = 'system';
    }

    applyTheme();
    themeMedia.addEventListener('change', () => {
        if (state.themePreference === 'system') {
            applyTheme();
        }
    });
}

function resolveTheme(preference = state.themePreference) {
    if (preference === 'light' || preference === 'dark') {
        return preference;
    }

    return themeMedia.matches ? 'dark' : 'light';
}

function applyTheme() {
    const resolved = resolveTheme();
    document.documentElement.dataset.theme = resolved;
    if (dom.themeMeta) {
        dom.themeMeta.setAttribute('content', resolved === 'dark' ? '#0E0E12' : '#F6F6F8');
    }

    for (const button of $$('[data-theme-choice]')) {
        button.classList.toggle('active', button.dataset.themeChoice === state.themePreference);
    }
}

function setThemePreference(preference) {
    if (!['light', 'dark', 'system'].includes(preference)) {
        return;
    }

    state.themePreference = preference;
    storage.set(KEYS.theme, preference);
    applyTheme();
}

// ---------- Toasts ----------

function toast(message, options = {}) {
    const {
        type = 'info',
        actionLabel = null,
        onAction = null,
        duration = type === 'error' ? 7000 : 4500
    } = options;

    if (!dom.toastRoot) {
        return;
    }

    const iconName = type === 'success' ? 'check' : type === 'error' ? 'alert' : 'info';
    const element = h('div', { class: `toast toast-${type}` },
        icon(iconName),
        h('div', { class: 'toast-text' }, String(message ?? '')));

    let timer = null;
    let dismissed = false;

    const dismiss = () => {
        if (dismissed) {
            return;
        }

        dismissed = true;
        clearTimeout(timer);
        element.classList.remove('in');
        setTimeout(() => element.remove(), 220);
    };

    if (actionLabel) {
        const action = h('button', {
            class: 'toast-action',
            type: 'button'
        }, actionLabel);
        action.addEventListener('click', () => {
            dismiss();
            try {
                onAction?.();
            } catch (error) {
                console.warn('Toast action failed.', error);
            }
        });
        element.append(action);
    }

    dom.toastRoot.append(element);
    while (dom.toastRoot.children.length > 3) {
        dom.toastRoot.firstElementChild?.remove();
    }

    requestAnimationFrame(() => element.classList.add('in'));
    timer = setTimeout(dismiss, duration);
    element.addEventListener('mouseenter', () => clearTimeout(timer), { passive: true });
    element.addEventListener('mouseleave', () => {
        if (!dismissed) {
            timer = setTimeout(dismiss, 1800);
        }
    });
    return dismiss;
}

function toastError(error, fallback = 'Something went wrong.') {
    if (isAuthError(error) || isAbort(error)) {
        return;
    }

    toast(describeError(error, fallback), { type: 'error' });
}

// ---------- Layers ----------

const openLayers = [];

function mountLayer(scrim, { focus = null, onClose = null } = {}) {
    let closed = false;
    const previousFocus = document.activeElement;
    const layer = {
        scrim,
        close() {
            if (closed) {
                return;
            }

            closed = true;
            const index = openLayers.indexOf(layer);
            if (index >= 0) {
                openLayers.splice(index, 1);
            }

            scrim.classList.remove('in');
            setTimeout(() => {
                scrim.remove();
                if (openLayers.length === 0 && previousFocus && typeof previousFocus.focus === 'function') {
                    try {
                        previousFocus.focus({ preventScroll: true });
                    } catch {
                        // Focus target may have been removed.
                    }
                }
            }, 190);

            try {
                onClose?.();
            } catch (error) {
                console.warn('Layer close handler failed.', error);
            }
        }
    };

    openLayers.push(layer);
    dom.layerRoot.append(scrim);
    requestAnimationFrame(() => scrim.classList.add('in'));

    setTimeout(() => {
        if (openLayers[openLayers.length - 1] !== layer) {
            return;
        }

        const target = focus || scrim.querySelector('input, textarea, button');
        try {
            target?.focus({ preventScroll: true });
        } catch {
            // Ignore focus failures on hidden elements.
        }
    }, 50);

    return layer.close;
}

function closeAllLayers() {
    for (const layer of [...openLayers].reverse()) {
        try {
            layer.close();
        } catch (error) {
            console.warn('Could not close a layer.', error);
        }
    }
}

document.addEventListener('keydown', (event) => {
    const layer = openLayers[openLayers.length - 1];
    if (!layer) {
        return;
    }

    if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        layer.close();
        return;
    }

    if (event.key !== 'Tab') {
        return;
    }

    const focusable = Array.from(layer.scrim.querySelectorAll(
        'button, [href], input, textarea, select, [tabindex]:not([tabindex="-1"])'))
        .filter((element) => !element.disabled && element.offsetParent !== null);

    if (focusable.length === 0) {
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
}, true);

function openSheet({ title = '', subtitle = '', items = [], content = null, onClose = null } = {}) {
    const scrim = h('div', { class: 'scrim' });
    const sheet = h('div', {
        class: 'sheet',
        role: 'dialog',
        'aria-modal': 'true',
        'aria-label': title || 'Options'
    });

    const closeButton = h('button', {
        class: 'sheet-close',
        type: 'button',
        'aria-label': 'Close'
    }, icon('close'));

    sheet.append(h('div', { class: 'sheet-handle' }));
    sheet.append(h('div', { class: 'sheet-header' },
        h('div', { class: 'sheet-title' }, title),
        closeButton));

    if (subtitle) {
        sheet.append(h('div', { class: 'sheet-subtitle' }, subtitle));
    }

    if (content) {
        sheet.append(content);
    }

    if (items.length > 0) {
        const list = h('div', { class: 'sheet-list' });
        for (const item of items) {
            if (!item) {
                continue;
            }

            const button = h('button', {
                class: `sheet-item${item.danger ? ' danger' : ''}`,
                type: 'button',
                disabled: Boolean(item.disabled)
            });

            if (item.icon) {
                button.append(icon(item.icon));
            }

            button.append(h('span', { class: 'sheet-label' }, item.label));
            if (item.hint) {
                button.append(h('span', { class: 'row-value' }, item.hint));
            }

            button.addEventListener('click', () => {
                close();
                try {
                    item.onSelect?.();
                } catch (error) {
                    toastError(error);
                }
            });

            list.append(button);
        }

        sheet.append(list);
    }

    scrim.append(sheet);
    scrim.addEventListener('click', (event) => {
        if (event.target === scrim) {
            close();
        }
    });
    closeButton.addEventListener('click', () => close());

    const close = mountLayer(scrim, { onClose });
    return close;
}

function confirmDialog({
    title,
    message,
    confirmLabel = 'Confirm',
    cancelLabel = 'Cancel',
    danger = false
} = {}) {
    return new Promise((resolve) => {
        let settled = false;

        const finish = (value) => {
            if (settled) {
                return;
            }

            settled = true;
            close();
            resolve(value);
        };

        const cancelButton = h('button', { class: 'btn btn-secondary', type: 'button' }, cancelLabel);
        const confirmButton = h('button', {
            class: `btn ${danger ? 'btn-danger' : 'btn-primary'}`,
            type: 'button'
        }, confirmLabel);

        const dialog = h('div', {
            class: 'dialog',
            role: 'alertdialog',
            'aria-modal': 'true',
            'aria-label': title || 'Confirm'
        },
        h('h2', {}, title || 'Confirm'),
        message ? h('p', {}, message) : null,
        h('div', { class: 'dialog-actions' }, cancelButton, confirmButton));

        const scrim = h('div', { class: 'scrim dialog-scrim' }, dialog);
        scrim.addEventListener('click', (event) => {
            if (event.target === scrim) {
                finish(false);
            }
        });

        cancelButton.addEventListener('click', () => finish(false));
        confirmButton.addEventListener('click', () => finish(true));

        const close = mountLayer(scrim, {
            focus: cancelButton,
            onClose: () => {
                if (!settled) {
                    settled = true;
                    resolve(false);
                }
            }
        });
    });
}

function promptSheet({
    title,
    label,
    value = '',
    placeholder = '',
    confirmLabel = 'Save',
    maxLength = 64,
    inputMode = null,
    type = 'text'
} = {}) {
    return new Promise((resolve) => {
        let settled = false;

        const input = h('input', {
            class: 'input',
            type,
            value,
            placeholder,
            maxlength: String(maxLength),
            autocomplete: 'off',
            'aria-label': label
        });

        if (inputMode) {
            input.setAttribute('inputmode', inputMode);
        }

        const errorBox = h('div', { class: 'form-error', hidden: true },
            icon('alert'),
            h('span', {}, ''));

        const cancelButton = h('button', { class: 'btn btn-secondary', type: 'button' }, 'Cancel');
        const saveButton = h('button', { class: 'btn btn-primary', type: 'button' }, confirmLabel);

        const showError = (message) => {
            errorBox.hidden = !message;
            errorBox.querySelector('span').textContent = message || '';
        };

        const finish = (result) => {
            if (settled) {
                return;
            }

            settled = true;
            close();
            resolve(result);
        };

        const submit = () => {
            const trimmed = input.value.trim();
            if (!trimmed) {
                showError('Enter a value first.');
                input.focus();
                return;
            }

            finish(trimmed);
        };

        const form = h('form', { class: 'sheet-form' },
            h('div', { class: 'field' },
                h('span', { class: 'field-label' }, label),
                input),
            errorBox,
            h('div', { class: 'sheet-actions' }, cancelButton, saveButton));

        form.addEventListener('submit', (event) => {
            event.preventDefault();
            submit();
        });
        cancelButton.addEventListener('click', () => finish(null));
        saveButton.addEventListener('click', submit);
        input.addEventListener('input', () => showError(''));

        const scrim = h('div', { class: 'scrim' });
        const sheet = h('div', {
            class: 'sheet',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': title || label
        },
        h('div', { class: 'sheet-handle' }),
        h('div', { class: 'sheet-header' },
            h('div', { class: 'sheet-title' }, title || label),
            h('button', {
                class: 'sheet-close',
                type: 'button',
                'aria-label': 'Close',
                onclick: () => finish(null)
            }, icon('close'))),
        form);

        scrim.append(sheet);
        scrim.addEventListener('click', (event) => {
            if (event.target === scrim) {
                finish(null);
            }
        });

        const close = mountLayer(scrim, {
            onClose: () => {
                if (!settled) {
                    settled = true;
                    resolve(null);
                }
            }
        });

        setTimeout(() => {
            input.focus();
            input.select();
        }, 80);
    });
}

// ---------- API client ----------

class ApiError extends Error {
    constructor(status, title, detail, retryAfter = null, body = null) {
        super(detail || title || `Request failed (${status})`);
        this.name = 'ApiError';
        this.status = status;
        this.title = title;
        this.detail = detail;
        this.retryAfter = retryAfter;
        this.body = body;
    }
}

async function parseProblem(response) {
    const retryAfter = Number(response.headers.get('Retry-After') || '') || null;
    let body = null;
    try {
        const text = await response.text();
        body = text ? JSON.parse(text) : null;
    } catch {
        body = null;
    }

    const title = body?.title || response.statusText || `Error ${response.status}`;
    const detail = body?.detail || '';
    return new ApiError(response.status, title, detail, retryAfter, body);
}

async function api(path, options = {}) {
    const {
        method = 'GET',
        body = undefined,
        headers = {},
        signal = undefined,
        auth = true
    } = options;

    const requestHeaders = { Accept: 'application/json', ...headers };
    if (auth && state.session?.token) {
        requestHeaders.Authorization = `Bearer ${state.session.token}`;
    }

    const init = {
        method,
        headers: requestHeaders,
        cache: 'no-store',
        signal
    };

    if (body !== undefined) {
        if (typeof Blob !== 'undefined' && body instanceof Blob) {
            init.body = body;
        } else if (body instanceof ArrayBuffer) {
            init.body = body;
        } else {
            if (!requestHeaders['Content-Type']) {
                requestHeaders['Content-Type'] = 'application/json';
            }
            init.body = JSON.stringify(body);
        }
    }

    let response;
    try {
        response = await fetch(path, init);
    } catch (error) {
        if (isAbort(error)) {
            throw error;
        }

        throw new ApiError(
            0,
            'Network error',
            'Could not reach the desktop. Check that both devices are on the same Wi-Fi network.');
    }

    if (response.status === 401 && auth && state.session) {
        handleSessionExpired();
        throw new ApiError(401, 'Not paired', 'Pair this device again.');
    }

    if (!response.ok) {
        throw await parseProblem(response);
    }

    return response;
}

async function apiJson(path, options = {}) {
    const response = await api(path, options);
    if (response.status === 204) {
        return null;
    }

    const text = await response.text();
    if (!text) {
        return null;
    }

    try {
        return JSON.parse(text);
    } catch {
        throw new ApiError(response.status, 'Unexpected response', 'The server sent an unreadable response.');
    }
}

function handleSessionExpired() {
    if (sessionExpiredHandled) {
        return;
    }

    sessionExpiredHandled = true;
    stopRealtime();
    transfers.clear();
    notifyTransfers();
    clearSession();
    closeAllLayers();
    toast('This device is no longer paired.', { type: 'error' });
    showPairing(null, {
        notice: 'This device is no longer paired. Scan a fresh QR code on the desktop to reconnect.'
    });
}

function updateSummaryCache(updated) {
    if (!updated?.id) {
        return;
    }

    const list = Array.isArray(state.pagesCache) ? state.pagesCache : [];
    let found = false;
    const next = list.map((page) => {
        if (!sameId(page.id, updated.id)) {
            return page;
        }

        found = true;
        return {
            ...page,
            title: updated.title,
            icon: updated.icon,
            parentId: updated.parentId,
            position: updated.position,
            isFavorite: updated.isFavorite,
            isDeleted: updated.isDeleted,
            updatedAt: updated.updatedAt
        };
    });

    if (!found && !updated.isDeleted) {
        next.push({
            id: updated.id,
            workspaceId: updated.workspaceId,
            parentId: updated.parentId,
            title: updated.title,
            icon: updated.icon,
            position: updated.position,
            isFavorite: updated.isFavorite,
            isDeleted: updated.isDeleted,
            createdAt: updated.createdAt,
            updatedAt: updated.updatedAt
        });
    }

    state.pagesCache = next.filter((page) => !page.isDeleted);
    storage.set(KEYS.pages, state.pagesCache);
}

function removeFromSummaryCache(id) {
    const list = Array.isArray(state.pagesCache) ? state.pagesCache : [];
    state.pagesCache = list.filter((page) => !sameId(page.id, id));
    storage.set(KEYS.pages, state.pagesCache);
}

async function prefetchPages() {
    if (!state.session) {
        return;
    }

    try {
        const pages = await apiJson('/api/pages');
        if (!Array.isArray(pages)) {
            return;
        }

        state.pagesCache = pages;
        storage.set(KEYS.pages, pages);
    } catch (error) {
        if (!isAbort(error) && !isAuthError(error) && error.status !== 0) {
            console.warn('Could not prefetch pages.', error);
        }
    }
}

// ---------- Pairing ----------

function capturePairToken() {
    let url;
    try {
        url = new URL(window.location.href);
    } catch {
        return null;
    }

    const token = url.searchParams.get('t');
    if (!token) {
        return null;
    }

    sessionStore.set(KEYS.pairToken, token);
    url.searchParams.delete('t');

    const cleaned = `${url.pathname}${url.search}${url.hash}`;
    try {
        history.replaceState(null, '', cleaned || '/pair');
    } catch {
        // Keeping the token in the address bar is acceptable if replaceState is blocked.
    }

    return token;
}

function storedPairToken() {
    if (state.session) {
        sessionStore.remove(KEYS.pairToken);
        return null;
    }

    return sessionStore.get(KEYS.pairToken) || null;
}

function extractPairToken(input) {
    const value = String(input || '').trim();
    if (!value) {
        return null;
    }

    try {
        const url = new URL(value, window.location.origin);
        const token = url.searchParams.get('t');
        if (token) {
            return token;
        }
    } catch {
        // Not a URL; fall through to raw token handling.
    }

    const marker = value.match(/[?&]t=([^&#\s]+)/);
    if (marker) {
        try {
            return decodeURIComponent(marker[1]);
        } catch {
            return marker[1];
        }
    }

    return /^[A-Za-z0-9_-]{16,200}$/.test(value) ? value : null;
}

function showPairing(token, options = {}) {
    endCurrentView();
    state.currentView = 'pair';
    setTabsOff(true);
    dom.tabbar.hidden = true;
    setAppBar({ hidden: true });
    dom.app.hidden = false;
    document.title = 'Pair · noted-x';
    dom.view.replaceChildren(buildPairScreen(token, options));
}

function buildPairScreen(token, { notice = '', cancelable = false } = {}) {
    const screen = h('div', { class: 'pair-screen' });
    screen.append(h('div', { class: 'pair-hero' },
        h('img', { class: 'pair-logo', src: '/icons/logo.png', alt: 'noted-x' }),
        h('h1', {}, 'Pair this device'),
        h('p', {}, token
            ? 'Finish pairing to open your notes on this phone.'
            : 'Open noted-x on the desktop and scan the QR code with this phone.')));

    const card = h('div', { class: 'pair-card' });

    if (notice) {
        card.append(h('div', { class: 'form-error' }, icon('alert'), h('span', {}, notice)));
    }

    if (!token) {
        card.append(h('div', { class: 'pair-steps' },
            pairStep(1, 'Open the noted-x desktop app on the same Wi-Fi network.'),
            pairStep(2, 'Choose “Pair a phone” and point the desktop screen at this phone.'),
            pairStep(3, 'The QR code opens this page with a short-lived pairing link.')));

        const pasteButton = h('button', { class: 'btn btn-primary btn-block', type: 'button' },
            icon('link'), 'I have a pairing link');
        pasteButton.addEventListener('click', async () => {
            const value = await promptSheet({
                title: 'Pairing link',
                label: 'Paste the link or code from the desktop',
                placeholder: 'http://192.168.…/pair?t=…',
                confirmLabel: 'Continue'
            });

            if (value === null) {
                return;
            }

            const extracted = extractPairToken(value);
            if (!extracted) {
                toast('That does not look like a pairing link.', { type: 'error' });
                return;
            }

            sessionStore.set(KEYS.pairToken, extracted);
            showPairing(extracted, { cancelable });
        });
        card.append(pasteButton);
        card.append(h('div', { class: 'pair-hint' },
            'Pairing links expire after two minutes. Everything stays on your local network.'));
    } else {
        const deviceInput = h('input', {
            class: 'input',
            type: 'text',
            value: guessDeviceName(),
            maxlength: '100',
            autocomplete: 'off',
            placeholder: 'Phone',
            'aria-label': 'Device name'
        });

        const pinInput = h('input', {
            class: 'input pin-input',
            type: 'text',
            inputmode: 'numeric',
            maxlength: '4',
            autocomplete: 'one-time-code',
            placeholder: '••••',
            'aria-label': 'Four-digit PIN'
        });

        const errorBox = h('div', { class: 'form-error', hidden: true },
            icon('alert'),
            h('span', {}, ''));

        const submitButton = h('button', { class: 'btn btn-primary btn-block', type: 'button' },
            icon('link'), 'Pair this device');

        const showError = (message) => {
            errorBox.hidden = !message;
            errorBox.querySelector('span').textContent = message || '';
        };

        const setBusy = (busy) => {
            submitButton.disabled = busy;
            if (busy) {
                submitButton.replaceChildren(h('span', { class: 'spinner' }), 'Pairing…');
            } else {
                submitButton.replaceChildren(icon('link'), 'Pair this device');
            }
        };

        pinInput.addEventListener('input', () => {
            const digits = pinInput.value.replace(/\D/g, '').slice(0, 4);
            if (pinInput.value !== digits) {
                pinInput.value = digits;
            }

            pinInput.removeAttribute('aria-invalid');
            showError('');
        });

        deviceInput.addEventListener('input', () => {
            storage.set(KEYS.deviceName, deviceInput.value.trim());
        });

        const submit = async () => {
            const pin = pinInput.value.trim();
            if (pin && pin.length !== 4) {
                pinInput.setAttribute('aria-invalid', 'true');
                showError('Enter the four-digit PIN shown on the desktop.');
                pinInput.focus();
                return;
            }

            setBusy(true);
            showError('');

            try {
                const response = await fetch('/pair', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Accept: 'application/json'
                    },
                    cache: 'no-store',
                    body: JSON.stringify({
                        token,
                        pin: pin || null,
                        deviceName: deviceInput.value.trim() || null
                    })
                });

                if (!response.ok) {
                    const problem = await parseProblem(response);
                    if (response.status === 429) {
                        const seconds = problem.retryAfter || 60;
                        showError(`Too many pairing attempts. Try again in ${seconds} seconds.`);
                        return;
                    }

                    if (problem.title === 'PIN required') {
                        pinInput.setAttribute('aria-invalid', 'true');
                        showError('Enter the four-digit PIN shown on the desktop.');
                        pinInput.focus();
                        return;
                    }

                    if (problem.title === 'Invalid PIN') {
                        pinInput.setAttribute('aria-invalid', 'true');
                        pinInput.select();
                        showError('That PIN is incorrect. Try again.');
                        return;
                    }

                    if (response.status === 401) {
                        showError('This pairing link is no longer valid. Generate a fresh QR code on the desktop.');
                        return;
                    }

                    showError(problem.detail || 'Pairing failed. Try again.');
                    return;
                }

                const result = await response.json();
                if (!result?.token || !result?.deviceId) {
                    showError('The desktop sent an unexpected pairing response.');
                    return;
                }

                saveSession({
                    token: result.token,
                    deviceId: result.deviceId,
                    deviceName: result.deviceName || deviceInput.value.trim() || 'Mobile device',
                    pairedAt: new Date().toISOString()
                });

                sessionExpiredHandled = false;
                sessionStore.remove(KEYS.pairToken);
                toast(`Paired as ${state.session.deviceName}.`, { type: 'success' });
                enterApp();
            } catch (error) {
                if (error.status === 429) {
                    showError(`Too many pairing attempts. Try again in ${error.retryAfter || 60} seconds.`);
                } else if (error.status === 0) {
                    showError('Could not reach the desktop. Check that both devices are on the same Wi-Fi network.');
                } else {
                    showError(error.detail || 'Pairing failed. Try again.');
                }
            } finally {
                if (submitButton.isConnected) {
                    setBusy(false);
                }
            }
        };

        submitButton.addEventListener('click', submit);
        deviceInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                submit();
            }
        });
        pinInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                submit();
            }
        });

        card.append(
            h('div', { class: 'field' },
                h('span', { class: 'field-label' }, 'Device name'),
                deviceInput),
            h('div', { class: 'field' },
                h('span', { class: 'field-label' },
                    h('span', {}, 'PIN'),
                    h('span', { class: 'optional' }, 'Only if the desktop shows one')),
                pinInput),
            errorBox,
            submitButton,
            h('div', { class: 'pair-hint' },
                'The pairing link expires after two minutes. The desktop never shares its token.'),
            h('div', { class: 'pair-note' }, icon('wifi'), 'Stays on your local network'));

        if (cancelable) {
            const back = h('button', { class: 'btn btn-ghost btn-block', type: 'button' }, 'Cancel pairing');
            back.addEventListener('click', () => {
                sessionStore.remove(KEYS.pairToken);
                pendingHash = null;
                enterApp();
            });
            card.append(back);
        }
    }

    screen.append(card);
    if (!token) {
        screen.append(h('div', { class: 'pair-note' }, icon('wifi'), 'No cloud · No telemetry · LAN only'));
    }

    return screen;
}

function pairStep(number, text) {
    return h('div', { class: 'pair-step' },
        h('span', { class: 'pair-step-num' }, String(number)),
        h('span', {}, text));
}

function enterApp() {
    dom.app.hidden = false;
    dom.tabbar.hidden = false;
    setTabsOff(false);
    startRealtime();

    const target = pendingHash && pendingHash.startsWith('#/') ? pendingHash : '#/home';
    pendingHash = null;
    history.replaceState(null, '', `/${target}`);
    route();
    prefetchPages();
}

// ---------- Chrome and routing ----------

function setAppBar({ title = '', subtitle = '', back = null, brand = false, status = true, actions = [], hidden = false } = {}) {
    if (!dom.appbar) {
        return;
    }

    if (hidden) {
        dom.appbar.hidden = true;
        dom.appbar.replaceChildren();
        return;
    }

    dom.appbar.hidden = false;
    const left = h('div', { class: 'appbar-left' });

    if (back) {
        left.append(iconButton('back', 'Back', () => navigate(back), 'back-button'));
    }

    if (brand) {
        left.append(brandMark(true));
    }

    left.append(h('div', { class: 'appbar-titles' },
        h('div', { class: 'appbar-title' }, title),
        subtitle ? h('div', { class: 'appbar-subtitle' }, subtitle) : null));

    const actionBar = h('div', { class: 'appbar-actions' });
    for (const action of actions) {
        if (action) {
            actionBar.append(action);
        }
    }

    if (status && state.session) {
        actionBar.append(statusDot());
    }

    dom.appbar.replaceChildren(h('div', { class: 'appbar-inner' }, left, actionBar));
    dom.appbar.classList.toggle('scrolled', window.scrollY > 4);
    updateStatusIndicators();
}

function statusDot() {
    const button = h('button', {
        class: 'status-dot',
        type: 'button',
        'aria-label': 'Connection status',
        title: 'Connection status'
    });
    button.addEventListener('click', () => openConnectionSheet());
    return button;
}

function connectionState() {
    if (!navigator.onLine) {
        return 'offline';
    }

    if (state.realtime === 'connected') {
        return 'connected';
    }

    return state.realtime === 'connecting' ? 'connecting' : 'offline';
}

function connectionLabel() {
    const value = connectionState();
    if (value === 'connected') {
        return 'Live';
    }

    if (value === 'connecting') {
        return 'Connecting';
    }

    return 'Offline';
}

function updateStatusIndicators() {
    const value = connectionState();
    const label = connectionLabel();

    for (const dot of $$('.status-dot')) {
        dot.dataset.state = value;
        dot.setAttribute('aria-label', `Connection: ${label}`);
        dot.title = `Connection: ${label}`;
    }

    for (const pill of $$('[data-role="connection"]')) {
        pill.dataset.state = value;
        pill.textContent = label;
    }
}

function showTabbar(active) {
    dom.tabbar.hidden = false;
    for (const tab of $$('.tab', dom.tabbar)) {
        tab.classList.toggle('active', tab.dataset.screen === active);
    }
}

function hideTabbar() {
    dom.tabbar.hidden = true;
}

function setTabsOff(off) {
    document.body.classList.toggle('tabs-off', off);
}

function navigate(hash) {
    if (window.location.hash === hash) {
        route();
        return;
    }

    window.location.hash = hash;
}

function parseRoute() {
    const raw = (window.location.hash || '').replace(/^#/, '') || '/home';
    const [path, queryString = ''] = raw.split('?');
    const parts = path.split('/').filter(Boolean);
    return {
        name: parts[0] || 'home',
        id: parts[1] || null,
        query: new URLSearchParams(queryString)
    };
}

function beginView(name) {
    state.currentView = name;
    state.disposeCurrent = null;
    state.liveRefresh = null;
    state.livePageChanged = null;
    state.liveTransferCompleted = null;
    state.liveOnline = null;
}

function endCurrentView() {
    const dispose = state.disposeCurrent;
    state.disposeCurrent = null;
    state.liveRefresh = null;
    state.livePageChanged = null;
    state.liveTransferCompleted = null;
    state.liveOnline = null;

    if (dispose) {
        try {
            dispose();
        } catch (error) {
            console.warn('View cleanup failed.', error);
        }
    }
}

async function route() {
    const seq = ++routeSeq;
    closeAllLayers();
    endCurrentView();
    state.currentView = null;

    if (!state.session) {
        const token = storedPairToken();
        showPairing(token, {
            notice: token ? '' : 'Open the pairing link from the desktop app to connect this phone.'
        });
        return;
    }

    const parsed = parseRoute();
    state.currentHash = window.location.hash;

    try {
        if (parsed.name === 'home' || parsed.name === '') {
            await renderHome(seq);
        } else if (parsed.name === 'page' && parsed.id) {
            await renderEditor(seq, parsed.id);
        } else if (parsed.name === 'search') {
            await renderSearch(seq);
        } else if (parsed.name === 'files') {
            await renderFiles(seq);
        } else if (parsed.name === 'settings') {
            await renderSettings(seq);
        } else if (parsed.name === 'trash') {
            await renderTrash(seq);
        } else {
            navigate('#/home');
            return;
        }
    } catch (error) {
        if (isAbort(error) || isAuthError(error)) {
            return;
        }

        console.warn('Could not render the current view.', error);
        if (seq === routeSeq) {
            dom.view.replaceChildren(h('div', { class: 'empty' },
                h('div', { class: 'empty-orb' }, icon('alert')),
                h('h2', {}, 'Something went wrong'),
                h('p', {}, describeError(error, 'The view could not be loaded.')),
                h('button', {
                    class: 'btn btn-primary',
                    type: 'button',
                    onclick: () => route()
                }, 'Try again')));
        }
    }

    if (seq === routeSeq) {
        window.scrollTo({ top: 0, behavior: 'auto' });
    }
}

function rerenderRoute() {
    route();
}

// ---------- Realtime ----------

let hubConnection = null;
let realtimeRetryTimer = null;
let realtimeRetryDelay = 4000;

function setRealtimeState(value) {
    if (state.realtime === value) {
        updateStatusIndicators();
        return;
    }

    state.realtime = value;
    updateStatusIndicators();
}

function startRealtime() {
    if (!state.session) {
        return;
    }

    if (!window.signalR) {
        setRealtimeState('offline');
        scheduleRealtimeRetry();
        return;
    }

    if (hubConnection) {
        return;
    }

    setRealtimeState('connecting');
    const connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hub', {
            accessTokenFactory: () => state.session?.token || ''
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
        .configureLogging(window.signalR.LogLevel.None)
        .build();

    hubConnection = connection;

    connection.on('PageChanged', (payload) => {
        try {
            handlePageChanged(payload);
        } catch (error) {
            console.warn('Could not handle a page change event.', error);
        }
    });

    connection.on('TransferCompleted', (payload) => {
        try {
            handleTransferCompleted(payload);
        } catch (error) {
            console.warn('Could not handle a transfer event.', error);
        }
    });

    connection.onreconnecting(() => {
        setRealtimeState('connecting');
    });

    connection.onreconnected(() => {
        realtimeRetryDelay = 4000;
        setRealtimeState('connected');
        refreshAfterReconnect();
    });

    connection.onclose(() => {
        if (hubConnection === connection) {
            hubConnection = null;
        }

        setRealtimeState('offline');
        scheduleRealtimeRetry();
    });

    connection.start()
        .then(() => {
            if (hubConnection !== connection) {
                return;
            }

            realtimeRetryDelay = 4000;
            setRealtimeState('connected');
        })
        .catch((error) => {
            if (hubConnection !== connection) {
                return;
            }

            hubConnection = null;
            setRealtimeState('offline');
            console.warn('Realtime connection failed.', error);
            scheduleRealtimeRetry();
        });
}

function stopRealtime() {
    clearTimeout(realtimeRetryTimer);
    realtimeRetryTimer = null;
    const connection = hubConnection;
    hubConnection = null;
    setRealtimeState('offline');

    if (connection) {
        connection.stop().catch(() => {});
    }
}

function scheduleRealtimeRetry() {
    clearTimeout(realtimeRetryTimer);
    realtimeRetryTimer = null;

    if (!state.session || !navigator.onLine) {
        return;
    }

    const delay = realtimeRetryDelay;
    realtimeRetryDelay = Math.min(realtimeRetryDelay * 2, 30_000);
    realtimeRetryTimer = setTimeout(() => {
        if (!state.session || !navigator.onLine || hubConnection) {
            return;
        }

        startRealtime();
    }, delay);
}

function restartRealtime() {
    stopRealtime();
    realtimeRetryDelay = 4000;
    startRealtime();
}

function handlePageChanged(payload) {
    if (!payload) {
        return;
    }

    if (state.currentView === 'editor') {
        state.livePageChanged?.(payload);
    } else {
        state.liveRefresh?.();
    }
}

function handleTransferCompleted(payload) {
    if (!payload) {
        return;
    }

    const own = Array.from(transfers.values()).some(
        (transfer) => transfer.uploadId && sameId(transfer.uploadId, payload.uploadId));

    if (own) {
        return;
    }

    if (state.currentView === 'editor') {
        state.liveTransferCompleted?.(payload);
    } else {
        state.liveRefresh?.();
    }

    if (payload.fileName) {
        toast(`File uploaded: ${payload.fileName}`, { type: 'info' });
    }
}

function refreshAfterReconnect() {
    state.liveRefresh?.();
    refreshCurrentEditor();
}

function refreshCurrentEditor() {
    if (state.currentView === 'editor') {
        state.livePageChanged?.({ pageId: state.editor?.id, change: 'reconnect', at: new Date().toISOString() });
    }
}

// ---------- Connection sheet ----------

function openConnectionSheet() {
    const content = h('div', { class: 'sheet-form' },
        h('div', { class: 'notice' },
            icon(navigator.onLine ? 'wifi' : 'cloud-off'),
            h('span', {}, navigator.onLine
                ? 'Live updates keep this phone in sync with the desktop over your local network.'
                : 'This phone is offline. Cached notes stay available and unsaved changes wait on this device.')),
        h('div', { class: 'field' },
            h('span', { class: 'field-label' }, 'Status'),
            h('span', { class: 'status-pill', dataset: { role: 'connection', state: connectionState() } },
                connectionLabel())),
        h('div', { class: 'field' },
            h('span', { class: 'field-label' }, 'Local server'),
            h('span', { class: 'mono', style: { fontSize: '12.5px', overflowWrap: 'anywhere' } },
                window.location.origin)),
        h('div', { class: 'field' },
            h('span', { class: 'field-label' }, 'This device'),
            h('span', { class: 'muted' }, state.session?.deviceName || 'Not paired')));

    openSheet({
        title: 'Connection',
        content,
        items: [
            {
                label: 'Reconnect live updates',
                icon: 'retry',
                onSelect: () => {
                    restartRealtime();
                    toast('Reconnecting…', { type: 'info' });
                }
            },
            {
                label: 'Refresh this view',
                icon: 'restore',
                onSelect: () => {
                    state.liveRefresh?.();
                    toast('Refreshed.', { type: 'success' });
                }
            }
        ]
    });
}

// ---------- Shared view pieces ----------

function emptyState({ iconName = 'file', title, message = '', actionLabel = null, onAction = null }) {
    const element = h('div', { class: 'empty' },
        h('div', { class: 'empty-orb' }, icon(iconName)),
        h('h2', {}, title),
        message ? h('p', {}, message) : null);

    if (actionLabel) {
        const button = h('button', { class: 'btn btn-primary', type: 'button' }, actionLabel);
        button.addEventListener('click', () => {
            try {
                onAction?.();
            } catch (error) {
                toastError(error);
            }
        });
        element.append(button);
    }

    return element;
}

function pageAvatar(page) {
    return page?.icon
        ? h('div', { class: 'page-avatar' }, page.icon)
        : h('div', { class: 'page-avatar' }, icon('file'));
}

function pageRow(page, { trash = false, actions = null } = {}) {
    const meta = h('div', { class: 'page-meta' });
    if (page.isFavorite && !trash) {
        meta.append(h('span', { class: 'star-badge' }, icon('star-filled')));
    }

    meta.append(h('span', {}, relativeTime(page.updatedAt) || 'Just now'));

    const open = h('a', {
        class: 'page-open',
        href: `#/page/${page.id}`,
        'aria-label': page.title || 'Untitled'
    },
    pageAvatar(page),
    h('div', { class: 'page-copy' },
        h('div', { class: 'page-name' }, page.title || 'Untitled'),
        meta));

    if (trash) {
        open.removeAttribute('href');
        open.setAttribute('aria-disabled', 'true');
    }

    if (actions) {
        open.classList.add('actions-only');
    }

    const row = h('div', { class: `page-row${trash ? ' trashed' : ''}${actions ? ' actions-only' : ''}` }, open);
    if (actions) {
        row.append(actions);
    } else {
        const more = h('button', {
            class: 'row-more',
            type: 'button',
            'aria-label': `Actions for ${page.title || 'Untitled'}`
        }, icon('more'));
        more.addEventListener('click', () => {
            try {
                openPageActions(page);
            } catch (error) {
                toastError(error);
            }
        });
        row.append(more);
    }

    return row;
}

function attachmentRow(attachment, { onDownload = null, trailing = null } = {}) {
    const row = h('div', { class: 'attach-row' },
        fileOrb(attachment.mimeType, attachment.fileName),
        h('div', { class: 'file-copy' },
            h('div', { class: 'file-name' }, attachment.fileName || 'File'),
            h('div', { class: 'file-meta' },
                h('span', {}, formatBytes(attachment.sizeBytes)),
                h('span', { class: 'dot' }),
                h('span', {}, formatDate(attachment.createdAt)))),
        h('div', { class: 'file-actions' }));

    const actions = row.querySelector('.file-actions');
    if (trailing) {
        actions.append(trailing);
    }

    if (onDownload) {
        const button = iconButton('download', `Download ${attachment.fileName || 'file'}`, onDownload);
        actions.prepend(button);
        row.style.cursor = 'pointer';
        row.addEventListener('click', (event) => {
            if (event.target.closest('button')) {
                return;
            }

            onDownload();
        });
    }

    return row;
}

function setAppBarSubtitle(value) {
    const element = dom.appbar?.querySelector('.appbar-subtitle');
    if (element) {
        element.textContent = value;
    }
}

function skeletonPageList(count = 4) {
    const list = h('div', { class: 'page-list' });
    for (let index = 0; index < count; index += 1) {
        list.append(h('div', { class: 'skeleton skeleton-row' }));
    }

    return list;
}

// ---------- Home ----------

async function renderHome(seq) {
    beginView('home');
    setTabsOff(false);
    showTabbar('home');
    dom.app.hidden = false;

    const moreButton = iconButton('more', 'More options', () => openHomeMenu(refresh));
    setAppBar({
        brand: true,
        title: 'noted-x',
        subtitle: 'Loading notes…',
        actions: [moreButton],
        status: true
    });
    document.title = 'noted-x';

    const content = h('div');
    const fab = h('button', {
        class: 'fab',
        type: 'button',
        'aria-label': 'New page',
        title: 'New page'
    }, icon('plus'));

    dom.view.replaceChildren(content, fab);

    const cached = Array.isArray(state.pagesCache) ? state.pagesCache : [];
    if (cached.length > 0) {
        renderHomePages(content, cached, { stale: false });
    } else {
        content.append(h('section', { class: 'section' },
            h('div', { class: 'section-head' },
                h('div', { class: 'section-title' }, 'Recent'),
                h('span', { class: 'section-count' }, 'Loading')),
            skeletonPageList(5)));
    }

    const controller = new AbortController();
    state.disposeCurrent = () => controller.abort();

    let creating = false;
    fab.addEventListener('click', async () => {
        if (creating) {
            return;
        }

        if (!navigator.onLine) {
            toast('You are offline. Create pages when the desktop is reachable.', { type: 'error' });
            return;
        }

        creating = true;
        fab.disabled = true;
        fab.replaceChildren(h('span', { class: 'spinner' }));

        try {
            const page = await apiJson('/api/pages', {
                method: 'POST',
                body: {
                    title: 'Untitled',
                    blocks: [{ type: 'paragraph', content: '{}' }]
                }
            });

            if (page?.id) {
                updateSummaryCache(page);
                storage.set(pageCacheKey(page.id), page);
                navigate(`#/page/${page.id}`);
            }
        } catch (error) {
            if (!isAuthError(error)) {
                toastError(error, 'Could not create a page.');
            }
        } finally {
            creating = false;
            if (fab.isConnected) {
                fab.disabled = false;
                fab.replaceChildren(icon('plus'));
            }
        }
    });

    const refresh = debounce(async () => {
        if (!navigator.onLine || seq !== routeSeq) {
            return;
        }

        try {
            const pages = await apiJson('/api/pages');
            if (seq !== routeSeq || !Array.isArray(pages)) {
                return;
            }

            state.pagesCache = pages;
            storage.set(KEYS.pages, pages);
            renderHomePages(content, pages, { stale: false });
        } catch (error) {
            if (!isAbort(error) && !isAuthError(error)) {
                console.warn('Could not refresh the home list.', error);
            }
        }
    }, 350);

    state.liveRefresh = refresh;

    try {
        const pages = await apiJson('/api/pages');
        if (seq !== routeSeq || controller.signal.aborted) {
            return;
        }

        const list = Array.isArray(pages) ? pages : [];
        state.pagesCache = list;
        storage.set(KEYS.pages, list);
        renderHomePages(content, list, { stale: false });
    } catch (error) {
        if (isAbort(error) || isAuthError(error) || seq !== routeSeq) {
            return;
        }

        if (cached.length > 0) {
            renderHomePages(content, cached, { stale: true });
            setAppBarSubtitle(`${cached.length === 1 ? '1 page' : `${cached.length} pages`} · saved offline`);
            toast('Showing the saved list while this phone reconnects.', { type: 'info' });
        } else {
            if (error.status === 0) {
                setAppBarSubtitle('Offline · desktop unreachable');
                setRealtimeState('offline');
            } else {
                setAppBarSubtitle('Notes unavailable');
            }

            content.replaceChildren(emptyState({
                iconName: 'cloud-off',
                title: error.status === 0 ? 'Offline' : 'Could not load notes',
                message: error.status === 0
                    ? 'This phone cannot reach the desktop right now. Reconnect to the same Wi-Fi network and try again.'
                    : describeError(error, 'The notes could not be loaded.'),
                actionLabel: 'Try again',
                onAction: () => rerenderRoute()
            }));
        }
    }
}

function renderHomePages(container, pages, { stale = false } = {}) {
    container.replaceChildren();

    const list = Array.isArray(pages) ? pages.filter((page) => !page.isDeleted) : [];
    setAppBarSubtitle(list.length === 1 ? '1 page · local network' : `${list.length} pages · local network`);

    if (stale) {
        container.append(h('div', { class: 'notice warn', style: { marginBottom: '14px' } },
            icon('cloud-off'),
            h('span', {}, 'Showing the saved copy of your list. Changes made on other devices will appear when the connection returns.')));
    }

    if (list.length === 0) {
        container.append(emptyState({
            iconName: 'file',
            title: 'No pages yet',
            message: 'Create your first page and start writing. Everything stays on your own network.',
            actionLabel: 'Create a page',
            onAction: () => $('.fab')?.click()
        }));
        return;
    }

    const sorted = [...list].sort((left, right) => extractOffset(right.updatedAt) - extractOffset(left.updatedAt));
    const favorites = sorted.filter((page) => page.isFavorite);
    const others = sorted.filter((page) => !page.isFavorite);

    if (favorites.length > 0) {
        container.append(homeSection('Pinned', 'star-filled', favorites, String(favorites.length)));
    }

    if (others.length > 0 || favorites.length === 0) {
        container.append(homeSection(
            favorites.length > 0 ? 'Notes' : 'Recent',
            null,
            others,
            String(others.length)));
    }
}

function homeSection(title, iconName, pages, count) {
    const section = h('section', { class: 'section' });
    const titleNode = h('div', { class: 'section-title' });
    if (iconName) {
        titleNode.append(icon(iconName));
    }
    titleNode.append(h('span', {}, title));

    section.append(h('div', { class: 'section-head' },
        titleNode,
        h('span', { class: 'section-count' }, count)));

    const list = h('div', { class: 'page-list' });
    for (const page of pages) {
        list.append(pageRow(page));
    }

    section.append(list);
    return section;
}

function openHomeMenu(refresh) {
    openSheet({
        title: 'noted-x',
        subtitle: state.session ? `Paired as ${state.session.deviceName}` : '',
        items: [
            {
                label: 'Trash',
                icon: 'trash',
                onSelect: () => navigate('#/trash')
            },
            {
                label: 'Refresh notes',
                icon: 'retry',
                onSelect: () => {
                    refresh?.();
                    toast('Refreshing notes…', { type: 'info' });
                }
            },
            {
                label: 'Files',
                icon: 'folder',
                onSelect: () => navigate('#/files')
            }
        ]
    });
}

function openPageActions(page) {
    const title = page.title || 'Untitled';

    openSheet({
        title,
        subtitle: `Edited ${relativeTime(page.updatedAt) || 'just now'}`,
        items: [
            {
                label: 'Open page',
                icon: 'file',
                onSelect: () => navigate(`#/page/${page.id}`)
            },
            {
                label: page.isFavorite ? 'Remove from favorites' : 'Add to favorites',
                icon: page.isFavorite ? 'star-filled' : 'star',
                onSelect: () => togglePageFavorite(page)
            },
            {
                label: 'Copy page text',
                icon: 'copy',
                onSelect: () => copyPageTextById(page.id, title)
            },
            {
                label: 'Copy page link',
                icon: 'link',
                onSelect: () => copyPageLink(page.id)
            },
            {
                label: 'Move to trash',
                icon: 'trash',
                danger: true,
                onSelect: () => trashPageFromList(page)
            }
        ]
    });
}

async function togglePageFavorite(page) {
    try {
        const updated = await apiJson(`/api/pages/${page.id}`, {
            method: 'PUT',
            body: { isFavorite: !page.isFavorite }
        });

        if (updated) {
            updateSummaryCache(updated);
            storage.set(pageCacheKey(page.id), updated);
            state.liveRefresh?.();
            toast(updated.isFavorite ? 'Added to favorites.' : 'Removed from favorites.', { type: 'success' });
        }
    } catch (error) {
        toastError(error, 'Could not update the favorite.');
    }
}

async function loadPageDetail(id) {
    try {
        return await apiJson(`/api/pages/${id}`);
    } catch (error) {
        const cached = storage.get(pageCacheKey(id), null);
        if (cached?.id) {
            return cached;
        }

        throw error;
    }
}

async function copyPageTextById(id, fallbackTitle = 'Untitled') {
    try {
        const detail = await loadPageDetail(id);
        if (!detail) {
            throw new Error('The page could not be loaded.');
        }

        const blocks = (detail.blocks || []).map((block) =>
            createLocalBlock(block.type, parseBlockContent(block.content)));
        const title = detail.title && detail.title !== 'Untitled' ? detail.title : fallbackTitle;
        const body = blocks.map(blockToText).filter(Boolean).join('\n\n');
        const copied = await copyText(`${title}\n\n${body}`.trim());
        toast(copied ? 'Page copied.' : 'Could not copy the page.', { type: copied ? 'success' : 'error' });
    } catch (error) {
        toastError(error, 'Could not copy the page.');
    }
}

async function copyPageLink(id) {
    const copied = await copyText(`${window.location.origin}/#/page/${id}`);
    toast(copied
        ? 'Link copied. Open it on any paired device.'
        : 'Could not copy the link.',
    { type: copied ? 'success' : 'error' });
}

async function trashPageFromList(page) {
    const title = page.title || 'Untitled';
    const confirmed = await confirmDialog({
        title: 'Move to trash?',
        message: `“${title}” and its subpages will move to the trash. You can restore them later.`,
        confirmLabel: 'Move to trash',
        danger: true
    });

    if (!confirmed) {
        return;
    }

    try {
        await api(`/api/pages/${page.id}`, { method: 'DELETE' });
        removeFromSummaryCache(page.id);
        storage.remove(pageCacheKey(page.id));
        state.liveRefresh?.();
        toast('Moved to trash.', {
            type: 'success',
            actionLabel: 'Undo',
            duration: 7000,
            onAction: async () => {
                try {
                    await api(`/api/pages/${page.id}/restore`, { method: 'POST' });
                    void prefetchPages();
                    state.liveRefresh?.();
                    toast('Page restored.', { type: 'success' });
                } catch (error) {
                    toastError(error, 'Could not restore the page.');
                }
            }
        });
    } catch (error) {
        toastError(error, 'Could not move the page to the trash.');
    }
}

// ---------- Editor ----------

async function renderEditor(seq, id) {
    beginView('editor');
    setTabsOff(true);
    hideTabbar();
    dom.app.hidden = false;
    dom.view.replaceChildren(h('div', { class: 'boot' },
        h('div', { class: 'spinner' }),
        h('p', {}, 'Opening page…')));
    setAppBar({ back: '#/home', title: 'Page', status: false });
    document.title = 'Page · noted-x';

    const controller = new AbortController();
    state.disposeCurrent = () => controller.abort();

    let detail = null;
    let fromCache = false;

    try {
        detail = await apiJson(`/api/pages/${id}`, { signal: controller.signal });
    } catch (error) {
        if (isAbort(error) || isAuthError(error) || seq !== routeSeq) {
            return;
        }

        const cached = storage.get(pageCacheKey(id), null);
        if (cached?.id) {
            detail = cached;
            fromCache = true;
        } else if (error.status === 404) {
            let trashed = null;
            try {
                trashed = await apiJson(`/api/pages/${id}?includeDeleted=true`, { signal: controller.signal });
            } catch {
                trashed = null;
            }

            if (seq !== routeSeq) {
                return;
            }

            if (trashed?.isDeleted) {
                renderTrashedEditor(seq, trashed);
            } else {
                renderEditorUnavailable(error);
            }
            return;
        } else {
            renderEditorUnavailable(error);
            return;
        }
    }

    if (seq !== routeSeq || controller.signal.aborted) {
        return;
    }

    storage.set(pageCacheKey(id), detail);
    setupEditor(seq, detail, { fromCache });
}

function renderEditorUnavailable(error) {
    setAppBar({ back: '#/home', title: 'Page', status: false });
    document.title = 'Page unavailable · noted-x';
    dom.view.replaceChildren(emptyState({
        iconName: error?.status === 0 ? 'cloud-off' : 'alert',
        title: error?.status === 0 ? 'Page unavailable offline' : 'This page is not available',
        message: error?.status === 0
            ? 'This page has not been saved on this phone yet. Reconnect to the desktop to open it.'
            : describeError(error, 'The page could not be loaded.'),
        actionLabel: 'Back to notes',
        onAction: () => navigate('#/home')
    }));
}

function renderTrashedEditor(seq, page) {
    setAppBar({ back: '#/home', title: page.title || 'Trashed page', status: false });
    document.title = `${page.title || 'Trashed page'} · noted-x`;
    dom.view.replaceChildren(emptyState({
        iconName: 'trash',
        title: 'This page is in the trash',
        message: 'Restore it to continue editing, or leave it in the trash.',
        actionLabel: 'Restore page',
        onAction: async () => {
            try {
                await api(`/api/pages/${page.id}/restore`, { method: 'POST' });
                void prefetchPages();
                toast('Page restored.', { type: 'success' });
                if (seq === routeSeq) {
                    rerenderRoute();
                }
            } catch (error) {
                toastError(error, 'Could not restore the page.');
            }
        }
    }));
}

function setupEditor(seq, detail, { fromCache = false } = {}) {
    const drafts = readEditorDrafts();
    const draft = drafts[detail.id] || null;

    const ed = {
        seq,
        id: detail.id,
        page: { ...detail },
        title: detail.title === 'Untitled' ? '' : (detail.title || ''),
        tags: Array.isArray(detail.tags) ? detail.tags.map((tag) => tag.name) : [],
        blocks: [],
        dirty: false,
        version: 0,
        saving: false,
        pendingSave: false,
        saveTimer: null,
        draftTimer: null,
        baseUpdatedAt: detail.updatedAt,
        lastLocalSaveAt: 0,
        gone: false,
        disposed: false,
        fromCache,
        saveState: 'saved',
        imageUrls: new Map(),
        imageObserver: null,
        blockMap: new Map(),
        serverBlockCount: Array.isArray(detail.blocks) ? detail.blocks.length : 0
    };

    ed.blocks = (detail.blocks || []).map((block) =>
        createLocalBlock(block.type, parseBlockContent(block.content)));

    if (ed.blocks.length === 0) {
        ed.blocks.push(createLocalBlock('paragraph'));
    }

    let draftApplied = false;
    let draftConflict = false;

    if (draft && Array.isArray(draft.blocks) && draft.blocks.length > 0) {
        try {
            ed.title = typeof draft.title === 'string' ? draft.title : ed.title;
            ed.tags = Array.isArray(draft.tags) ? draft.tags.map(String) : ed.tags;
            const restored = draft.blocks
                .filter((block) => block && typeof block === 'object')
                .map((block) => createLocalBlock(block.type, block.data));

            if (restored.length > 0) {
                ed.blocks = restored;
                draftApplied = true;
                ed.dirty = true;
                draftConflict = Boolean(draft.baseUpdatedAt) &&
                    String(draft.baseUpdatedAt) !== String(detail.updatedAt);
            }
        } catch (error) {
            console.warn('Could not restore a local draft.', error);
        }
    }

    const titleInput = h('textarea', {
        class: 'editor-title',
        rows: 1,
        placeholder: 'Untitled',
        maxlength: '500',
        'aria-label': 'Page title',
        spellcheck: 'true'
    });
    titleInput.value = ed.title;

    const meta = h('div', { class: 'editor-meta' });
    const banner = h('div');
    const tagsRow = h('div', { class: 'tags-row' });
    const blocksEl = h('div', { class: 'blocks' });
    const addBlockButton = h('button', {
        class: 'btn btn-ghost btn-block',
        type: 'button'
    }, icon('plus'), 'Add block');
    const foot = h('div', { class: 'editor-foot', hidden: true });

    const editorEl = h('div', { class: 'editor' },
        titleInput,
        meta,
        banner,
        tagsRow,
        blocksEl,
        addBlockButton,
        foot);

    dom.view.replaceChildren(editorEl);

    ed.titleInput = titleInput;
    ed.metaEl = meta;
    ed.bannerEl = banner;
    ed.tagsEl = tagsRow;
    ed.blocksEl = blocksEl;
    ed.footEl = foot;

    const saveStateEl = h('span', { class: 'save-state', dataset: { state: 'saved' } });
    const moreButton = iconButton('more', 'Page actions', () => openEditorMenu(ed));
    ed.saveStateEl = saveStateEl;

    setAppBar({
        back: '#/home',
        title: ed.title.trim() || 'Untitled',
        actions: [saveStateEl, moreButton],
        status: false
    });

    document.title = `${ed.title.trim() || 'Untitled'} · noted-x`;

    autoGrow(titleInput);
    renderEditorTags(ed);
    renderEditorBlocks(ed);
    renderEditorMeta(ed);
    renderEditorFoot(ed);
    renderEditorSaveState(ed, 'saved');

    if (fromCache) {
        renderEditorSaveState(ed, 'offline');
    }

    // Title editing
    const updateTitle = () => {
        ed.title = titleInput.value;
        autoGrow(titleInput);
        updateEditorChromeTitle(ed);
        markEditorDirty(ed);
    };

    titleInput.addEventListener('input', updateTitle);
    titleInput.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' && !event.isComposing) {
            event.preventDefault();
            const first = ed.blocks[0];
            if (first) {
                focusEditorBlock(ed, first.key, 'start');
            }
        }
    });

    // Block editing
    blocksEl.addEventListener('input', (event) => handleEditorInput(ed, event));
    blocksEl.addEventListener('click', (event) => handleEditorClick(ed, event));
    blocksEl.addEventListener('keydown', (event) => handleEditorKeydown(ed, event));

    // Add block
    addBlockButton.addEventListener('click', () => openBlockInsertSheet(ed, ed.blocks.length));

    // Draft restore banner and initial state
    if (draftApplied) {
        presentDraftBanner(ed, draftConflict);
        if (!draftConflict) {
            scheduleEditorSave(ed, 1200);
        }
    } else if (!navigator.onLine) {
        showEditorBanner(ed, {
            tone: 'warn',
            iconName: 'cloud-off',
            text: 'Offline — showing the copy saved on this phone. Changes will sync when the connection returns.'
        });
    }

    ed.livePageChanged = (payload) => handleEditorPageChanged(ed, payload);
    ed.liveRefresh = () => checkEditorForRemoteChanges(ed);
    ed.liveTransferCompleted = (payload) => handleEditorTransferCompleted(ed, payload);
    ed.liveOnline = () => {
        if (!ed.disposed && ed.dirty && (ed.saveState === 'offline' || ed.saveState === 'error')) {
            void saveEditor(ed);
        }
    };

    ed.dispose = () => disposeEditor(ed);
    state.editor = ed;
    state.disposeCurrent = ed.dispose;
    state.liveRefresh = ed.liveRefresh;
    state.livePageChanged = ed.livePageChanged;
    state.liveTransferCompleted = ed.liveTransferCompleted;
    state.liveOnline = ed.liveOnline;

    if (draftApplied && !draftConflict) {
        writeEditorDraft(ed);
    }
}

function updateEditorChromeTitle(ed) {
    const value = ed.title.trim() || 'Untitled';
    const appbarTitle = dom.appbar?.querySelector('.appbar-title');
    if (appbarTitle) {
        appbarTitle.textContent = value;
    }

    document.title = `${value} · noted-x`;
}

function renderEditorMeta(ed) {
    if (!ed.metaEl) {
        return;
    }

    const edited = relativeTime(ed.page?.updatedAt) || 'just now';
    const attachments = Array.isArray(ed.page?.attachments) ? ed.page.attachments.length : 0;

    ed.metaEl.replaceChildren(
        h('span', {}, `Edited ${edited}`),
        h('span', { class: 'dot' }),
        h('span', {}, ed.fromCache ? 'Offline copy' : 'Saved on the desktop'));

    if (attachments > 0) {
        ed.metaEl.append(h('span', { class: 'dot' }));
        ed.metaEl.append(h('span', {}, attachments === 1 ? '1 attachment' : `${attachments} attachments`));
    }
}

function renderEditorTags(ed) {
    if (!ed.tagsEl) {
        return;
    }

    ed.tagsEl.replaceChildren();

    for (const name of ed.tags) {
        const chip = h('span', { class: 'tag-chip' },
            h('span', { class: 'tag-dot' }),
            h('span', {}, name));

        const remove = h('button', {
            type: 'button',
            'aria-label': `Remove tag ${name}`
        }, icon('close'));
        remove.addEventListener('click', () => {
            ed.tags = ed.tags.filter((tag) => tag !== name);
            renderEditorTags(ed);
            markEditorDirty(ed);
        });

        chip.append(remove);
        ed.tagsEl.append(chip);
    }

    const add = h('button', { class: 'tag-add', type: 'button' }, icon('plus'), 'Tag');
    add.addEventListener('click', () => addEditorTag(ed));
    ed.tagsEl.append(add);
}

async function addEditorTag(ed) {
    if (ed.tags.length >= 20) {
        toast('A page can have at most 20 tags.', { type: 'error' });
        return;
    }

    const name = await promptSheet({
        title: 'Add tag',
        label: 'Tag name',
        placeholder: 'e.g. Ideas',
        maxLength: 64,
        confirmLabel: 'Add tag'
    });

    if (name === null) {
        return;
    }

    if (ed.tags.some((tag) => tag.toLowerCase() === name.toLowerCase())) {
        toast('That tag is already on this page.', { type: 'info' });
        return;
    }

    ed.tags.push(name);
    renderEditorTags(ed);
    markEditorDirty(ed);
}

function renderEditorBlocks(ed) {
    if (!ed.blocksEl) {
        return;
    }

    ed.blockMap = new Map(ed.blocks.map((block) => [block.key, block]));
    const fragment = document.createDocumentFragment();
    let numbered = 0;

    ed.blocks.forEach((block, index) => {
        if (block.type === 'numberedList') {
            numbered += 1;
        } else {
            numbered = 0;
        }

        fragment.append(buildEditorBlock(ed, block, index, numbered));
    });

    ed.blocksEl.replaceChildren(fragment);
    for (const area of $$('textarea.block-input', ed.blocksEl)) {
        autoGrow(area);
    }

    for (const figure of $$('.block-image', ed.blocksEl)) {
        if (figure.dataset.attachmentId) {
            observeEditorImage(ed, figure);
        }
    }
}

function buildEditorBlock(ed, block, index, number) {
    const element = h('div', {
        class: `block b-${block.type}`,
        dataset: { key: block.key }
    });

    if (block.type === 'toDo' && block.data.checked) {
        element.classList.add('checked');
    }

    const main = h('div', { class: 'block-main' });

    switch (block.type) {
        case 'divider':
            main.append(h('div', { class: 'block-divider' }, h('hr')));
            break;

        case 'image':
            main.append(buildEditorImage(ed, block));
            break;

        case 'file':
            main.append(buildEditorFile(ed, block));
            break;

        case 'table':
            main.append(buildEditorTable(block));
            break;

        case 'code':
            main.append(buildEditorCode(block));
            break;

        default: {
            if (block.type === 'bulletedList' || block.type === 'numberedList') {
                main.append(h('div', { class: 'block-marker' },
                    block.type === 'bulletedList' ? '•' : `${number}.`));
            }

            if (block.type === 'toDo') {
                const box = h('button', {
                    class: 'todo-box',
                    type: 'button',
                    'aria-pressed': block.data.checked ? 'true' : 'false',
                    'aria-label': block.data.checked ? 'Mark as not done' : 'Mark as done'
                }, icon('check'));
                main.append(box);
            }

            const area = h('textarea', {
                class: 'block-input',
                rows: 1,
                placeholder: BLOCK_PLACEHOLDERS[block.type] || '',
                'aria-label': BLOCK_LABELS[block.type] || 'Block',
                spellcheck: 'true'
            });
            area.value = String(block.data.text ?? '');
            main.append(area);
            break;
        }
    }

    const more = h('button', {
        class: 'block-more',
        type: 'button',
        'aria-label': 'Block options'
    }, icon('more'));

    element.append(main, more);
    return element;
}

function buildEditorCode(block) {
    const frame = h('div', { class: 'code-frame' });
    const language = h('input', {
        class: 'language-input',
        type: 'text',
        value: String(block.data.language ?? ''),
        placeholder: 'language',
        'aria-label': 'Code language',
        autocomplete: 'off',
        spellcheck: 'false',
        maxlength: '40'
    });

    const copy = h('button', {
        class: 'code-copy',
        type: 'button',
        dataset: { action: 'copy-code' }
    }, icon('copy'), 'Copy');

    const area = h('textarea', {
        class: 'block-input code-input',
        rows: 1,
        placeholder: 'Code',
        'aria-label': 'Code',
        spellcheck: 'false'
    });
    area.value = String(block.data.text ?? '');

    frame.append(h('div', { class: 'code-head' }, language, copy), area);
    return frame;
}

function buildEditorImage(ed, block) {
    const figure = h('div', { class: 'block-image' });
    const name = block.data.name || 'Image';
    const attachmentId = block.data.attachmentId ? String(block.data.attachmentId) : null;
    const remoteUrl = !attachmentId && block.data.url ? String(block.data.url) : null;

    if (!attachmentId && !remoteUrl) {
        figure.append(h('div', { class: 'image-loading' },
            icon('image'),
            h('span', {}, 'Image unavailable')));
        return figure;
    }

    if (remoteUrl) {
        figure.append(h('div', { class: 'image-loading' },
            icon('link'),
            h('span', {}, 'Remote image'),
            h('a', {
                class: 'btn btn-ghost',
                href: remoteUrl,
                target: '_blank',
                rel: 'noopener noreferrer'
            }, 'Open link')));
        return figure;
    }

    figure.dataset.attachmentId = attachmentId;
    figure.dataset.imageName = name;
    figure.append(h('div', { class: 'image-loading' },
        h('span', { class: 'spinner' }),
        h('span', {}, 'Loading image…')));
    figure.append(h('div', { class: 'image-caption' },
        h('span', {}, name),
        h('div', { class: 'file-actions' },
            h('button', {
                class: 'icon-button',
                type: 'button',
                dataset: { action: 'download-attachment', attachment: attachmentId },
                'aria-label': `Download ${name}`
            }, icon('download')))));

    const cachedUrl = ed.imageUrls.get(attachmentId);
    if (cachedUrl) {
        const image = h('img', { alt: name, loading: 'lazy' });
        image.src = cachedUrl;
        image.addEventListener('click', () => openImagePreview(ed, attachmentId, name, cachedUrl));
        figure.querySelector('.image-loading')?.replaceWith(image);
    }

    return figure;
}

function observeEditorImage(ed, figure) {
    if (figure.dataset.started === 'true') {
        return;
    }

    figure.dataset.started = 'true';

    if (!('IntersectionObserver' in window)) {
        void loadEditorImage(ed, figure, figure.dataset.attachmentId, figure.dataset.imageName);
        return;
    }

    if (!ed.imageObserver) {
        ed.imageObserver = new IntersectionObserver((entries) => {
            for (const entry of entries) {
                if (!entry.isIntersecting) {
                    continue;
                }

                ed.imageObserver.unobserve(entry.target);
                void loadEditorImage(ed, entry.target, entry.target.dataset.attachmentId, entry.target.dataset.imageName);
            }
        }, { rootMargin: '500px 0px' });
    }

    ed.imageObserver.observe(figure);
}

async function loadEditorImage(ed, figure, attachmentId, name) {
    if (!attachmentId || !figure.isConnected) {
        return;
    }

    try {
        let url = ed.imageUrls.get(attachmentId);
        if (!url) {
            const response = await api(`/api/files/${attachmentId}`);
            const blob = await response.blob();
            url = URL.createObjectURL(blob);
            ed.imageUrls.set(attachmentId, url);
        }

        if (!figure.isConnected) {
            return;
        }

        const image = h('img', { alt: name || 'Image', loading: 'lazy' });
        image.src = url;
        image.addEventListener('click', () => openImagePreview(ed, attachmentId, name || 'Image', url));
        figure.querySelector('.image-loading')?.replaceWith(image);
    } catch (error) {
        if (isAbort(error) || isAuthError(error) || !figure.isConnected) {
            return;
        }

        const loading = figure.querySelector('.image-loading');
        if (loading) {
            loading.replaceChildren(
                icon('alert'),
                h('span', {}, 'Image unavailable'),
                h('button', {
                    class: 'btn btn-ghost',
                    type: 'button',
                    onclick: () => void loadEditorImage(ed, figure, attachmentId, name)
                }, 'Retry'));
        }
    }
}

function openImagePreview(ed, attachmentId, name, url) {
    const content = h('div', { class: 'image-viewer' },
        h('img', { src: url, alt: name || 'Image' }));

    openSheet({
        title: name || 'Image',
        content,
        items: [
            {
                label: 'Download image',
                icon: 'download',
                onSelect: () => startAttachmentDownloadById(ed, attachmentId, name)
            }
        ]
    });
}

function buildEditorFile(ed, block) {
    const attachmentId = block.data.attachmentId ? String(block.data.attachmentId) : null;
    const attachment = attachmentId ? findEditorAttachment(ed, attachmentId) : null;
    const name = block.data.name || attachment?.fileName || 'File';
    const meta = attachment ? formatBytes(attachment.sizeBytes) : 'Attachment';

    return h('div', { class: 'block-file' },
        fileOrb(attachment?.mimeType, name),
        h('div', { class: 'file-copy' },
            h('div', { class: 'file-name' }, name),
            h('div', { class: 'file-meta' }, h('span', {}, meta))),
        h('div', { class: 'file-actions' },
            h('button', {
                class: 'icon-button',
                type: 'button',
                dataset: { action: 'download-attachment', attachment: attachmentId || '' },
                'aria-label': `Download ${name}`
            }, icon('download'))));
}

function buildEditorTable(block) {
    const rows = normalizeTableRows(block.data.rows);
    block.data.rows = rows;
    const columns = Math.max(1, ...rows.map((row) => row.length));
    const grid = h('div', { class: 'table-grid' });
    grid.style.gridTemplateColumns = `repeat(${columns}, minmax(118px, 1fr))`;

    rows.forEach((row, rowIndex) => {
        for (let columnIndex = 0; columnIndex < columns; columnIndex += 1) {
            const input = h('input', {
                class: 'cell-input',
                type: 'text',
                value: row[columnIndex] ?? '',
                'aria-label': `Row ${rowIndex + 1}, column ${columnIndex + 1}`,
                autocomplete: 'off'
            });
            input.dataset.row = String(rowIndex);
            input.dataset.col = String(columnIndex);
            grid.append(input);
        }
    });

    return h('div', { class: 'table-scroll' }, grid);
}

function findEditorAttachment(ed, attachmentId) {
    if (!attachmentId) {
        return null;
    }

    return (ed.page.attachments || []).find((attachment) => sameId(attachment.id, attachmentId)) || null;
}

function renderEditorFoot(ed) {
    if (!ed.footEl) {
        return;
    }

    const attachments = Array.isArray(ed.page.attachments) ? ed.page.attachments : [];
    ed.footEl.hidden = attachments.length === 0;

    if (attachments.length === 0) {
        ed.footEl.replaceChildren();
        renderEditorMeta(ed);
        return;
    }

    const head = h('div', { class: 'editor-foot-head' },
        h('div', { class: 'editor-foot-title' },
            attachments.length === 1 ? 'Attachment' : `Attachments · ${attachments.length}`),
        h('button', {
            class: 'section-action',
            type: 'button',
            onclick: () => pickEditorUpload(ed, 'file')
        }, 'Attach'));

    const list = h('div', { class: 'attach-list' });
    for (const attachment of attachments) {
        list.append(attachmentRow(attachment, {
            onDownload: () => downloadAttachment(attachment)
        }));
    }

    ed.footEl.replaceChildren(head, list);
    renderEditorMeta(ed);
}

function handleEditorInput(ed, event) {
    if (ed.gone) {
        return;
    }

    const blockElement = event.target.closest('.block');
    if (!blockElement || !ed.blocksEl.contains(blockElement)) {
        return;
    }

    const block = ed.blockMap.get(blockElement.dataset.key);
    if (!block) {
        return;
    }

    const target = event.target;
    if (target.classList.contains('block-input')) {
        block.data.text = target.value;
        autoGrow(target);
        markEditorDirty(ed);
    } else if (target.classList.contains('language-input')) {
        block.data.language = target.value;
        markEditorDirty(ed);
    } else if (target.classList.contains('cell-input')) {
        const row = Number(target.dataset.row);
        const column = Number(target.dataset.col);
        if (Number.isInteger(row) && Number.isInteger(column)) {
            if (!Array.isArray(block.data.rows)) {
                block.data.rows = normalizeTableRows([]);
            }

            while (block.data.rows.length <= row) {
                block.data.rows.push([]);
            }

            while (block.data.rows[row].length <= column) {
                block.data.rows[row].push('');
            }

            block.data.rows[row][column] = target.value;
            markEditorDirty(ed);
        }
    }
}

function handleEditorClick(ed, event) {
    const blockElement = event.target.closest('.block');
    if (!blockElement || !ed.blocksEl.contains(blockElement)) {
        return;
    }

    const block = ed.blockMap.get(blockElement.dataset.key);
    if (!block) {
        return;
    }

    const todo = event.target.closest('.todo-box');
    if (todo) {
        block.data.checked = !block.data.checked;
        blockElement.classList.toggle('checked', block.data.checked);
        todo.setAttribute('aria-pressed', block.data.checked ? 'true' : 'false');
        todo.setAttribute('aria-label', block.data.checked ? 'Mark as not done' : 'Mark as done');
        markEditorDirty(ed);
        return;
    }

    const more = event.target.closest('.block-more');
    if (more) {
        openEditorBlockMenu(ed, block);
        return;
    }

    const copyCode = event.target.closest('[data-action="copy-code"]');
    if (copyCode) {
        void (async () => {
            const copied = await copyText(block.data.text ?? '');
            toast(copied ? 'Code copied.' : 'Could not copy the code.', { type: copied ? 'success' : 'error' });
        })();
        return;
    }

    const download = event.target.closest('[data-action="download-attachment"]');
    if (download) {
        startAttachmentDownloadById(ed, download.dataset.attachment, block.data.name);
        return;
    }

    const image = event.target.closest('.block-image img');
    if (image) {
        const figure = image.closest('.block-image');
        const attachmentId = figure?.dataset.attachmentId;
        if (attachmentId) {
            const url = ed.imageUrls.get(attachmentId);
            if (url) {
                openImagePreview(ed, attachmentId, figure.dataset.imageName || 'Image', url);
            }
        }
    }
}

function handleEditorKeydown(ed, event) {
    if (ed.gone || event.isComposing) {
        return;
    }

    const target = event.target;
    if (!target.classList.contains('block-input')) {
        return;
    }

    const blockElement = target.closest('.block');
    if (!blockElement) {
        return;
    }

    const block = ed.blockMap.get(blockElement.dataset.key);
    if (!block) {
        return;
    }

    if (event.key === 'Enter' && !event.shiftKey) {
        if (block.type === 'code') {
            return;
        }

        event.preventDefault();
        const text = String(block.data.text ?? '');
        if (LIST_BLOCK_TYPES.has(block.type) && text.trim() === '') {
            const type = block.type;
            block.type = 'paragraph';
            block.data = { text: '' };
            if (type === 'toDo') {
                blockElement.classList.remove('checked');
            }

            renderEditorBlocks(ed);
            focusEditorBlock(ed, block.key, 'start');
            markEditorDirty(ed);
            return;
        }

        const caret = target.selectionStart ?? text.length;
        const before = text.slice(0, caret);
        const after = text.slice(caret);
        block.data.text = before;

        const nextType = LIST_BLOCK_TYPES.has(block.type) ? block.type : 'paragraph';
        const next = createLocalBlock(nextType);
        next.data.text = after;
        ed.blocks.splice(ed.blocks.indexOf(block) + 1, 0, next);
        renderEditorBlocks(ed);
        focusEditorBlock(ed, next.key, 'start');
        markEditorDirty(ed);
        return;
    }

    if (event.key === 'Backspace' && !event.shiftKey) {
        const start = target.selectionStart ?? 0;
        const end = target.selectionEnd ?? 0;
        if (start !== 0 || end !== 0) {
            return;
        }

        const index = ed.blocks.indexOf(block);
        if (index <= 0) {
            return;
        }

        event.preventDefault();
        const previous = ed.blocks[index - 1];
        const currentText = String(block.data.text ?? '');

        if (currentText === '' &&
            (block.type === 'paragraph' || block.type === 'quote' || LIST_BLOCK_TYPES.has(block.type))) {
            ed.blocks.splice(index, 1);
            renderEditorBlocks(ed);
            focusEditorBlock(ed, previous.key, 'end');
            markEditorDirty(ed);
            return;
        }

        if (TEXT_BLOCK_TYPES.has(previous.type) && TEXT_BLOCK_TYPES.has(block.type)) {
            const previousText = String(previous.data.text ?? '');
            previous.data.text = previousText + currentText;
            ed.blocks.splice(index, 1);
            renderEditorBlocks(ed);
            focusEditorBlock(ed, previous.key, previousText.length);
            markEditorDirty(ed);
        }
    }
}

function focusEditorBlock(ed, key, position = 'end') {
    const element = Array.from(ed.blocksEl.children).find((child) => child.dataset.key === key);
    const input = element?.querySelector('textarea.block-input, input.cell-input');
    if (!input) {
        element?.querySelector('.block-more')?.focus();
        return;
    }

    input.focus();
    const length = input.value.length;
    const caret = position === 'start'
        ? 0
        : position === 'end'
            ? length
            : Math.min(Math.max(0, position), length);
    try {
        input.setSelectionRange(caret, caret);
    } catch {
        // Some input types do not support ranges; focus alone is enough.
    }
    autoGrow(input);
}

function autoGrow(element) {
    if (!element) {
        return;
    }

    element.style.height = 'auto';
    element.style.height = `${element.scrollHeight}px`;
}

function markEditorDirty(ed) {
    if (ed.gone) {
        return;
    }

    ed.dirty = true;
    ed.version += 1;
    renderEditorSaveState(ed, ed.saving ? 'saving' : 'unsaved');
    scheduleEditorSave(ed);
    scheduleEditorDraft(ed);
}

function renderEditorSaveState(ed, value) {
    ed.saveState = value;

    if (!ed.saveStateEl || ed.disposed) {
        return;
    }

    const labels = {
        saved: 'Saved',
        unsaved: 'Unsaved',
        saving: 'Saving…',
        offline: 'Offline',
        error: 'Not saved'
    };

    ed.saveStateEl.dataset.state = value === 'saved' ? 'saved' : value;
    ed.saveStateEl.replaceChildren(
        h('span', { class: 'dot' }),
        h('span', {}, labels[value] || value));

    if (value === 'offline' || value === 'error') {
        const retry = h('button', { type: 'button' }, 'Retry');
        retry.addEventListener('click', () => void saveEditor(ed));
        ed.saveStateEl.append(retry);
    }
}

function scheduleEditorSave(ed, wait = SAVE_DEBOUNCE_MS) {
    clearTimeout(ed.saveTimer);
    ed.saveTimer = setTimeout(() => {
        ed.saveTimer = null;
        void saveEditor(ed);
    }, wait);
}

function scheduleEditorDraft(ed) {
    clearTimeout(ed.draftTimer);
    ed.draftTimer = setTimeout(() => {
        ed.draftTimer = null;
        writeEditorDraft(ed);
    }, DRAFT_DEBOUNCE_MS);
}

function readEditorDrafts() {
    const value = storage.get(KEYS.drafts, {});
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function writeEditorDraft(ed) {
    if (!ed || !ed.dirty || ed.gone) {
        return;
    }

    const drafts = readEditorDrafts();
    drafts[ed.id] = {
        title: ed.title,
        tags: [...ed.tags],
        blocks: ed.blocks.map((block) => ({
            type: block.type,
            data: serializeBlockData(block)
        })),
        baseUpdatedAt: ed.baseUpdatedAt,
        savedAt: new Date().toISOString()
    };

    storage.set(KEYS.drafts, drafts);
}

function clearEditorDraft(id) {
    const drafts = readEditorDrafts();
    if (Object.prototype.hasOwnProperty.call(drafts, id)) {
        delete drafts[id];
        storage.set(KEYS.drafts, drafts);
    }
}

async function saveEditor(ed) {
    if (!ed || ed.gone) {
        return;
    }

    if (ed.saving) {
        ed.pendingSave = true;
        return;
    }

    if (!ed.dirty) {
        return;
    }

    if (!navigator.onLine) {
        renderEditorSaveState(ed, 'offline');
        writeEditorDraft(ed);
        return;
    }

    clearTimeout(ed.saveTimer);
    ed.saveTimer = null;
    ed.saving = true;
    ed.pendingSave = false;
    const version = ed.version;
    renderEditorSaveState(ed, 'saving');

    let succeeded = false;

    try {
        const updated = await apiJson(`/api/pages/${ed.id}`, {
            method: 'PUT',
            body: {
                title: ed.title,
                tags: [...ed.tags],
                blocks: ed.blocks.map(blockToInput)
            }
        });

        if (!updated) {
            throw new ApiError(500, 'Unexpected response', 'The page could not be saved.');
        }

        succeeded = true;
        ed.page = { ...ed.page, ...updated };
        ed.baseUpdatedAt = updated.updatedAt;
        ed.lastLocalSaveAt = Date.now();
        ed.fromCache = false;

        storage.set(pageCacheKey(ed.id), updated);
        updateSummaryCache(updated);
        clearEditorBanner(ed);
        renderEditorMeta(ed);
        renderEditorFoot(ed);

        ed.dirty = ed.version !== version;
        if (ed.dirty) {
            renderEditorSaveState(ed, 'unsaved');
            scheduleEditorSave(ed, 400);
        } else {
            renderEditorSaveState(ed, 'saved');
            clearEditorDraft(ed.id);
        }
    } catch (error) {
        if (isAuthError(error) || isAbort(error)) {
            return;
        }

        writeEditorDraft(ed);

        if (error.status === 404) {
            ed.gone = true;
            ed.dirty = false;
            renderEditorSaveState(ed, 'saved');
            showEditorBanner(ed, {
                tone: 'danger',
                iconName: 'alert',
                text: 'This page is no longer available. It may have been moved to the trash.',
                actions: [{ label: 'Back to notes', onClick: () => navigate('#/home') }]
            });
            return;
        }

        if (error.status === 0 || !navigator.onLine) {
            renderEditorSaveState(ed, 'offline');
            showEditorBanner(ed, {
                tone: 'warn',
                iconName: 'cloud-off',
                text: 'Offline — your changes stay on this phone and will save when the connection returns.',
                actions: [{ label: 'Retry', onClick: () => void saveEditor(ed) }]
            });
        } else {
            renderEditorSaveState(ed, 'error');
            showEditorBanner(ed, {
                tone: 'danger',
                iconName: 'alert',
                text: describeError(error, 'The page could not be saved.'),
                actions: [{ label: 'Retry', onClick: () => void saveEditor(ed) }]
            });
        }
    } finally {
        ed.saving = false;
        if (succeeded && ed.dirty && ed.pendingSave) {
            ed.pendingSave = false;
            scheduleEditorSave(ed, 350);
        }
    }
}

function showEditorBanner(ed, { tone = 'warn', iconName = 'info', text, actions = [] } = {}) {
    if (!ed.bannerEl || ed.disposed) {
        return;
    }

    const banner = h('div', { class: `editor-banner ${tone}` },
        icon(iconName),
        h('span', { class: 'editor-banner-text' }, text));

    for (const action of actions) {
        const button = h('button', { type: 'button' }, action.label);
        button.addEventListener('click', () => {
            try {
                action.onClick?.();
            } catch (error) {
                toastError(error);
            }
        });
        banner.append(button);
    }

    ed.bannerEl.replaceChildren(banner);
}

function clearEditorBanner(ed) {
    ed?.bannerEl?.replaceChildren();
}

function presentDraftBanner(ed, conflict) {
    showEditorBanner(ed, {
        tone: conflict ? 'danger' : 'warn',
        iconName: conflict ? 'alert' : 'info',
        text: conflict
            ? 'This page changed on another device. Your unsaved changes were restored here — review them before saving.'
            : 'Restored unsaved changes from this phone.',
        actions: [
            {
                label: 'Discard',
                onClick: () => void reloadEditor(ed)
            },
            {
                label: 'Save now',
                onClick: () => {
                    clearEditorBanner(ed);
                    ed.dirty = true;
                    void saveEditor(ed);
                }
            }
        ]
    });
}

function handleEditorPageChanged(ed, payload) {
    if (!payload || ed.disposed) {
        return;
    }

    if (payload.pageId && !sameId(payload.pageId, ed.id)) {
        return;
    }

    const change = payload.change;

    if (change === 'reconnect') {
        void checkEditorForRemoteChanges(ed);
        return;
    }

    if (Date.now() - ed.lastLocalSaveAt < OWN_SAVE_SUPPRESS_MS) {
        return;
    }

    if (change === 'trashed' || change === 'deleted') {
        ed.gone = true;
        ed.dirty = false;
        clearTimeout(ed.saveTimer);
        ed.saveTimer = null;
        clearEditorDraft(ed.id);
        renderEditorSaveState(ed, 'saved');
        showEditorBanner(ed, {
            tone: 'danger',
            iconName: 'trash',
            text: 'This page was moved to the trash on another device. Editing is paused.',
            actions: [{ label: 'Back to notes', onClick: () => navigate('#/home') }]
        });
        return;
    }

    if ((change === 'updated' || change === 'restored') && !ed.gone) {
        showEditorBanner(ed, {
            tone: 'warn',
            iconName: 'info',
            text: 'This page changed on another device.',
            actions: [{ label: 'Reload', onClick: () => void reloadEditor(ed) }]
        });
    }
}

async function checkEditorForRemoteChanges(ed) {
    if (!ed || ed.disposed || ed.gone || !navigator.onLine) {
        return;
    }

    try {
        const latest = await apiJson(`/api/pages/${ed.id}`);
        if (!latest || ed.disposed || ed.gone) {
            return;
        }

        if (latest.updatedAt && String(latest.updatedAt) !== String(ed.baseUpdatedAt)) {
            showEditorBanner(ed, {
                tone: 'warn',
                iconName: 'info',
                text: 'This page has newer changes from another device.',
                actions: [{ label: 'Reload', onClick: () => void reloadEditor(ed) }]
            });
        }
    } catch (error) {
        if (!isAbort(error) && !isAuthError(error)) {
            console.warn('Could not check for remote changes.', error);
        }
    }
}

function handleEditorTransferCompleted(ed, payload) {
    if (!ed || ed.disposed || !payload?.pageId || !sameId(payload.pageId, ed.id)) {
        return;
    }

    void (async () => {
        try {
            const refreshed = await apiJson(`/api/pages/${ed.id}`);
            if (!refreshed || ed.disposed) {
                return;
            }

            ed.page.attachments = Array.isArray(refreshed.attachments) ? refreshed.attachments : [];
            renderEditorFoot(ed);
        } catch (error) {
            if (!isAuthError(error) && !isAbort(error)) {
                console.warn('Could not refresh attachments.', error);
            }
        }
    })();
}

async function reloadEditor(ed) {
    if (!ed) {
        return;
    }

    if (ed.dirty) {
        const confirmed = await confirmDialog({
            title: 'Reload this page?',
            message: 'Unsaved changes on this phone will be discarded.',
            confirmLabel: 'Discard and reload',
            danger: true
        });

        if (!confirmed) {
            return;
        }
    }

    ed.dirty = false;
    ed.gone = false;
    clearTimeout(ed.saveTimer);
    ed.saveTimer = null;
    clearEditorDraft(ed.id);

    const dispose = state.disposeCurrent;
    state.disposeCurrent = null;
    try {
        dispose?.();
    } catch {
        // Ignore cleanup issues during an explicit reload.
    }

    await route();
}

function disposeEditor(ed) {
    if (ed.disposed) {
        return;
    }

    ed.disposed = true;
    clearTimeout(ed.saveTimer);
    clearTimeout(ed.draftTimer);
    ed.saveTimer = null;
    ed.draftTimer = null;

    if (ed.dirty && !ed.gone) {
        writeEditorDraft(ed);
        void saveEditor(ed);
    }

    if (ed.imageObserver) {
        ed.imageObserver.disconnect();
        ed.imageObserver = null;
    }

    for (const url of ed.imageUrls.values()) {
        try {
            URL.revokeObjectURL(url);
        } catch {
            // Ignore revoked or invalid URLs.
        }
    }

    ed.imageUrls.clear();

    if (state.editor === ed) {
        state.editor = null;
    }
}

function openEditorMenu(ed) {
    const title = ed.title.trim() || 'Untitled';

    openSheet({
        title,
        subtitle: ed.page ? `Edited ${relativeTime(ed.page.updatedAt) || 'just now'}` : '',
        items: [
            {
                label: ed.page?.isFavorite ? 'Remove from favorites' : 'Add to favorites',
                icon: ed.page?.isFavorite ? 'star-filled' : 'star',
                onSelect: () => toggleEditorFavorite(ed)
            },
            {
                label: 'Add image',
                icon: 'image',
                onSelect: () => pickEditorUpload(ed, 'image')
            },
            {
                label: 'Attach a file',
                icon: 'upload',
                onSelect: () => pickEditorUpload(ed, 'file')
            },
            {
                label: 'Copy page text',
                icon: 'copy',
                onSelect: () => copyEditorText(ed)
            },
            {
                label: 'Copy page link',
                icon: 'link',
                onSelect: () => copyPageLink(ed.id)
            },
            {
                label: 'Reload latest',
                icon: 'retry',
                onSelect: () => void reloadEditor(ed)
            },
            {
                label: 'Move to trash',
                icon: 'trash',
                danger: true,
                onSelect: () => void trashEditorPage(ed)
            }
        ]
    });
}

async function toggleEditorFavorite(ed) {
    const next = !ed.page.isFavorite;

    try {
        const updated = await apiJson(`/api/pages/${ed.id}`, {
            method: 'PUT',
            body: { isFavorite: next }
        });

        if (!updated) {
            return;
        }

        ed.page.isFavorite = updated.isFavorite;
        updateSummaryCache(updated);
        storage.set(pageCacheKey(ed.id), { ...(storage.get(pageCacheKey(ed.id), {}) || {}), ...updated });
        toast(next ? 'Added to favorites.' : 'Removed from favorites.', { type: 'success' });
    } catch (error) {
        toastError(error, 'Could not update the favorite.');
    }
}

async function copyEditorText(ed) {
    const title = ed.title.trim() || 'Untitled';
    const body = ed.blocks.map(blockToText).filter(Boolean).join('\n\n');
    const copied = await copyText(`${title}\n\n${body}`.trim());
    toast(copied ? 'Page copied.' : 'Could not copy the page.', { type: copied ? 'success' : 'error' });
}

async function trashEditorPage(ed) {
    const title = ed.title.trim() || 'Untitled';
    const confirmed = await confirmDialog({
        title: 'Move to trash?',
        message: `“${title}” and its subpages will move to the trash. You can restore them later.`,
        confirmLabel: 'Move to trash',
        danger: true
    });

    if (!confirmed || ed.gone) {
        return;
    }

    clearTimeout(ed.saveTimer);
    ed.saveTimer = null;
    ed.gone = true;

    try {
        await api(`/api/pages/${ed.id}`, { method: 'DELETE' });
        ed.dirty = false;
        clearEditorDraft(ed.id);
        removeFromSummaryCache(ed.id);
        storage.remove(pageCacheKey(ed.id));
        navigate('#/home');
        toast('Moved to trash.', {
            type: 'success',
            actionLabel: 'Undo',
            duration: 7000,
            onAction: async () => {
                try {
                    await api(`/api/pages/${ed.id}/restore`, { method: 'POST' });
                    void prefetchPages();
                    toast('Page restored.', { type: 'success' });
                } catch (error) {
                    toastError(error, 'Could not restore the page.');
                }
            }
        });
    } catch (error) {
        ed.gone = false;
        toastError(error, 'Could not move the page to the trash.');
        if (ed.dirty) {
            scheduleEditorSave(ed, 300);
        }
    }
}

function pickEditorUpload(ed, kind) {
    openFilePicker({
        accept: kind === 'image' ? 'image/*' : '',
        multiple: false,
        onFiles: (files) => {
            for (const file of files) {
                if (!validateUploadFile(file)) {
                    continue;
                }

                enqueueUpload(file, {
                    pageId: ed.id,
                    onComplete: (attachment) => {
                        if (ed.disposed || ed.gone) {
                            return;
                        }

                        const attachments = Array.isArray(ed.page.attachments) ? ed.page.attachments : [];
                        if (!attachments.some((item) => sameId(item.id, attachment.id))) {
                            attachments.unshift(attachment);
                        }
                        ed.page.attachments = attachments;
                        renderEditorFoot(ed);

                        const block = createLocalBlock(kind === 'image' ? 'image' : 'file', {
                            attachmentId: attachment.id,
                            name: attachment.fileName,
                            ...(kind === 'image' ? { url: null } : {})
                        });
                        ed.blocks.push(block);
                        renderEditorBlocks(ed);
                        markEditorDirty(ed);
                        requestAnimationFrame(() => {
                            const element = Array.from(ed.blocksEl.children)
                                .find((child) => child.dataset.key === block.key);
                            element?.scrollIntoView({
                                block: 'center',
                                behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
                            });
                        });
                    }
                });
            }
        }
    });
}

function startAttachmentDownloadById(ed, attachmentId, name) {
    if (!attachmentId) {
        toast('This attachment is missing.', { type: 'error' });
        return;
    }

    const attachment = findEditorAttachment(ed, attachmentId);
    if (attachment) {
        downloadAttachment(attachment);
        return;
    }

    void (async () => {
        try {
            const meta = await apiJson(`/api/files/${attachmentId}/meta`);
            downloadAttachment(meta);
        } catch (error) {
            toastError(error, 'That file is unavailable.');
        }
    })();
}

function openEditorBlockMenu(ed, block) {
    const index = ed.blocks.indexOf(block);
    const items = [];

    if (TEXT_BLOCK_TYPES.has(block.type) || block.type === 'code') {
        items.push({
            label: 'Turn into…',
            icon: 'retry',
            onSelect: () => openBlockTransformSheet(ed, block)
        });
    }

    items.push({
        label: 'Insert block below',
        icon: 'plus',
        onSelect: () => openBlockInsertSheet(ed, index + 1)
    });

    items.push({
        label: 'Move up',
        icon: null,
        disabled: index <= 0,
        onSelect: () => moveEditorBlock(ed, block, -1)
    });

    items.push({
        label: 'Move down',
        icon: null,
        disabled: index >= ed.blocks.length - 1,
        onSelect: () => moveEditorBlock(ed, block, 1)
    });

    items.push({
        label: 'Duplicate',
        icon: 'copy',
        onSelect: () => duplicateEditorBlock(ed, block)
    });

    if (block.type === 'table') {
        items.push({
            label: 'Add row',
            icon: 'plus',
            onSelect: () => {
                const rows = normalizeTableRows(block.data.rows);
                const columns = Math.max(1, ...rows.map((row) => row.length));
                rows.push(new Array(columns).fill(''));
                block.data.rows = rows;
                renderEditorBlocks(ed);
                markEditorDirty(ed);
            }
        });

        items.push({
            label: 'Add column',
            icon: 'plus',
            onSelect: () => {
                const rows = normalizeTableRows(block.data.rows);
                for (const row of rows) {
                    row.push('');
                }
                block.data.rows = rows;
                renderEditorBlocks(ed);
                markEditorDirty(ed);
            }
        });
    }

    if ((block.type === 'image' || block.type === 'file') && block.data.attachmentId) {
        items.push({
            label: 'Download attachment',
            icon: 'download',
            onSelect: () => startAttachmentDownloadById(ed, block.data.attachmentId, block.data.name)
        });
    }

    items.push({
        label: 'Delete block',
        icon: 'trash',
        danger: true,
        onSelect: () => deleteEditorBlock(ed, block)
    });

    openSheet({
        title: BLOCK_LABELS[block.type] || 'Block',
        subtitle: block.type === 'divider' ? '' : String(block.data.text || '').slice(0, 80),
        items
    });
}

function openBlockTransformSheet(ed, block) {
    const targets = ['paragraph', 'heading1', 'heading2', 'heading3', 'bulletedList', 'numberedList', 'toDo', 'quote', 'code'];
    const items = targets.map((type) => ({
        label: BLOCK_LABELS[type],
        icon: BLOCK_ICONS[type],
        hint: type === block.type ? 'Current' : '',
        disabled: type === block.type,
        onSelect: () => {
            const text = String(block.data.text ?? '');
            const language = block.type === 'code' ? String(block.data.language ?? '') : '';
            const checked = block.type === 'toDo' ? Boolean(block.data.checked) : false;
            block.type = type;
            block.data = { ...defaultBlockData(type), text, language, checked };
            renderEditorBlocks(ed);
            const element = Array.from(ed.blocksEl.children).find((child) => child.dataset.key === block.key);
            element?.querySelector('textarea')?.focus();
            markEditorDirty(ed);
        }
    }));

    openSheet({ title: 'Turn into', items });
}

function openBlockInsertSheet(ed, index) {
    const items = INSERTABLE_TYPES.map((type) => ({
        label: BLOCK_LABELS[type],
        icon: BLOCK_ICONS[type],
        onSelect: () => {
            const block = createLocalBlock(type);
            const target = Math.max(0, Math.min(index, ed.blocks.length));
            ed.blocks.splice(target, 0, block);
            renderEditorBlocks(ed);
            const element = Array.from(ed.blocksEl.children).find((child) => child.dataset.key === block.key);
            element?.querySelector('textarea')?.focus();
            element?.scrollIntoView({ block: 'center' });
            markEditorDirty(ed);
        }
    }));

    openSheet({ title: 'Insert block', items });
}

function moveEditorBlock(ed, block, delta) {
    const index = ed.blocks.indexOf(block);
    const target = index + delta;
    if (index < 0 || target < 0 || target >= ed.blocks.length) {
        return;
    }

    ed.blocks.splice(index, 1);
    ed.blocks.splice(target, 0, block);
    renderEditorBlocks(ed);
    focusEditorBlock(ed, block.key, 'start');
    markEditorDirty(ed);
}

function duplicateEditorBlock(ed, block) {
    const copy = createLocalBlock(block.type, serializeBlockData(block));
    ed.blocks.splice(ed.blocks.indexOf(block) + 1, 0, copy);
    renderEditorBlocks(ed);
    if (TEXT_BLOCK_TYPES.has(copy.type) || copy.type === 'code') {
        focusEditorBlock(ed, copy.key, 'start');
    }
    markEditorDirty(ed);
}

function deleteEditorBlock(ed, block) {
    const index = ed.blocks.indexOf(block);
    if (index < 0) {
        return;
    }

    ed.blocks.splice(index, 1);
    if (ed.blocks.length === 0) {
        ed.blocks.push(createLocalBlock('paragraph'));
    }

    renderEditorBlocks(ed);
    const neighbor = ed.blocks[Math.max(0, index - 1)] || ed.blocks[0];
    if (neighbor) {
        focusEditorBlock(ed, neighbor.key, 'end');
    }
    markEditorDirty(ed);
}

// ---------- Transfers ----------

const transfers = new Map();
const transferListeners = new Set();
let uploadWorkerActive = false;

function notifyTransfers() {
    updateDock();

    for (const listener of [...transferListeners]) {
        try {
            listener();
        } catch (error) {
            console.warn('A transfer listener failed.', error);
        }
    }
}

function notifyTransferProgress(transfer) {
    const now = Date.now();
    if (!transfer.lastNotifyAt || now - transfer.lastNotifyAt >= 120) {
        transfer.lastNotifyAt = now;
        notifyTransfers();
    }
}

function subscribeTransfers(listener) {
    transferListeners.add(listener);
    return () => transferListeners.delete(listener);
}

function createTransfer(kind, { name, mimeType, size, pageId = null, onComplete = null }) {
    const transfer = {
        id: uid(),
        kind,
        name: String(name || 'File'),
        mimeType: String(mimeType || ''),
        size: Math.max(0, Number(size) || 0),
        received: 0,
        status: 'queued',
        error: null,
        pageId,
        onComplete,
        file: null,
        attachmentId: null,
        uploadId: null,
        chunkSize: DEFAULT_UPLOAD_CHUNK,
        controller: null,
        autoResume: false,
        running: false,
        etag: null,
        parts: [],
        createdAt: Date.now(),
        startedAt: 0,
        completedAt: 0,
        lastNotifyAt: 0
    };

    transfers.set(transfer.id, transfer);
    return transfer;
}

function validateUploadFile(file) {
    if (!file) {
        return false;
    }

    if (Number(file.size) > MAX_UPLOAD_BYTES) {
        toast(`“${file.name || 'That file'}” is larger than the 2 GB limit.`, { type: 'error' });
        return false;
    }

    return true;
}

function enqueueUpload(file, { pageId = null, onComplete = null } = {}) {
    if (!validateUploadFile(file)) {
        return null;
    }

    const transfer = createTransfer('upload', {
        name: file.name || 'File',
        mimeType: file.type || 'application/octet-stream',
        size: file.size,
        pageId,
        onComplete
    });

    transfer.file = file;
    notifyTransfers();

    if (!navigator.onLine) {
        toast('Upload queued. It starts when this phone is back online.', { type: 'info' });
    } else {
        toast(`Uploading “${transfer.name}”…`, { type: 'info' });
    }

    processQueue();
    return transfer;
}

function processQueue() {
    if (uploadWorkerActive || !navigator.onLine) {
        return;
    }

    const next = Array.from(transfers.values())
        .find((transfer) => transfer.kind === 'upload' && transfer.status === 'queued');

    if (!next) {
        return;
    }

    uploadWorkerActive = true;
    runUpload(next)
        .catch((error) => {
            if (!isAbort(error) && !isAuthError(error)) {
                console.warn('The upload worker stopped unexpectedly.', error);
            }
        })
        .finally(() => {
            uploadWorkerActive = false;
            processQueue();
        });
}

async function runUpload(transfer) {
    if (!transfer || transfer.status !== 'queued') {
        return;
    }

    if (!navigator.onLine) {
        transfer.status = 'paused';
        transfer.autoResume = true;
        transfer.error = 'Waiting for the network';
        notifyTransfers();
        return;
    }

    transfer.status = 'uploading';
    transfer.error = null;
    transfer.startedAt = transfer.startedAt || Date.now();
    transfer.controller = new AbortController();
    transfer.running = true;
    const signal = transfer.controller.signal;
    const startedAt = transfer.startedAt;
    notifyTransfers();

    let restarted = false;

    try {
        if (!transfer.file) {
            throw new ApiError(0, 'Upload unavailable', 'This file is no longer available on this phone.');
        }

        if (!transfer.uploadId) {
            const init = await apiJson('/api/files', {
                method: 'POST',
                signal,
                body: {
                    fileName: transfer.name,
                    mimeType: transfer.mimeType,
                    sizeBytes: transfer.size,
                    pageId: transfer.pageId || null
                }
            });

            if (!init?.uploadId) {
                throw new ApiError(500, 'Unexpected response', 'The desktop did not start the upload.');
            }

            transfer.uploadId = init.uploadId;
            transfer.chunkSize = Number(init.chunkSize) || DEFAULT_UPLOAD_CHUNK;
            transfer.received = Number(init.receivedBytes) || 0;
            if (Number.isFinite(init.sizeBytes)) {
                transfer.size = Number(init.sizeBytes);
            }
        } else {
            try {
                const status = await apiJson(`/api/uploads/${transfer.uploadId}`, { signal });
                if (status?.completedAttachmentId) {
                    await completeUploadTransfer(transfer, status.completedAttachmentId);
                    return;
                }

                transfer.received = Number(status?.receivedBytes) || 0;
                if (Number.isFinite(status?.sizeBytes)) {
                    transfer.size = Number(status.sizeBytes);
                }
            } catch (error) {
                if (isAbort(error) || isAuthError(error)) {
                    throw error;
                }

                if (error.status !== 404) {
                    throw error;
                }

                transfer.uploadId = null;
                transfer.received = 0;
            }
        }

        while (transfer.received < transfer.size) {
            if (!navigator.onLine) {
                transfer.autoResume = true;
                throw new ApiError(0, 'Offline', 'Waiting for the network.');
            }

            const from = transfer.received;
            const to = Math.min(from + transfer.chunkSize, transfer.size) - 1;
            const chunk = transfer.file.slice(from, to + 1);

            let result;
            try {
                result = await apiJson(`/api/files/${transfer.uploadId}`, {
                    method: 'PUT',
                    signal,
                    headers: {
                        'Content-Range': `bytes ${from}-${to}/${transfer.size}`,
                        'Content-Type': 'application/octet-stream'
                    },
                    body: chunk
                });
            } catch (error) {
                if (isAbort(error) || isAuthError(error)) {
                    throw error;
                }

                if (error.status === 409 && error.body && typeof error.body.receivedBytes === 'number') {
                    transfer.received = Number(error.body.receivedBytes) || 0;
                    if (error.body.completedAttachmentId) {
                        await completeUploadTransfer(transfer, error.body.completedAttachmentId);
                        return;
                    }

                    notifyTransfers();
                    continue;
                }

                if (error.status === 404 && !restarted) {
                    restarted = true;
                    transfer.uploadId = null;
                    transfer.received = 0;
                    transfer.status = 'queued';
                    notifyTransfers();
                    await runUpload(transfer);
                    return;
                }

                throw error;
            }

            transfer.received = Number.isFinite(result?.receivedBytes)
                ? Number(result.receivedBytes)
                : to + 1;

            if (result?.completed) {
                await completeUploadTransfer(transfer, result.attachmentId);
                return;
            }

            notifyTransferProgress(transfer);
        }

        if (transfer.received >= transfer.size) {
            const result = await apiJson(`/api/uploads/${transfer.uploadId}/complete`, {
                method: 'POST',
                signal
            });

            await completeUploadTransfer(transfer, result?.attachmentId);
        }
    } catch (error) {
        if (isAuthError(error)) {
            transfer.status = 'error';
            transfer.error = 'Pairing expired';
            transfer.file = null;
            notifyTransfers();
            return;
        }

        if (isAbort(error)) {
            if (transfer.status !== 'paused' && transfer.status !== 'cancelled') {
                transfer.status = 'paused';
                transfer.error = 'Paused';
            }

            notifyTransfers();
            return;
        }

        if (!navigator.onLine) {
            transfer.status = 'paused';
            transfer.autoResume = true;
            transfer.error = 'Waiting for the network';
        } else {
            transfer.status = 'error';
            transfer.autoResume = false;
            transfer.error = describeError(error, 'Upload failed');
        }

        notifyTransfers();
    } finally {
        transfer.running = false;
        transfer.controller = null;

        if (transfer.status === 'uploading') {
            transfer.averageSpeed = transfer.received / Math.max(1, (Date.now() - startedAt) / 1000);
        }
    }
}

async function completeUploadTransfer(transfer, attachmentId) {
    if (!attachmentId) {
        throw new ApiError(500, 'Unexpected response', 'The desktop did not return the uploaded file.');
    }

    let attachment = null;
    try {
        attachment = await apiJson(`/api/files/${attachmentId}/meta`);
    } catch (error) {
        if (isAuthError(error)) {
            throw error;
        }
    }

    if (!attachment) {
        attachment = {
            id: attachmentId,
            pageId: transfer.pageId || null,
            fileName: transfer.name,
            mimeType: transfer.mimeType || 'application/octet-stream',
            sizeBytes: transfer.size,
            uploadedBy: state.session?.deviceName || 'Mobile device',
            createdAt: new Date().toISOString(),
            contentHash: '',
            downloadUrl: `/api/files/${attachmentId}`
        };
    }

    transfer.status = 'done';
    transfer.received = transfer.size;
    transfer.completedAt = Date.now();
    transfer.file = null;
    transfer.uploadId = null;
    transfer.parts = [];
    addFileToCache(attachment);
    notifyTransfers();

    try {
        transfer.onComplete?.(attachment);
    } catch (error) {
        console.warn('The upload completion handler failed.', error);
    }

    if (state.currentView !== 'editor') {
        state.liveRefresh?.();
    }

    toast(`Uploaded “${attachment.fileName}”.`, { type: 'success' });
    scheduleTransferCleanup(transfer);
}

function scheduleTransferCleanup(transfer) {
    setTimeout(() => {
        if (transfer.status === 'done' && transfers.has(transfer.id)) {
            transfers.delete(transfer.id);
            notifyTransfers();
        }
    }, 8000);
}

function pauseTransfer(transfer, { autoResume = false, reason = 'Paused', silent = false } = {}) {
    if (!transfer || (transfer.status !== 'uploading' && transfer.status !== 'downloading')) {
        return;
    }

    transfer.status = 'paused';
    transfer.autoResume = autoResume;
    transfer.error = reason;
    try {
        transfer.controller?.abort();
    } catch {
        // Aborting an already finished request is harmless.
    }

    notifyTransfers();

    if (!silent) {
        toast(`Paused “${transfer.name}”.`, { type: 'info' });
    }
}

function resumeTransfer(transfer) {
    if (!transfer || transfer.running) {
        return;
    }

    if (!navigator.onLine) {
        toast('Connect to the desktop before resuming transfers.', { type: 'error' });
        return;
    }

    transfer.error = null;
    transfer.autoResume = false;
    transfer.status = 'queued';

    if (transfer.kind === 'upload') {
        notifyTransfers();
        processQueue();
    } else {
        notifyTransfers();
        void runDownload(transfer);
    }
}

function cancelTransfer(transfer) {
    if (!transfer) {
        return;
    }

    const wasActive = transfer.status === 'uploading' || transfer.status === 'downloading';
    transfer.status = 'cancelled';
    transfer.autoResume = false;
    transfer.file = null;
    transfer.parts = [];

    try {
        transfer.controller?.abort();
    } catch {
        // Aborting an already finished request is harmless.
    }

    if (transfer.uploadId) {
        const uploadId = transfer.uploadId;
        transfer.uploadId = null;
        api(`/api/uploads/${uploadId}`, { method: 'DELETE' }).catch(() => {});
    }

    transfers.delete(transfer.id);
    notifyTransfers();

    if (wasActive) {
        toast(`Cancelled “${transfer.name}”.`, { type: 'info' });
    }
}

function resumeAutoTransfers() {
    for (const transfer of transfers.values()) {
        if (transfer.autoResume && transfer.status === 'paused' && !transfer.running) {
            transfer.autoResume = false;
            transfer.error = null;
            transfer.status = 'queued';

            if (transfer.kind === 'download') {
                void runDownload(transfer);
            }
        }
    }

    notifyTransfers();
    processQueue();
}

function downloadAttachment(attachment) {
    if (!attachment?.id) {
        toast('This file is unavailable.', { type: 'error' });
        return null;
    }

    const existing = Array.from(transfers.values()).find((transfer) =>
        transfer.kind === 'download' &&
        sameId(transfer.attachmentId, attachment.id) &&
        ['queued', 'downloading', 'paused', 'error'].includes(transfer.status));

    if (existing) {
        toast('This file is already downloading.', { type: 'info' });
        return existing;
    }

    if (!navigator.onLine) {
        toast('Connect to the desktop to download files.', { type: 'error' });
        return null;
    }

    const size = Number(attachment.sizeBytes) || 0;
    if (size > 512 * 1024 * 1024) {
        return confirmDialog({
            title: 'Download a large file?',
            message: `“${attachment.fileName || 'This file'}” is ${formatBytes(size)}. Keep this screen open until the download finishes.`,
            confirmLabel: 'Download'
        }).then((confirmed) => confirmed ? startDownload(attachment, size) : null);
    }

    return startDownload(attachment, size);
}

function startDownload(attachment, size) {
    const transfer = createTransfer('download', {
        name: attachment.fileName || 'File',
        mimeType: attachment.mimeType || 'application/octet-stream',
        size,
        pageId: attachment.pageId || null
    });

    transfer.attachmentId = attachment.id;
    transfer.parts = [];
    notifyTransfers();
    void runDownload(transfer);
    return transfer;
}

async function runDownload(transfer) {
    if (!transfer || transfer.running) {
        return;
    }

    if (!navigator.onLine) {
        transfer.status = 'paused';
        transfer.autoResume = true;
        transfer.error = 'Waiting for the network';
        notifyTransfers();
        return;
    }

    transfer.running = true;
    transfer.status = 'downloading';
    transfer.error = null;
    transfer.startedAt = transfer.startedAt || Date.now();
    transfer.controller = new AbortController();
    const signal = transfer.controller.signal;
    notifyTransfers();

    try {
        while (true) {
            const headers = {};
            if (transfer.received > 0) {
                headers.Range = `bytes=${transfer.received}-`;
                if (transfer.etag) {
                    headers['If-Range'] = transfer.etag;
                }
            }

            let response;
            try {
                response = await api(`/api/files/${transfer.attachmentId}`, { headers, signal });
            } catch (error) {
                if (error.status === 416 && transfer.received > 0) {
                    break;
                }

                throw error;
            }

            const etag = response.headers.get('ETag');
            if (etag) {
                transfer.etag = etag;
            }

            if (response.status === 200) {
                const length = Number(response.headers.get('Content-Length') || '') || 0;
                if (transfer.received > 0 || transfer.parts.length > 0) {
                    transfer.parts = [];
                    transfer.received = 0;
                }

                if (length > 0) {
                    transfer.size = length;
                }
            } else if (response.status === 206) {
                const range = response.headers.get('Content-Range');
                const total = range ? Number(range.split('/').pop()) : NaN;
                if (Number.isFinite(total) && total > 0) {
                    transfer.size = total;
                }
            }

            if (response.body && typeof response.body.getReader === 'function') {
                const reader = response.body.getReader();
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) {
                        break;
                    }

                    if (value && value.byteLength) {
                        transfer.parts.push(value);
                        transfer.received += value.byteLength;
                        notifyTransferProgress(transfer);
                    }
                }
            } else {
                const buffer = await response.arrayBuffer();
                if (buffer && buffer.byteLength) {
                    transfer.parts.push(new Uint8Array(buffer));
                    transfer.received += buffer.byteLength;
                    notifyTransferProgress(transfer);
                }
            }

            if (response.status === 200 || !transfer.size || transfer.received >= transfer.size) {
                break;
            }
        }

        const blob = new Blob(transfer.parts, { type: transfer.mimeType || 'application/octet-stream' });
        if (!transfer.size) {
            transfer.size = blob.size;
        }

        transfer.parts = [];
        transfer.status = 'done';
        transfer.received = transfer.size;
        transfer.completedAt = Date.now();
        downloadBlob(blob, transfer.name);
        notifyTransfers();
        toast(`Saved “${transfer.name}” to this device.`, { type: 'success' });
        scheduleTransferCleanup(transfer);
    } catch (error) {
        if (isAuthError(error)) {
            transfer.status = 'error';
            transfer.error = 'Pairing expired';
            notifyTransfers();
            return;
        }

        if (isAbort(error)) {
            if (transfer.status !== 'paused' && transfer.status !== 'cancelled') {
                transfer.status = 'paused';
                transfer.error = 'Paused';
            }

            notifyTransfers();
            return;
        }

        transfer.status = navigator.onLine ? 'error' : 'paused';
        transfer.autoResume = !navigator.onLine;
        transfer.error = navigator.onLine
            ? describeError(error, 'Download failed')
            : 'Waiting for the network';
        notifyTransfers();
    } finally {
        transfer.running = false;
        transfer.controller = null;
    }
}

function updateDock() {
    const dock = dom.uploadDock;
    if (!dock) {
        return;
    }

    const list = Array.from(transfers.values())
        .filter((transfer) => transfer.status !== 'done' && transfer.status !== 'cancelled');

    if (list.length === 0) {
        dock.hidden = true;
        dock.replaceChildren();
        document.body.classList.remove('has-dock');
        return;
    }

    const active = list.filter((transfer) =>
        transfer.status === 'uploading' || transfer.status === 'downloading');
    const total = list.reduce((sum, transfer) => sum + Math.max(transfer.size, transfer.received), 0);
    const sent = list.reduce((sum, transfer) => sum + Math.min(transfer.received, Math.max(transfer.size, transfer.received)), 0);
    const percent = total > 0 ? Math.min(100, Math.round((sent / total) * 100)) : 0;

    let label;
    if (active.length === 1) {
        const transfer = active[0];
        label = `${transfer.kind === 'upload' ? 'Uploading' : 'Downloading'} “${transfer.name}” · ${percent}%`;
    } else if (active.length > 1) {
        const downloading = active.every((transfer) => transfer.kind === 'download');
        label = `${downloading ? 'Downloading' : 'Uploading'} ${active.length} files · ${percent}%`;
    } else if (list.some((transfer) => transfer.status === 'error')) {
        label = 'A transfer needs attention';
    } else if (list.some((transfer) => transfer.status === 'paused')) {
        label = 'Transfers paused';
    } else {
        label = list.length === 1 ? '1 file waiting' : `${list.length} files waiting`;
    }

    const iconName = active.length > 0 && active[0].kind === 'download' ? 'download' : 'upload';
    dock.hidden = false;
    dock.setAttribute('role', 'button');
    dock.setAttribute('tabindex', '0');
    dock.setAttribute('aria-label', `${label}. Open transfers.`);
    dock.replaceChildren(
        icon(iconName),
        h('span', {}, label),
        h('span', { class: 'mini-progress' }, h('span', { style: { width: `${percent}%` } })));
    document.body.classList.add('has-dock');
}

function transferStatusText(transfer) {
    switch (transfer.status) {
        case 'uploading':
        case 'downloading': {
            const elapsed = Math.max(1, (Date.now() - (transfer.startedAt || Date.now())) / 1000);
            const speed = transfer.received / elapsed;
            return `${transfer.kind === 'upload' ? 'Uploading' : 'Downloading'} · ${formatBytes(speed)}/s`;
        }
        case 'queued':
            return 'Waiting to start';
        case 'paused':
            return transfer.error || 'Paused';
        case 'error':
            return transfer.error || 'Failed';
        case 'done':
            return transfer.kind === 'upload' ? 'Uploaded' : 'Saved';
        default:
            return '';
    }
}

function transferCard(transfer) {
    const total = Math.max(transfer.size, 1);
    const percent = transfer.status === 'done'
        ? 100
        : Math.min(100, Math.round((transfer.received / total) * 100));
    const actions = h('div', { class: 'transfer-actions' });

    if (transfer.status === 'uploading' || transfer.status === 'downloading') {
        actions.append(
            iconButton('pause', 'Pause', () => pauseTransfer(transfer)),
            iconButton('close', 'Cancel', () => cancelTransfer(transfer), 'danger'));
    } else if (transfer.status === 'paused' || transfer.status === 'error') {
        actions.append(
            iconButton('play', 'Resume', () => resumeTransfer(transfer)),
            iconButton('close', 'Cancel', () => cancelTransfer(transfer), 'danger'));
    } else if (transfer.status === 'queued') {
        actions.append(iconButton('close', 'Cancel', () => cancelTransfer(transfer), 'danger'));
    } else if (transfer.status === 'done') {
        actions.append(iconButton('check', 'Dismiss', () => {
            transfers.delete(transfer.id);
            notifyTransfers();
        }));
    }

    return h('div', { class: 'transfer-card', dataset: { status: transfer.status } },
        h('div', { class: 'transfer-top' },
            fileOrb(transfer.mimeType, transfer.name, 32),
            h('div', { class: 'transfer-name' }, transfer.name),
            h('span', { class: 'transfer-bytes' },
                `${formatBytes(transfer.received)} / ${formatBytes(transfer.size)}`)),
        h('div', { class: 'progress' }, h('span', { style: { width: `${percent}%` } })),
        h('div', { class: 'transfer-foot' },
            h('span', {}, transferStatusText(transfer)),
            actions));
}

function addFileToCache(attachment) {
    if (!attachment?.id) {
        return;
    }

    const list = (Array.isArray(state.filesCache) ? state.filesCache : [])
        .filter((item) => !sameId(item.id, attachment.id));

    list.unshift(attachment);
    state.filesCache = list.slice(0, 200);
    storage.set(KEYS.files, state.filesCache);
}

function removeFileFromCache(id) {
    const list = (Array.isArray(state.filesCache) ? state.filesCache : [])
        .filter((item) => !sameId(item.id, id));

    state.filesCache = list;
    storage.set(KEYS.files, list);
}

function openFilePicker({ accept = '', multiple = false, onFiles = null } = {}) {
    const input = h('input', { type: 'file', style: { display: 'none' } });
    if (accept) {
        input.accept = accept;
    }

    if (multiple) {
        input.multiple = true;
    }

    let finished = false;
    const cleanup = () => {
        if (!finished) {
            finished = true;
            input.remove();
        }
    };

    input.addEventListener('change', () => {
        const files = Array.from(input.files || []);
        cleanup();
        if (files.length > 0) {
            onFiles?.(files);
        }
    });
    input.addEventListener('cancel', cleanup);

    document.body.append(input);
    input.click();
}

function pickFilesToUpload() {
    openFilePicker({
        multiple: true,
        onFiles: (files) => {
            for (const file of files) {
                enqueueUpload(file);
            }
        }
    });
}

// ---------- Files ----------

async function renderFiles(seq) {
    beginView('files');
    setTabsOff(false);
    showTabbar('files');
    dom.app.hidden = false;
    setAppBar({ title: 'Files', subtitle: 'Shared on your local network', status: true });
    document.title = 'Files · noted-x';

    const content = h('div');
    dom.view.replaceChildren(content);

    const controller = new AbortController();
    let files = Array.isArray(state.filesCache) ? state.filesCache : [];
    let loading = files.length === 0;
    let stale = false;
    let lastError = null;

    const paint = () => {
        if (seq !== routeSeq) {
            return;
        }

        content.replaceChildren();

        const cards = Array.from(transfers.values())
            .sort((left, right) => right.createdAt - left.createdAt);

        if (cards.length > 0) {
            const list = h('div', { class: 'transfers' });
            for (const transfer of cards) {
                list.append(transferCard(transfer));
            }

            content.append(h('section', { class: 'section' },
                h('div', { class: 'section-head' },
                    h('div', { class: 'section-title' }, 'Transfers'),
                    h('span', { class: 'section-count' }, String(cards.length))),
                list));
        }

        if (stale) {
            content.append(h('div', { class: 'notice warn', style: { marginBottom: '14px' } },
                icon('cloud-off'),
                h('span', {}, lastError?.status === 0
                    ? 'Showing the file list saved on this phone. Reconnect to the desktop for the latest files.'
                    : 'The latest file list could not be loaded.')));
        }

        if (loading) {
            const list = h('div', { class: 'file-list' });
            for (let index = 0; index < 4; index += 1) {
                list.append(h('div', { class: 'skeleton skeleton-row' }));
            }

            content.append(h('section', { class: 'section' },
                h('div', { class: 'files-summary' },
                    h('div', { class: 'section-title' }, 'All files'),
                    h('span', {}, 'Loading…')),
                list));
        } else if (files.length === 0) {
            content.append(emptyState({
                iconName: 'folder',
                title: 'No files yet',
                message: 'Upload photos, documents, or anything else. Files stay on the desktop and are shared over your local network.',
                actionLabel: 'Upload a file',
                onAction: () => pickFilesToUpload()
            }));
        } else {
            const total = files.reduce((sum, file) => sum + (Number(file.sizeBytes) || 0), 0);
            const list = h('div', { class: 'file-list' });
            for (const file of files) {
                list.append(fileRow(file));
            }

            content.append(h('section', { class: 'section' },
                h('div', { class: 'files-summary' },
                    h('div', { class: 'section-title' }, 'All files'),
                    h('span', {}, `${files.length} ${files.length === 1 ? 'file' : 'files'} · ${formatBytes(total)}`)),
                list));
        }

        const fab = h('button', {
            class: 'fab',
            type: 'button',
            'aria-label': 'Upload files',
            title: 'Upload files'
        }, icon('upload'));
        fab.addEventListener('click', () => pickFilesToUpload());
        content.append(fab);
    };

    function fileRow(file) {
        const more = iconButton('more', `Actions for ${file.fileName || 'file'}`, () => openAttachmentMenu(file));
        return attachmentRow(file, {
            onDownload: () => downloadAttachment(file),
            trailing: more
        });
    }

    const refresh = debounce(async () => {
        if (seq !== routeSeq || !navigator.onLine) {
            return;
        }

        try {
            const items = await apiJson('/api/files', { signal: controller.signal });
            if (seq !== routeSeq) {
                return;
            }

            files = Array.isArray(items) ? items : [];
            state.filesCache = files.slice(0, 200);
            storage.set(KEYS.files, state.filesCache);
            loading = false;
            stale = false;
            lastError = null;
            paint();
        } catch (error) {
            if (isAbort(error) || isAuthError(error) || seq !== routeSeq) {
                return;
            }

            loading = false;
            stale = files.length > 0;
            lastError = error;
            paint();

            if (files.length === 0) {
                toastError(error, 'Could not load files.');
            }
        }
    }, 250);

    const unsubscribe = subscribeTransfers(paint);
    state.disposeCurrent = () => {
        controller.abort();
        unsubscribe();
    };
    state.liveRefresh = refresh;
    state.liveTransferCompleted = refresh;
    state.liveOnline = () => {
        if (navigator.onLine) {
            refresh();
        }
    };

    paint();
    void refresh();
}

function openAttachmentMenu(attachment) {
    const name = attachment.fileName || 'File';

    openSheet({
        title: name,
        subtitle: `${formatBytes(attachment.sizeBytes)} · ${formatDate(attachment.createdAt) || 'Stored on the desktop'}`,
        items: [
            {
                label: 'Download to this phone',
                icon: 'download',
                onSelect: () => downloadAttachment(attachment)
            },
            {
                label: 'Copy file name',
                icon: 'copy',
                onSelect: async () => {
                    const copied = await copyText(name);
                    toast(copied ? 'File name copied.' : 'Could not copy the file name.', {
                        type: copied ? 'success' : 'error'
                    });
                }
            },
            {
                label: 'Delete file',
                icon: 'trash',
                danger: true,
                onSelect: () => void deleteAttachment(attachment)
            }
        ]
    });
}

async function deleteAttachment(attachment) {
    const confirmed = await confirmDialog({
        title: 'Delete this file?',
        message: `“${attachment.fileName || 'This file'}” will be removed from the desktop. Pages that use it will show a missing attachment.`,
        confirmLabel: 'Delete',
        danger: true
    });

    if (!confirmed) {
        return;
    }

    try {
        await api(`/api/files/${attachment.id}`, { method: 'DELETE' });

        for (const transfer of Array.from(transfers.values())) {
            if (transfer.kind === 'download' && sameId(transfer.attachmentId, attachment.id)) {
                cancelTransfer(transfer);
            }
        }

        removeFileFromCache(attachment.id);
        state.liveRefresh?.();
        toast('File deleted.', { type: 'success' });
    } catch (error) {
        toastError(error, 'Could not delete the file.');
    }
}

// ---------- Search ----------

function escapeRegExp(value) {
    return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function highlightInto(parent, text, query) {
    const source = String(text ?? '');
    const terms = String(query || '').trim().split(/\s+/).filter(Boolean);

    if (terms.length === 0 || !source) {
        parent.append(document.createTextNode(source));
        return;
    }

    let pattern;
    try {
        pattern = new RegExp(`(${terms.map(escapeRegExp).join('|')})`, 'ig');
    } catch {
        parent.append(document.createTextNode(source));
        return;
    }

    let last = 0;
    for (const match of source.matchAll(pattern)) {
        if (match.index > last) {
            parent.append(document.createTextNode(source.slice(last, match.index)));
        }

        parent.append(h('mark', {}, match[0]));
        last = match.index + match[0].length;
    }

    if (last < source.length) {
        parent.append(document.createTextNode(source.slice(last)));
    }
}

function searchResultRow(hit, query) {
    const avatar = h('div', { class: 'result-avatar' });
    if (hit.icon) {
        avatar.textContent = hit.icon;
    } else {
        avatar.append(icon('file'));
    }

    const title = h('div', { class: 'result-title' });
    highlightInto(title, hit.title || 'Untitled', query);

    const snippet = h('div', { class: 'result-snippet' });
    highlightInto(snippet, hit.snippet || 'Saved on the desktop', query);

    return h('a', {
        class: 'result-row',
        href: `#/page/${hit.id}`,
        'aria-label': hit.title || 'Untitled'
    },
    avatar,
    h('div', { class: 'result-copy' },
        title,
        snippet,
        h('div', { class: 'result-meta' }, relativeTime(hit.updatedAt) || 'Just now')));
}

function offlineSearch(query) {
    const terms = String(query || '').toLowerCase().split(/\s+/).filter(Boolean);
    if (terms.length === 0) {
        return [];
    }

    const pages = Array.isArray(state.pagesCache) ? state.pagesCache : [];
    const hits = [];

    for (const page of pages) {
        if (!page || page.isDeleted) {
            continue;
        }

        const title = String(page.title || 'Untitled');
        const cached = storage.get(pageCacheKey(page.id), null);
        let body = '';

        if (cached && Array.isArray(cached.blocks)) {
            body = cached.blocks
                .map((block) => {
                    if (!block || typeof block !== 'object') {
                        return '';
                    }

                    const data = parseBlockContent(block.content);
                    return blockToText({ type: normalizeBlockType(block.type), data });
                })
                .join('\n');
        }

        const haystack = `${title}\n${body}`.toLowerCase();
        if (!terms.every((term) => haystack.includes(term))) {
            continue;
        }

        const source = body || title;
        const at = Math.max(0, source.toLowerCase().indexOf(terms[0]));
        const snippet = source
            .slice(Math.max(0, at - 50), Math.max(0, at - 50) + 150)
            .replace(/\s+/g, ' ')
            .trim();

        hits.push({
            id: page.id,
            title,
            icon: page.icon,
            snippet: snippet || 'Saved on this phone',
            updatedAt: page.updatedAt
        });
    }

    return hits
        .sort((left, right) => extractOffset(right.updatedAt) - extractOffset(left.updatedAt))
        .slice(0, 30);
}

async function renderSearch(seq) {
    beginView('search');
    setTabsOff(false);
    showTabbar('search');
    dom.app.hidden = false;
    setAppBar({ title: 'Search', subtitle: 'Titles and page content', status: true });
    document.title = 'Search · noted-x';

    const input = h('input', {
        type: 'search',
        placeholder: 'Search notes…',
        autocomplete: 'off',
        enterkeyhint: 'search',
        'aria-label': 'Search notes'
    });
    input.value = typeof state.searchQuery === 'string' ? state.searchQuery : '';

    const clearButton = h('button', {
        class: 'search-clear',
        type: 'button',
        'aria-label': 'Clear search'
    }, icon('close'));

    const hint = h('div', { class: 'search-hint' });
    const listEl = h('div', { class: 'result-list' });
    const bar = h('div', { class: 'search-bar' },
        h('div', { class: 'search-field' }, icon('search'), input, clearButton));
    dom.view.replaceChildren(bar, hint, listEl);

    const controller = new AbortController();
    let runId = 0;
    let timer = null;

    const updateClear = () => {
        clearButton.hidden = !input.value.trim();
    };
    updateClear();

    const showRecent = () => {
        const pages = (Array.isArray(state.pagesCache) ? state.pagesCache : [])
            .filter((page) => !page.isDeleted)
            .sort((left, right) => extractOffset(right.updatedAt) - extractOffset(left.updatedAt))
            .slice(0, 8);

        if (pages.length === 0) {
            hint.textContent = 'Type a word to search titles and page content.';
            listEl.replaceChildren(emptyState({
                iconName: 'search',
                title: 'Search your notes',
                message: 'Matches appear here as you type. Recent pages show when the list is empty.'
            }));
            return;
        }

        hint.textContent = 'Recent pages';
        listEl.replaceChildren(...pages.map((page) => searchResultRow({
            id: page.id,
            title: page.title,
            icon: page.icon,
            snippet: '',
            updatedAt: page.updatedAt
        }, '')));
    };

    function renderResults(hits, query, { offline = false } = {}) {
        const items = hits.filter((hit) => hit && hit.id);

        if (items.length === 0) {
            hint.textContent = `No results for “${query}”`;
            listEl.replaceChildren(emptyState({
                iconName: 'search',
                title: 'No matches',
                message: offline
                    ? 'No page saved on this phone matches those words. Reconnect to search every note.'
                    : 'Try a different word, or check the spelling.'
            }));
            return;
        }

        hint.textContent = `${items.length} ${items.length === 1 ? 'result' : 'results'}${offline ? ' · saved on this phone' : ''}`;
        listEl.replaceChildren(...items.map((hit) => searchResultRow(hit, query)));
    }

    const run = async () => {
        const query = input.value.trim();
        state.searchQuery = query;

        if (!query) {
            runId += 1;
            showRecent();
            return;
        }

        const current = ++runId;
        hint.textContent = 'Searching…';

        if (!navigator.onLine) {
            const hits = offlineSearch(query);
            if (current !== runId || seq !== routeSeq) {
                return;
            }

            renderResults(hits, query, { offline: true });
            return;
        }

        try {
            const hits = await apiJson(`/api/search?q=${encodeURIComponent(query)}&limit=30`, {
                signal: controller.signal
            });

            if (current !== runId || seq !== routeSeq) {
                return;
            }

            renderResults(Array.isArray(hits) ? hits : [], query, { offline: false });
        } catch (error) {
            if (isAbort(error) || isAuthError(error) || current !== runId || seq !== routeSeq) {
                return;
            }

            if (error.status === 0) {
                renderResults(offlineSearch(query), query, { offline: true });
                return;
            }

            hint.textContent = 'Search unavailable';
            listEl.replaceChildren(emptyState({
                iconName: 'alert',
                title: 'Search failed',
                message: describeError(error, 'The desktop could not run that search.'),
                actionLabel: 'Try again',
                onAction: () => void run()
            }));
        }
    };

    const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
            timer = null;
            void run();
        }, SEARCH_DEBOUNCE_MS);
    };

    input.addEventListener('input', () => {
        updateClear();
        if (!input.value.trim()) {
            clearTimeout(timer);
            void run();
        } else {
            schedule();
        }
    });

    input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
            clearTimeout(timer);
            void run();
            input.blur();
        }
    });

    clearButton.addEventListener('click', () => {
        input.value = '';
        state.searchQuery = '';
        updateClear();
        input.focus();
        void run();
    });

    state.disposeCurrent = () => {
        controller.abort();
        clearTimeout(timer);
    };
    state.liveRefresh = () => {
        if (input.value.trim()) {
            schedule();
        } else {
            showRecent();
        }
    };
    state.liveOnline = () => {
        if (navigator.onLine && input.value.trim()) {
            void run();
        }
    };

    if (input.value.trim()) {
        void run();
    } else {
        showRecent();
    }

    requestAnimationFrame(() => {
        try {
            input.focus({ preventScroll: true });
        } catch {
            // Focus is a nicety; the field still works without it.
        }
    });
}

// ---------- Settings ----------

function settingsRow({ iconName, title, desc = '', value = null, action = null, onClick = null, danger = false, accent = false }) {
    const row = h(onClick ? 'button' : 'div', {
        class: `row${danger ? ' danger' : ''}`,
        type: onClick ? 'button' : null
    });

    row.append(h('div', { class: `row-icon${accent ? ' accent' : ''}${danger ? ' danger' : ''}` }, icon(iconName)));
    row.append(h('div', { class: 'row-copy' },
        h('div', { class: 'row-title' }, title),
        desc ? h('div', { class: 'row-desc' }, desc) : null));

    if (value !== null) {
        row.append(value instanceof Node ? value : h('span', { class: 'row-value' }, String(value)));
    }

    if (action) {
        row.append(action);
    }

    if (onClick) {
        if (!action && !danger) {
            row.append(icon('chevron', 'chevron'));
        }

        row.addEventListener('click', () => {
            try {
                const result = onClick();
                if (result && typeof result.catch === 'function') {
                    result.catch((error) => toastError(error));
                }
            } catch (error) {
                toastError(error);
            }
        });
    }

    return row;
}

function estimateLocalCache() {
    let bytes = 0;
    let pages = 0;

    try {
        for (let index = 0; index < localStorage.length; index += 1) {
            const key = localStorage.key(index);
            if (!key) {
                continue;
            }

            if (key.startsWith('nbut.cache.') || key === KEYS.drafts) {
                const value = localStorage.getItem(key) || '';
                bytes += (key.length + value.length) * 2;

                if (key.startsWith(KEYS.pagePrefix)) {
                    pages += 1;
                }
            }
        }
    } catch {
        // Storage inspection is best effort.
    }

    return { bytes, pages };
}

function clearLocalCaches() {
    const keys = [];

    try {
        for (let index = 0; index < localStorage.length; index += 1) {
            const key = localStorage.key(index);
            if (key && (key.startsWith('nbut.cache.') || key === KEYS.drafts)) {
                keys.push(key);
            }
        }
    } catch {
        // Fall through and clear the known keys.
    }

    for (const key of keys) {
        storage.remove(key);
    }

    storage.remove(KEYS.pages);
    storage.remove(KEYS.files);
    storage.remove(KEYS.trash);
    storage.remove(KEYS.drafts);
    state.pagesCache = [];
    state.filesCache = [];
}

async function clearCachedData() {
    const confirmed = await confirmDialog({
        title: 'Clear offline data?',
        message: 'Cached pages, drafts, and the file list saved on this phone will be removed. Your notes on the desktop are not affected.',
        confirmLabel: 'Clear data',
        danger: true
    });

    if (!confirmed) {
        return false;
    }

    clearLocalCaches();
    toast('Offline data cleared.', { type: 'success' });
    return true;
}

function openInstallHelp() {
    openSheet({
        title: 'Add to Home Screen',
        content: h('div', { class: 'sheet-form' },
            h('p', { class: 'muted', style: { fontSize: '13px' } },
                'Install noted-x for a full-screen, app-like experience. It still runs entirely on your local network.'),
            pairStep(1, isIos() ? 'Open this page in Safari.' : 'Open this page in Chrome or your browser.'),
            pairStep(2, isIos() ? 'Tap the Share button.' : 'Open the browser menu.'),
            pairStep(3, isIos() ? 'Choose “Add to Home Screen”.' : 'Choose “Install app” or “Add to Home screen”.'))
    });
}

async function promptInstall() {
    const prompt = state.installPrompt;

    if (!prompt) {
        openInstallHelp();
        return;
    }

    state.installPrompt = null;

    try {
        await prompt.prompt();
        const choice = await prompt.userChoice;
        toast(choice?.outcome === 'accepted' ? 'Installing noted-x…' : 'Install dismissed.', {
            type: choice?.outcome === 'accepted' ? 'success' : 'info'
        });
    } catch (error) {
        console.warn('The install prompt could not be shown.', error);
        openInstallHelp();
    }
}

async function unpairDevice() {
    const confirmed = await confirmDialog({
        title: 'Unpair this device?',
        message: 'This phone will forget its pairing token and cached notes. Scan a fresh QR code on the desktop to reconnect.',
        confirmLabel: 'Unpair',
        danger: true
    });

    if (!confirmed) {
        return;
    }

    const deviceId = state.session?.deviceId;
    if (deviceId && navigator.onLine) {
        try {
            await api(`/api/devices/${deviceId}`, { method: 'DELETE' });
        } catch (error) {
            if (!isAuthError(error)) {
                console.warn('Could not revoke this device before unpairing.', error);
            }
        }
    }

    stopRealtime();
    clearSession();
    clearLocalCaches();
    sessionExpiredHandled = false;
    sessionStore.remove(KEYS.pairToken);

    try {
        history.replaceState(null, '', '/');
    } catch {
        // The address bar is cosmetic here.
    }

    showPairing(null, { notice: 'This device has been unpaired.' });
}

async function renderSettings(seq) {
    beginView('settings');
    setTabsOff(false);
    showTabbar('settings');
    dom.app.hidden = false;
    setAppBar({ title: 'Settings', subtitle: 'Local network edition', status: true });
    document.title = 'Settings · noted-x';

    const content = h('div', { class: 'settings' });
    dom.view.replaceChildren(content);

    const controller = new AbortController();
    let devices = null;
    let devicesLoading = true;
    let devicesError = null;

    const paint = () => {
        if (seq !== routeSeq) {
            return;
        }

        content.replaceChildren(
            section('Appearance', appearanceCard()),
            section('Connection', connectionCard()),
            section('This device', deviceCard()),
            section('Paired devices', devicesCard()),
            section('Storage', storageCard()),
            section('App', appCard()),
            aboutCard(),
            unpairCard());
        updateStatusIndicators();
    };

    function section(title, card) {
        return h('section', {},
            h('div', { class: 'settings-section-title' }, title),
            card);
    }

    function appearanceCard() {
        const segmented = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Theme' });

        for (const [value, label] of [['light', 'Light'], ['dark', 'Dark'], ['system', 'Auto']]) {
            const button = h('button', {
                type: 'button',
                class: state.themePreference === value ? 'active' : '',
                'aria-pressed': state.themePreference === value ? 'true' : 'false'
            }, label);

            button.addEventListener('click', () => {
                setThemePreference(value);
                paint();
            });

            segmented.append(button);
        }

        return h('div', { class: 'card' }, settingsRow({
            iconName: 'sun',
            title: 'Theme',
            desc: 'Match this phone or choose a look.',
            action: segmented
        }));
    }

    function connectionCard() {
        return h('div', { class: 'card' },
            settingsRow({
                iconName: 'wifi',
                title: 'Live updates',
                desc: 'Realtime changes over Wi-Fi',
                accent: true,
                value: h('span', {
                    class: 'status-pill',
                    dataset: { role: 'connection', state: connectionState() }
                }, connectionLabel())
            }),
            settingsRow({
                iconName: 'retry',
                title: 'Reconnect now',
                desc: window.location.origin,
                onClick: () => {
                    restartRealtime();
                    toast('Reconnecting…', { type: 'info' });
                }
            }),
            settingsRow({
                iconName: 'info',
                title: 'Connection details',
                onClick: () => openConnectionSheet()
            }));
    }

    function deviceCard() {
        return h('div', { class: 'card' },
            settingsRow({
                iconName: 'todo',
                title: state.session?.deviceName || 'This phone',
                desc: state.session?.pairedAt
                    ? `Paired ${formatDate(state.session.pairedAt)}`
                    : 'Paired device',
                value: 'This device'
            }),
            settingsRow({
                iconName: 'trash',
                title: 'Trash',
                desc: 'Restore or permanently delete pages',
                onClick: () => navigate('#/trash')
            }));
    }

    function devicesCard() {
        const card = h('div', { class: 'card' });

        if (!devices && devicesLoading) {
            card.append(h('div', { class: 'row' },
                h('div', { class: 'row-icon' }, icon('wifi')),
                h('div', { class: 'row-copy' },
                    h('div', { class: 'row-title' }, 'Loading devices…'),
                    h('div', { class: 'row-desc' }, 'Checking the desktop'))));
            return card;
        }

        if (!devices && devicesError) {
            card.append(settingsRow({
                iconName: 'alert',
                title: 'Could not load devices',
                desc: describeError(devicesError, 'Try again.'),
                onClick: () => loadDevices()
            }));
            return card;
        }

        const list = (devices || []).filter((device) => !device.isRevoked);

        if (list.length === 0) {
            card.append(h('div', { class: 'row' },
                h('div', { class: 'row-icon' }, icon('info')),
                h('div', { class: 'row-copy' },
                    h('div', { class: 'row-title' }, 'No paired devices'),
                    h('div', { class: 'row-desc' }, 'Pair a phone from the desktop app.'))));
            return card;
        }

        for (const device of list) {
            const current = state.session && sameId(device.id, state.session.deviceId);
            card.append(settingsRow({
                iconName: 'wifi',
                title: device.name || 'Device',
                desc: current
                    ? `This device · paired ${formatDate(device.createdAt)}`
                    : device.lastSeenAt
                        ? `Last seen ${relativeTime(device.lastSeenAt)}`
                        : `Paired ${formatDate(device.createdAt)}`,
                value: current ? 'This device' : null,
                action: current
                    ? null
                    : iconButton('close', `Revoke ${device.name || 'device'}`, () => revokeDevice(device), 'danger')
            }));
        }

        return card;
    }

    function storageCard() {
        const estimate = estimateLocalCache();
        const clear = h('button', {
            class: 'btn btn-secondary',
            type: 'button',
            style: { minHeight: '34px', padding: '0 12px', fontSize: '12.5px' }
        }, 'Clear');

        clear.addEventListener('click', () => {
            void clearCachedData().then((cleared) => {
                if (cleared) {
                    paint();
                }
            });
        });

        return h('div', { class: 'card' },
            settingsRow({
                iconName: 'folder',
                title: 'Offline cache',
                desc: `${estimate.pages} ${estimate.pages === 1 ? 'page' : 'pages'} · ${formatBytes(estimate.bytes)}`,
                action: clear
            }),
            settingsRow({
                iconName: 'retry',
                title: 'Sync now',
                desc: 'Refresh notes and devices from the desktop',
                onClick: () => {
                    void prefetchPages();
                    void loadDevices();
                    toast('Refreshing from the desktop…', { type: 'info' });
                }
            }));
    }

    function appCard() {
        const card = h('div', { class: 'card' });

        if (state.installPrompt) {
            card.append(settingsRow({
                iconName: 'download',
                title: 'Install on this phone',
                desc: 'Add noted-x to your home screen.',
                onClick: () => promptInstall()
            }));
        } else if (isStandalone()) {
            card.append(settingsRow({
                iconName: 'check',
                title: 'Installed',
                desc: 'Running as an app on this phone.'
            }));
        } else {
            card.append(settingsRow({
                iconName: 'plus',
                title: 'Add to Home Screen',
                desc: 'Open noted-x like a native app.',
                onClick: () => openInstallHelp()
            }));
        }

        const worker = 'serviceWorker' in navigator && window.isSecureContext;
        card.append(settingsRow({
            iconName: worker ? 'check' : 'info',
            title: 'Offline app shell',
            desc: worker
                ? 'The interface is cached for quick opening.'
                : 'Browsers only allow app caching over HTTPS or localhost.',
            value: worker ? 'Ready' : 'HTTP'
        }));

        return card;
    }

    function aboutCard() {
        return h('div', { class: 'about' },
            h('img', { class: 'about-logo', src: '/icons/logo.png', alt: 'noted-x' }),
            h('span', {}, 'Local network edition'),
            h('span', {}, 'No cloud · No telemetry · No accounts'));
    }

    function unpairCard() {
        return h('div', { class: 'card' },
            settingsRow({
                iconName: 'close',
                title: 'Unpair this device',
                desc: 'Remove the saved token and cached notes from this phone',
                danger: true,
                onClick: () => unpairDevice()
            }));
    }

    async function loadDevices() {
        if (!navigator.onLine) {
            devicesLoading = false;
            devicesError = new ApiError(0, 'Offline', 'Device details are only available online.');
            paint();
            return;
        }

        if (!devices) {
            devicesLoading = true;
            devicesError = null;
            paint();
        }

        try {
            const items = await apiJson('/api/devices', { signal: controller.signal });
            if (seq !== routeSeq) {
                return;
            }

            devices = Array.isArray(items) ? items : [];
            devicesError = null;
        } catch (error) {
            if (isAbort(error) || isAuthError(error) || seq !== routeSeq) {
                return;
            }

            devicesError = error;
        } finally {
            if (seq === routeSeq) {
                devicesLoading = false;
                paint();
            }
        }
    }

    async function revokeDevice(device) {
        const confirmed = await confirmDialog({
            title: 'Revoke this device?',
            message: `“${device.name || 'This device'}” will lose access immediately. Pair it again from the desktop to restore access.`,
            confirmLabel: 'Revoke',
            danger: true
        });

        if (!confirmed) {
            return;
        }

        try {
            await api(`/api/devices/${device.id}`, { method: 'DELETE' });
            toast('Device revoked.', { type: 'success' });
            await loadDevices();
        } catch (error) {
            toastError(error, 'Could not revoke the device.');
        }
    }

    state.disposeCurrent = () => controller.abort();
    state.liveRefresh = () => void loadDevices();
    state.liveOnline = () => {
        updateNetworkBanner();
        if (navigator.onLine) {
            void loadDevices();
        }
    };

    paint();
    void loadDevices();
}

// ---------- Trash ----------

async function renderTrash(seq) {
    beginView('trash');
    setTabsOff(true);
    hideTabbar();
    dom.app.hidden = false;
    setAppBar({ back: '#/home', title: 'Trash', subtitle: 'Deleted pages', status: true });
    document.title = 'Trash · noted-x';

    const cached = storage.get(KEYS.trash, []);
    let pages = Array.isArray(cached) ? cached : [];
    let loading = pages.length === 0;
    let stale = false;

    const content = h('div');
    dom.view.replaceChildren(content);
    const controller = new AbortController();

    const paint = () => {
        if (seq !== routeSeq) {
            return;
        }

        content.replaceChildren();

        if (stale) {
            content.append(h('div', { class: 'notice warn', style: { marginBottom: '14px' } },
                icon('cloud-off'),
                h('span', {}, 'Showing the saved trash list. Reconnect to the desktop to refresh it.')));
        }

        if (loading) {
            content.append(skeletonPageList(3));
            return;
        }

        if (pages.length === 0) {
            content.append(emptyState({
                iconName: 'trash',
                title: 'Trash is empty',
                message: 'Pages you delete stay here until you remove them permanently.',
                actionLabel: 'Back to notes',
                onAction: () => navigate('#/home')
            }));
            return;
        }

        const list = h('div', { class: 'page-list' });
        for (const page of pages) {
            const actions = h('div', { class: 'trash-actions' },
                iconButton('restore', `Restore ${page.title || 'page'}`, () => void restorePage(page)),
                iconButton('trash', `Delete ${page.title || 'page'} forever`, () => void deletePage(page), 'danger'));
            list.append(pageRow(page, { trash: true, actions }));
        }

        content.append(h('section', { class: 'section' },
            h('div', { class: 'section-head' },
                h('div', { class: 'section-title' }, 'Deleted pages'),
                h('span', { class: 'section-count' }, String(pages.length))),
            list));
    };

    const load = async (showSpinner = false) => {
        if (!navigator.onLine) {
            loading = false;
            stale = pages.length > 0;
            paint();
            return;
        }

        if (showSpinner && pages.length === 0) {
            loading = true;
            paint();
        }

        try {
            const items = await apiJson('/api/pages?includeDeleted=true', { signal: controller.signal });
            if (seq !== routeSeq) {
                return;
            }

            pages = (Array.isArray(items) ? items : []).filter((page) => page.isDeleted);
            storage.set(KEYS.trash, pages);
            loading = false;
            stale = false;
            paint();
        } catch (error) {
            if (isAbort(error) || isAuthError(error) || seq !== routeSeq) {
                return;
            }

            loading = false;
            stale = pages.length > 0;
            paint();

            if (pages.length === 0) {
                toastError(error, 'Could not load the trash.');
            }
        }
    };

    async function restorePage(page) {
        try {
            await api(`/api/pages/${page.id}/restore`, { method: 'POST' });
            pages = pages.filter((item) => !sameId(item.id, page.id));
            storage.set(KEYS.trash, pages);
            void prefetchPages();
            toast('Page restored.', { type: 'success' });
            paint();
        } catch (error) {
            toastError(error, 'Could not restore the page.');
        }
    }

    async function deletePage(page) {
        const confirmed = await confirmDialog({
            title: 'Delete forever?',
            message: `“${page.title || 'Untitled'}” and its subpages will be permanently removed. This cannot be undone.`,
            confirmLabel: 'Delete forever',
            danger: true
        });

        if (!confirmed) {
            return;
        }

        try {
            await api(`/api/pages/${page.id}`, { method: 'DELETE' });
            pages = pages.filter((item) => !sameId(item.id, page.id));
            storage.set(KEYS.trash, pages);
            toast('Page deleted permanently.', { type: 'success' });
            paint();
        } catch (error) {
            toastError(error, 'Could not delete the page.');
        }
    }

    state.disposeCurrent = () => controller.abort();
    state.liveRefresh = debounce(() => void load(false), 300);
    state.liveOnline = () => {
        updateNetworkBanner();
        if (navigator.onLine) {
            void load(false);
        }
    };

    paint();
    void load(true);
}

// ---------- Startup ----------

function updateNetworkBanner() {
    if (dom.offlineBanner) {
        dom.offlineBanner.hidden = navigator.onLine;
    }

    updateStatusIndicators();
}

function handleOnline() {
    updateNetworkBanner();
    resumeAutoTransfers();

    if (state.session && !hubConnection) {
        startRealtime();
    }

    state.liveOnline?.();
    state.liveRefresh?.();
}

function handleOffline() {
    updateNetworkBanner();

    for (const transfer of Array.from(transfers.values())) {
        if (transfer.status === 'uploading' || transfer.status === 'downloading') {
            pauseTransfer(transfer, {
                autoResume: true,
                reason: 'Waiting for the network',
                silent: true
            });
        }
    }

    state.liveOnline?.();
}

function registerServiceWorker() {
    if (!('serviceWorker' in navigator) || !window.isSecureContext) {
        return;
    }

    const register = () => {
        navigator.serviceWorker.register('/service-worker.js').catch((error) => {
            console.warn('The service worker could not be registered.', error);
        });
    };

    if (document.readyState === 'complete') {
        register();
    } else {
        window.addEventListener('load', register, { once: true });
    }
}

function wireGlobalEvents() {
    window.addEventListener('hashchange', () => {
        if (state.currentView === 'pair') {
            return;
        }

        void route();
    });

    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);

    window.addEventListener('beforeinstallprompt', (event) => {
        event.preventDefault();
        state.installPrompt = event;
    });

    window.addEventListener('appinstalled', () => {
        state.installPrompt = null;
        toast('noted-x is installed.', { type: 'success' });
    });

    window.addEventListener('scroll', () => {
        dom.appbar?.classList.toggle('scrolled', window.scrollY > 4);
    }, { passive: true });

    themeMedia.addEventListener('change', () => {
        if (state.themePreference === 'system') {
            applyTheme();
        }
    });

    const dock = dom.uploadDock;
    if (dock) {
        dock.addEventListener('click', () => navigate('#/files'));
        dock.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                navigate('#/files');
            }
        });
    }
}

function boot() {
    initializeTheme();
    if (typeof state.searchQuery !== 'string') {
        state.searchQuery = '';
    }

    const pairToken = capturePairToken();
    if (window.location.hash && window.location.hash.startsWith('#/')) {
        pendingHash = window.location.hash;
    }

    wireGlobalEvents();
    registerServiceWorker();
    updateNetworkBanner();
    processQueue();

    if (pairToken) {
        showPairing(pairToken, { cancelable: Boolean(state.session) });
        return;
    }

    if (state.session) {
        pendingHash = null;

        if (!window.location.hash) {
            try {
                history.replaceState(null, '', '/#/home');
            } catch {
                // Routing still defaults to home without the hash.
            }
        }

        startRealtime();
        void route();
        void prefetchPages();
        return;
    }

    const storedToken = storedPairToken();
    showPairing(storedToken, {
        notice: storedToken
            ? ''
            : 'Open the pairing link from the desktop app to connect this phone.'
    });
}

boot();

