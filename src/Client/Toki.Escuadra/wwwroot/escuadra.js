// Function keys belong to the register, not to the browser (F5 would reload in the middle of a sale).
document.addEventListener('keydown', e => {
  if (/^F([1-9]|1[0-2])$/.test(e.key)) e.preventDefault();
}, true);

window.escuadra = {
  // HTML autofocus only works on page load; a dialog Blazor inserts later focuses its field here.
  enfocar: el => {
    if (!el) return;
    const campo = el.querySelector('[autofocus]') || el.querySelector('input:not([type=hidden]):not([disabled]), select, textarea');
    (campo || el).focus();
  }
};
