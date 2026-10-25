/* Platform Connector UI: shared helpers. Classic script (CSP script-src 'self'); everything hangs off window.PC.
   Renderers are pure functions state -> HTML string, so tests run them without a DOM. */
(() => {
  const PC = window.PC = window.PC || {};

  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  const attr = (name, value) => value === undefined || value === null || value === false ? '' : ` ${name}="${esc(value === true ? name : value)}"`;

  const svg = (size, body, stroke = '1.8') => `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
  // Icons are the drawings of the approved mockup (24 px grid).
  const icons = {
    home: '<path d="M3 10.5 12 3l9 7.5V21h-6v-6H9v6H3z"></path>',
    jobs: '<path d="M10 6h10M10 12h10M10 18h10"></path><path d="m3.5 6 1.5 1.5L7.5 5M3.5 12l1.5 1.5L7.5 11M3.5 18l1.5 1.5L7.5 17"></path>',
    tekla: '<path d="M12 2.5 3.5 7v10l8.5 4.5 8.5-4.5V7z"></path><path d="M3.5 7 12 11.5 20.5 7M12 11.5V21.5"></path>',
    autocad: '<path d="M12 3v4M12 7 6 21M12 7l6 14M8.2 15.5h7.6"></path>',
    folders: '<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2h8.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z"></path>',
    converters: '<path d="M4 8h13l-3-3M20 16H7l3 3"></path>',
    agr: '<path d="m12 3 9 5-9 5-9-5z"></path><path d="m3 13 9 5 9-5"></path>',
    settings: '<circle cx="12" cy="12" r="3.2"></circle><path d="M12 2.5v3M12 18.5v3M4.6 4.6l2.1 2.1M17.3 17.3l2.1 2.1M2.5 12h3M18.5 12h3M4.6 19.4l2.1-2.1M17.3 6.7l2.1-2.1"></path>',
    services: '<circle cx="6" cy="12" r="2.5"></circle><circle cx="18" cy="6" r="2.5"></circle><circle cx="18" cy="18" r="2.5"></circle><path d="m8.2 10.9 7.6-3.8M8.2 13.1l7.6 3.8"></path>',
    attributes: '<path d="M3 12V4.5A1.5 1.5 0 0 1 4.5 3H12l9 9-9 9z"></path><circle cx="7.5" cy="7.5" r="1.5"></circle>',
    exports: '<path d="M12 4v11M7.5 10.5 12 15l4.5-4.5M4 19h16"></path>',
    user: '<circle cx="12" cy="8" r="4"></circle><path d="M4 21c1.5-4 4.5-6 8-6s6.5 2 8 6"></path>'
  };
  const icon = (name, size = 18) => svg(size, icons[name] || '');
  const logo = (size, stroke = 2.6, ink = 'var(--markInk)') => `<svg width="${size}" height="${size}" viewBox="0 0 28 28" aria-hidden="true"><rect x="1" y="1" width="26" height="26" rx="7" fill="var(--mark)"></rect><path d="M8 19V9h6.5a3.5 3.5 0 0 1 0 7H8" fill="none" stroke="${ink}" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round"></path><circle cx="19.5" cy="19" r="${stroke > 2.5 ? 1.9 : 1.8}" fill="${ink}"></circle></svg>`;
  const searchIcon = size => `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="11" cy="11" r="7"></circle><path d="m20 20-3.5-3.5"></path></svg>`;

  /* Buttons. A native command is data-cmd, a UI action is data-ui; data-* extras become payload/args. */
  const button = (label, cls, opts = {}) => {
    const data = Object.entries(opts.data || {}).map(([k, v]) => attr('data-' + k, v)).join('');
    return `<button type="button" class="${cls}"${attr('data-cmd', opts.cmd)}${attr('data-ui', opts.ui)}${data}${attr('disabled', !!opts.disabled)}${attr('aria-label', opts.aria)}${attr('style', opts.style)}${attr('title', opts.title)}>${opts.html ?? esc(label)}</button>`;
  };
  const chip = (text, tone, style = '') => `<span class="chip ${tone}"${attr('style', style)}>${esc(text)}</span>`;
  const dot = tone => `<span class="dot dot-${tone}"></span>`;
  const tones = { ok: 't-ok', run: 't-run', warn: 't-warn', bad: 't-bad', idle: 't-idle', error: 't-bad', info: 't-idle', progress: 't-run' };

  // Russian plural: 1 сеанс, 2 сеанса, 5 сеансов.
  const plural = (n, one, few, many) => {
    const m10 = n % 10, m100 = n % 100;
    return m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
  };

  Object.assign(PC, { esc, attr, icon, logo, searchIcon, button, chip, dot, tones, plural });
})();
