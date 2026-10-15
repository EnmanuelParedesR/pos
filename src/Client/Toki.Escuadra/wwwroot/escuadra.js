// The register's keyboard rules. One listener on window in the capture phase: it sees every key before Blazor does.
(() => {
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
      const owner = overlay || document.querySelector('[data-keys]');
      if (!owner) return;
      e.stopImmediatePropagation();
      owner.dispatchEvent(new KeyboardEvent('keydown', { key: e.key, code: e.code, shiftKey: e.shiftKey, bubbles: true, cancelable: true }));
    }
  }, true);

  window.escuadra = {
    // HTML autofocus only works on page load; a dialog Blazor inserts later focuses its field here.
    focusFirst: el => {
      if (!el) return;
      const field = el.querySelector('[autofocus]') || el.querySelector('input:not([type=hidden]):not([disabled]), select, textarea');
      (field || el).focus();
    }
  };
})();
