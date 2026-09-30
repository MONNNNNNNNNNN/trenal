// Terminal renderer for the iOS app. All I/O goes through the native bridge;
// this page never touches the network.
(() => {
  'use strict';

  const post = (msg) => window.webkit.messageHandlers.term.postMessage(JSON.stringify(msg));

  const store = {
    get(k, d) { try { return JSON.parse(localStorage.getItem(k)) ?? d; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* storage may be unavailable for file:// */ } },
  };

  const term = new Terminal({
    allowProposedApi: true, // unicode11 addon
    cursorBlink: true,
    fontFamily: 'ui-monospace, Menlo, monospace',
    fontSize: store.get('fontSize', 15),
    macOptionIsMeta: true,
    scrollback: 10000,
    theme: {
      background: '#1a1b26', foreground: '#c0caf5', cursor: '#c0caf5', selectionBackground: '#33467c',
      black: '#15161e', red: '#f7768e', green: '#9ece6a', yellow: '#e0af68',
      blue: '#7aa2f7', magenta: '#bb9af7', cyan: '#7dcfff', white: '#a9b1d6',
      brightBlack: '#737aa2', brightRed: '#f7768e', brightGreen: '#9ece6a', brightYellow: '#e0af68',
      brightBlue: '#7aa2f7', brightMagenta: '#bb9af7', brightCyan: '#7dcfff', brightWhite: '#c0caf5',
    },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11'; // correct widths for Thai combining marks, CJK, emoji

  const el = document.getElementById('term');
  term.open(el);
  if (term.textarea) {
    for (const [k, v] of Object.entries({ autocapitalize: 'off', autocorrect: 'off', autocomplete: 'off', spellcheck: 'false' })) {
      term.textarea.setAttribute(k, v);
    }
  }

  // --- sticky modifiers from the key bar -------------------------------------
  const latched = { ctrl: false, alt: false };
  const buttons = {};

  function setLatch(name, on) {
    latched[name] = on;
    buttons[name]?.classList.toggle('latched', on);
  }

  function withModifiers(data) {
    if (latched.ctrl && data.length === 1) {
      const c = data.toUpperCase().charCodeAt(0);
      if (c >= 64 && c <= 95) data = String.fromCharCode(c - 64);
      else if (data === ' ') data = '\x00';
    }
    if (latched.alt) data = '\x1b' + data;
    setLatch('ctrl', false);
    setLatch('alt', false);
    return data;
  }

  term.onData((data) => post({ t: 'i', d: withModifiers(data) }));
  term.onResize(({ cols, rows }) => post({ t: 'r', c: cols, r: rows }));
  term.onTitleChange((title) => post({ t: 'title', v: title }));

  const arrow = (c) => (term.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[') + c;
  const send = (data) => post({ t: 'i', d: withModifiers(data) });

  function zoom(delta) {
    const size = Math.min(28, Math.max(9, term.options.fontSize + delta));
    term.options.fontSize = size;
    store.set('fontSize', size);
    fit.fit();
  }

  const keys = [
    ['esc', () => send('\x1b')],
    ['tab', () => send('\t')],
    ['ctrl', () => setLatch('ctrl', !latched.ctrl), 'ctrl'],
    ['alt', () => setLatch('alt', !latched.alt), 'alt'],
    ['←', () => send(arrow('D'))],
    ['↑', () => send(arrow('A'))],
    ['↓', () => send(arrow('B'))],
    ['→', () => send(arrow('C'))],
    null,
    ...['|', '$', '-', '/', '\\', '~', '`', '{', '}', '[', ']', '(', ')', '@', '"', "'"].map((ch) => [ch, () => send(ch)]),
    null,
    ['^C', () => post({ t: 'i', d: '\x03' })],
    ['A−', () => zoom(-1)],
    ['A+', () => zoom(+1)],
  ];

  const bar = document.getElementById('bar');
  for (const k of keys) {
    if (!k) {
      bar.appendChild(Object.assign(document.createElement('span'), { className: 'gap' }));
      continue;
    }
    const [label, action, latch] = k;
    const b = document.createElement('button');
    b.textContent = label;
    // pointerdown + preventDefault keeps focus (and the keyboard) on the terminal.
    b.addEventListener('pointerdown', (e) => { e.preventDefault(); action(); term.focus(); });
    if (latch) buttons[latch] = b;
    bar.appendChild(b);
  }

  new ResizeObserver(() => fit.fit()).observe(el);
  el.addEventListener('pointerup', () => term.focus());

  window.trenal = {
    write: (data) => term.write(data),
    setHardwareKeyboard: (on) => {
      document.body.classList.toggle('hw', !!on);
      fit.fit();
    },
  };

  fit.fit();
  term.focus();
  post({ t: 'ready', c: term.cols, r: term.rows });
})();
