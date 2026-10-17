// The register's keyboard rules. One listener on window in the capture phase: it sees every key before Blazor does.
(() => {
  // A cash, quantity or price field ([data-select-on-focus]) selects its value on focus, so typing replaces it. A listener, not an
  // inline onfocus: the Lite app's CSP forbids inline handlers (TK-324).
  document.addEventListener('focusin', e => { if ((e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) && e.target.matches('[data-select-on-focus]')) e.target.select(); });

  const topOverlay = () => {
    const all = document.querySelectorAll('.esc-overlay');
    return all.length ? all[all.length - 1] : null;
  };

  // A barcode scanner types like a very fast keyboard and ends with Enter. Inside a PIN, quantity or cash field ([data-no-scan])
  // that burst is not a value: it is undone and its Enter dropped, so a scan by habit never confirms a payment of
  // RD$ 7,460,000,000,017 or spends PIN attempts (principle 2: "se ignora con aviso").
  const BURST_GAP_MS = 50, BURST_MIN = 6;
  const burst = { count: 0, last: 0, target: null, value: null };

  let toastTimer;
  const toast = text => {
    let t = document.getElementById('esc-toast');
    if (!t) {
      t = document.createElement('div');
      t.id = 'esc-toast';
      t.className = 'esc-toast';
      t.setAttribute('role', 'status');
      document.body.appendChild(t);
    }
    t.textContent = text;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, 4000);
  };

  const undoBurst = target => {
    if (burst.value !== null && 'value' in target) {
      target.value = burst.value;
      target.dispatchEvent(new Event('input', { bubbles: true })); // Blazor's binding takes the old value back
    } else {
      // A PIN pad: its digits went to Blazor one by one, so they are erased the same way.
      for (let i = 0; i < burst.count; i++) target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Backspace', bubbles: true }));
    }
  };

  window.addEventListener('keydown', e => {
    // Function keys belong to the register, not to the browser (F5 would reload in the middle of a sale).
    if (/^F([1-9]|1[0-2])$/.test(e.key)) e.preventDefault();
    const plain = !e.ctrlKey && !e.altKey && !e.metaKey;

    if (e.key.length === 1 && plain) {
      if (e.timeStamp - burst.last > BURST_GAP_MS || burst.target !== e.target) {
        burst.count = 0;
        burst.target = e.target;
        burst.value = 'value' in e.target ? e.target.value : null;
      }
      burst.count++;
      burst.last = e.timeStamp;
    } else if (e.key === 'Enter') {
      const scanned = burst.count >= BURST_MIN && e.timeStamp - burst.last <= BURST_GAP_MS && burst.target === e.target;
      burst.count = 0;
      if (scanned && e.target.closest && e.target.closest('[data-no-scan]')) {
        e.preventDefault();
        e.stopImmediatePropagation();
        undoBurst(e.target);
        toast('Se ignoró un escaneo: termine aquí y luego escanee en la venta.');
        return;
      }
      // Enter on a focused button only presses that button; the screen's own Enter (confirm) must not run as well.
      if (e.target.tagName === 'BUTTON') {
        e.stopImmediatePropagation();
        return;
      }
    }

    // Tab stays inside the open dialog.
    if (e.key === 'Tab') {
      const overlay = topOverlay();
      if (!overlay) return;
      const items = [...overlay.querySelectorAll('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex="0"]')];
      if (!items.length) return;
      const first = items[0], last = items[items.length - 1], active = document.activeElement;
      if (!overlay.contains(active) || (e.shiftKey && active === first) || (!e.shiftKey && active === last)) {
        e.preventDefault();
        (e.shiftKey ? last : first).focus();
      }
      return;
    }

    // Nothing has the focus (a tap on a cart line or on a disabled button leaves it on <body>): the key still reaches the screen.
    if (e.target === document.body && plain) {
      const overlay = topOverlay();
      const scanner = overlay ? null : document.querySelector('[data-scanner]');
      if (scanner && (e.key.length === 1 || e.key === 'F2')) {
        scanner.focus(); // focused during keydown, so the character lands in the scanner
        if (e.key === 'F2') e.stopImmediatePropagation();
        return;
      }
      // No screen keys (sign-in, setup): the frame still gets F1 for the help (TK-242).
      const owner = overlay || document.querySelector('[data-keys]') || document.querySelector('[data-keys-root]');
      if (!owner) return;
      e.stopImmediatePropagation();
      owner.dispatchEvent(new KeyboardEvent('keydown', { key: e.key, code: e.code, shiftKey: e.shiftKey, bubbles: true, cancelable: true }));
    }
  }, true);

  // Admin dark mode (TK-183): the choice ("light", "dark" or none = follow the system) is kept per device; <html data-theme> is always the
  // resolved theme, set before Blazor renders so the page never flashes light.
  const THEME_KEY = 'abastalia.theme';
  const darkQuery = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  let unsavedTheme = null; // storage blocked: the choice lasts until the page reloads
  const storedTheme = () => { try { return localStorage.getItem(THEME_KEY); } catch { return unsavedTheme; } };
  const applyTheme = () => {
    const choice = storedTheme();
    document.documentElement.dataset.theme = choice === 'light' || choice === 'dark' ? choice : (darkQuery && darkQuery.matches ? 'dark' : 'light');
  };
  applyTheme();
  if (darkQuery) darkQuery.addEventListener('change', applyTheme);

  window.escuadra = {
    // "system", "light" or "dark".
    getTheme: () => storedTheme() || 'system',
    setTheme: choice => {
      unsavedTheme = choice === 'light' || choice === 'dark' ? choice : null;
      try {
        if (unsavedTheme) localStorage.setItem(THEME_KEY, unsavedTheme);
        else localStorage.removeItem(THEME_KEY);
      } catch { /* kept in unsavedTheme */ }
      applyTheme();
    },
    // Whether a dialog is open (the register does not close to update in the middle of one).
    hasDialog: () => document.querySelector('.esc-overlay') !== null,
    // HTML autofocus only works on page load; a dialog Blazor inserts later focuses its field here.
    focusFirst: el => {
      // A screen may focus its own field first (Blazor Server runs this after the client acknowledges the render): keep it.
      if (!el || el.contains(document.activeElement)) return;
      const field = el.querySelector('[autofocus]') || el.querySelector('input:not([type=hidden]):not([disabled]), select, textarea');
      (field || el).focus();
    },
    // A file the API answered (a report): the browser saves it; in the store app (WebView2) the window asks where (MainWindow).
    download: (name, type, bytes) => {
      const url = URL.createObjectURL(new Blob([bytes], { type }));
      const a = document.createElement('a');
      a.href = url;
      a.download = name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    }
  };
})();
