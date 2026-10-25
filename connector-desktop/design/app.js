/* Platform Connector UI: DOM wiring. Renders PC.shell.app(state) into #app, routes clicks to native commands
   (data-cmd) or UI actions (data-ui), and turns native notices into toasts and page notes. */
(() => {
  const PC = window.PC, M = PC.model, S = PC.sections;
  const state = PC.state = M.create();
  const root = document.getElementById('app');
  const TOAST_TIME = { ok: 4000, info: 4000, warn: 8000 };
  let renderPending = false;

  /* ---- rendering ------------------------------------------------------------------------------------ */
  function render() {
    const active = document.activeElement;
    if (active && active.tagName === 'SELECT' && root.contains(active)) { renderPending = true; return; }
    renderPending = false;
    const focusId = active && root.contains(active) ? active.id : '';
    const focusKey = active && root.contains(active) && !focusId ? keyOf(active) : '';
    const caret = active && typeof active.selectionStart === 'number' ? [active.selectionStart, active.selectionEnd] : null;
    const scroll = root.querySelector('.main')?.scrollTop || 0;
    if (!S.isVisible(state, state.ui.page)) state.ui.page = 'home';
    document.documentElement.dataset.theme = state.theme;
    root.innerHTML = PC.shell.app(state);
    const mainEl = root.querySelector('.main');
    if (mainEl) mainEl.scrollTop = scroll;
    const target = focusId ? document.getElementById(focusId) : focusKey ? [...root.querySelectorAll('[data-cmd],[data-ui]')].find(el => keyOf(el) === focusKey) : null;
    if (target && !target.disabled) {
      target.focus({ preventScroll: true });
      if (caret && typeof target.setSelectionRange === 'function') try { target.setSelectionRange(...caret); } catch { /* not a text field */ }
    }
    if (state.ui.palette.open && !root.contains(document.activeElement)) document.getElementById('palette-input')?.focus();
    const entry = S.byId(state.ui.page);
    document.title = 'Платформа Коннектор' + (state.snapshotSeen && !M.connection(state).showSignIn && entry ? ' · ' + entry.title : '');
  }
  const keyOf = el => [el.dataset.cmd, el.dataset.ui, el.dataset.id, el.dataset.page, el.dataset.target].join('|');
  const dataOf = el => Object.fromEntries(el.getAttributeNames().filter(name => name.startsWith('data-')).map(name => [name.slice(5), el.getAttribute(name)]));

  /* ---- notices -------------------------------------------------------------------------------------- */
  function handleNotice(notice) {
    if (!notice) return;
    notice.at = Date.now();
    notice.page = S.ownerOf(notice.command)?.id || state.ui.page;
    if (notice.kind === 'progress') return;
    const id = String(notice.seq);
    state.toasts = [...state.toasts.filter(t => t.id !== id), { id, kind: notice.kind, message: notice.message }].slice(-4);
    if (TOAST_TIME[notice.kind]) setTimeout(() => { closeToast(id); }, TOAST_TIME[notice.kind]);
  }
  function closeToast(id) {
    const before = state.toasts.length;
    state.toasts = state.toasts.filter(t => t.id !== id);
    if (state.toasts.length !== before) render();
  }

  /* ---- native commands ------------------------------------------------------------------------------ */
  function runCommand(command, data = {}, files = null) {
    const bridge = window.CONNECTOR_DESKTOP_BRIDGE;
    const sectionId = data.section || state.ui.page;
    const entry = S.byId(sectionId);
    const product = S.productFor(state, sectionId, command);
    const payload = {
      mode: product,
      page: command.startsWith('bridge-') ? 'bridge' : entry?.legacyPage || 'overview',
      converter: state.ui.conv === 'fbx' ? 'fbx' : 'ifc',
      tekla: state.ui.teklaTab,
      fields: {}
    };
    // data-field-* attributes are strings; «true»/«false» become booleans (the explicit flag of «confirm-answer»).
    for (const [key, value] of Object.entries(data)) if (key.startsWith('field-'))
      payload.fields[key.slice(6)] = value === 'true' ? true : value === 'false' ? false : value;
    if (data.target !== undefined) payload.target = data.target;
    if (data.index !== undefined) payload.index = String(data.index);
    if (!bridge) { handleNotice(M.localNotice(state, command, 'Платформа Коннектор не отвечает. Перезапустите приложение.')); render(); return; }
    bridge.invoke(command, payload, files);
  }

  /** Declines the native confirmation shown in the page (Escape, a click outside the dialog). */
  function declineConfirmation() {
    const c = state.confirmation;
    if (!c) return false;
    state.confirmation = null;
    render();
    runCommand('confirm-answer', { section: state.ui.page, target: c.id, 'field-accept': false });
    return true;
  }

  /* ---- UI actions ----------------------------------------------------------------------------------- */
  const closeOverlays = () => { state.ui.accountOpen = false; state.ui.dialog = null; state.ui.palette.open = false; };
  function go(page, extra = {}) {
    closeOverlays();
    state.ui.page = page;
    if (extra.tab) state.ui.teklaTab = extra.tab;
    if (extra.conv) state.ui.conv = extra.conv;
    root.querySelector('.main')?.scrollTo?.(0, 0);
  }
  function runPaletteItem(item) {
    if (!item) return;
    const run = item.run;
    if (run.ui === 'go') go(run.page);
    else if (run.ui === 'job') openJob(run.id);
    else if (run.ui === 'theme') setTheme(run.data.id);
    else {
      go(run.page, run);
      if (run.dialog) state.ui.dialog = { name: run.dialog, args: {} };
      else if (run.cmd) { render(); runCommand(run.cmd, { section: run.section, ...(run.data || {}) }); return; }
    }
    render();
  }
  function openJob(id) { go('jobs'); state.ui.jobId = id; state.ui.jobFilter = 'all'; state.ui.jobQuery = ''; }
  function setTheme(theme) {
    if (theme !== 'dark' && theme !== 'light') return;
    state.theme = theme;
    runCommand('ui-theme', { section: 'settings', 'field-ui-theme': theme });
  }

  function onUi(el, event) {
    const action = el.dataset.ui, id = el.dataset.id;
    switch (action) {
      case 'go': go(el.dataset.page); break;
      case 'job': openJob(id); break;
      case 'tab': state.ui.teklaTab = id; break;
      case 'conv': state.ui.conv = id; break;
      case 'filter': state.ui.jobFilter = id; break;
      case 'cad': state.ui.picks['cad-session'] = id; break;
      case 'theme': setTheme(id); break;
      case 'account': state.ui.accountOpen = !state.ui.accountOpen; break;
      case 'dialog': state.ui.accountOpen = false; state.ui.dialog = { name: el.dataset.dialog, args: { id } }; break;
      case 'close-dialog': if (declineConfirmation()) return; state.ui.dialog = null; break;
      case 'confirm': {
        const model = PC.shell.dialogModel(state);
        state.ui.dialog = null;
        render();
        if (model) runCommand(model.confirm.cmd, model.confirm);
        return;
      }
      case 'scrim-dialog': if (event.target !== el) return; if (declineConfirmation()) return; state.ui.dialog = null; break;
      case 'scrim-palette': if (event.target === el) state.ui.palette.open = false; else return; break;
      case 'palette': closeOverlays(); state.ui.palette = { open: true, query: '', index: 0 }; break;
      case 'palette-close': state.ui.palette.open = false; break;
      case 'palette-run': runPaletteItem(PC.shell.paletteItems(state)[Number(id)]); return;
      case 'toast-close': closeToast(id); return;
      case 'note-close': state.ui.dismissed[id] = true; break;
      case 'popover': case 'titlebar': case 'drive': case 'profile': case 'autostart': return;
      default: return;
    }
    render();
  }

  document.addEventListener('click', event => {
    const el = event.target.closest('[data-cmd],[data-ui]');
    let closed = false;
    if (state.ui.accountOpen && !event.target.closest('[data-ui="account"]') &&
        (!event.target.closest('[data-ui="popover"]') || (el && el.dataset.cmd))) { state.ui.accountOpen = false; closed = true; }
    if (!el || el.disabled || !root.contains(el)) { if (closed) render(); return; }
    if (el.dataset.cmd) {
      // The answer closes the native confirmation at once; the next snapshot confirms it.
      if (el.dataset.cmd === 'confirm-answer') { state.confirmation = null; closed = true; }
      runCommand(el.dataset.cmd, dataOf(el)); if (closed) render(); return;
    }
    onUi(el, event);
    if (closed && el.dataset.ui && ['popover', 'titlebar', 'drive', 'profile', 'autostart', 'toast-close'].includes(el.dataset.ui)) render();
  });
  document.addEventListener('change', event => {
    const el = event.target;
    if (el.dataset.ui === 'drive') { state.ui.picks.drives[el.dataset.id] = el.value; render(); }
    else if (el.dataset.ui === 'profile') { state.ui.picks['ifc-profile'] = el.value; render(); }
    else if (el.dataset.ui === 'autostart') runCommand('settings-store', { section: 'settings', 'field-autostart': el.checked });
    else if (renderPending) render();
  });
  document.addEventListener('focusout', () => { if (renderPending) setTimeout(() => { if (renderPending) render(); }, 0); });
  document.addEventListener('input', event => {
    const el = event.target;
    if (el.id === 'job-query') { state.ui.jobQuery = el.value; render(); }
    else if (el.id === 'palette-input') {
      state.ui.palette.query = el.value; state.ui.palette.index = 0;
      const list = document.getElementById('palette-list');
      if (list) list.innerHTML = PC.shell.paletteList(state);
    }
  });
  document.addEventListener('keydown', event => {
    const shell = state.snapshotSeen && !M.connection(state).showSignIn;
    if (shell && event.ctrlKey && !event.altKey && (event.code === 'KeyK' || event.key === 'k' || event.key === 'K')) {
      event.preventDefault(); closeOverlays(); state.ui.palette = { open: true, query: '', index: 0 }; render(); return;
    }
    if (event.key === 'Escape') {
      if (state.confirmation) { event.preventDefault(); declineConfirmation(); return; }
      if (state.ui.palette.open || state.ui.dialog || state.ui.accountOpen) { event.preventDefault(); closeOverlays(); render(); }
      return;
    }
    if (state.ui.palette.open && ['ArrowDown', 'ArrowUp', 'Enter'].includes(event.key)) {
      const items = PC.shell.paletteItems(state);
      event.preventDefault();
      if (event.key === 'Enter') { runPaletteItem(items[state.ui.palette.index]); return; }
      const step = event.key === 'ArrowDown' ? 1 : -1;
      state.ui.palette.index = items.length ? (state.ui.palette.index + step + items.length) % items.length : 0;
      const list = document.getElementById('palette-list');
      if (list) list.innerHTML = PC.shell.paletteList(state);
    }
  });

  /* ---- files dropped on the window ----------------------------------------------------------------- */
  // A drop anywhere else must not navigate the WebView to the file; a drop on a zone is a file selection.
  const dropZone = event => event.target instanceof Element ? event.target.closest('[data-drop]') : null;
  document.addEventListener('dragover', event => {
    event.preventDefault();
    const zone = dropZone(event);
    if (event.dataTransfer) event.dataTransfer.dropEffect = zone ? 'copy' : 'none';
    root.querySelectorAll('[data-drop].over').forEach(el => { if (el !== zone) el.classList.remove('over'); });
    if (zone) zone.classList.add('over');
  });
  document.addEventListener('dragleave', event => { const zone = dropZone(event); if (zone && !zone.contains(event.relatedTarget)) zone.classList.remove('over'); });
  document.addEventListener('drop', event => {
    event.preventDefault();
    const zone = dropZone(event);
    root.querySelectorAll('[data-drop].over').forEach(el => el.classList.remove('over'));
    const files = event.dataTransfer?.files;
    if (!zone || !files || !files.length) return;
    runCommand('drop-input', { section: 'converters', target: zone.dataset.drop }, files);
  });

  /* ---- native boundary ------------------------------------------------------------------------------ */
  window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE = (command, error) => {
    handleNotice(M.localNotice(state, command, String(error || 'Действие пока недоступно.'), 'error'));
    render();
  };
  window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT = snapshot => {
    handleNotice(M.apply(state, snapshot));
    render();
  };

  render();
  window.CONNECTOR_DESKTOP_BRIDGE?.flushSnapshots();
})();
