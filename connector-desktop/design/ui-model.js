/* Platform Connector UI: state and the reading of native snapshots.
   Only what the controller sends is shown; nothing here invents data for a missing field. */
(() => {
  const PC = window.PC;
  const str = value => typeof value === 'string' ? value : '';
  const list = value => Array.isArray(value) ? value.filter(item => item && typeof item === 'object') : null;

  const create = () => ({
    snapshotSeen: false,
    status: 'Не подключено', ready: false, busy: false, selected: true,
    signInPending: false, signOutPending: false, needsSignIn: false, hasCredential: false, progress: '',
    allowed: { structura: [], platform: [] },
    module: {}, fields: {}, files: [], ifcFiles: [], history: [], job: null,
    folders: [], folderStatus: '', legacy: null, cadSessions: [],
    updateAvailable: false, agrInstalling: false,
    theme: 'dark', maximized: false, nativeCaption: false, agrRuntime: null,
    lastSeq: 0, localSeq: 0, notices: [], toasts: [], connectError: null,
    ui: {
      page: 'home', teklaTab: 'standard', conv: 'ifc', jobFilter: 'all', jobQuery: '', jobId: null,
      accountOpen: false, dialog: null, palette: { open: false, query: '', index: 0 },
      dismissed: {}, picks: { 'ifc-profile': 'exact', 'cad-session': '', drives: {} }
    }
  });

  const CONNECTION_COMMANDS = ['connect-common', 'connect', 'reconnect', 'signout', 'disconnect', 'connect-platform'];
  const inferKind = message => /^Действие запущено/.test(message) ? 'progress'
    : /^(Готово|Подключено|Состояние обновлено|Вы вышли)/.test(message) ? 'ok'
    : /отменено|отменён/i.test(message) ? 'info'
    : /не выполнен|не удалось|недоступ|ошибк|нет доступа|не найден|повторите/i.test(message) ? 'error' : 'info';

  /** Records a notice once; returns it so the caller can raise a toast. */
  const pushNotice = (state, notice) => {
    const item = {
      command: str(notice.command) || 'app', message: str(notice.message), seq: notice.seq,
      kind: ['ok', 'error', 'warn', 'info', 'progress'].includes(notice.kind) ? notice.kind : inferKind(str(notice.message))
    };
    if (!item.message) return null;
    state.notices.unshift(item);
    state.notices.length = Math.min(state.notices.length, 30);
    if (CONNECTION_COMMANDS.includes(item.command)) state.connectError = item.kind === 'error' ? item.message : null;
    return item;
  };
  const localNotice = (state, command, message, kind = 'error') => pushNotice(state, { command, message, kind, seq: 'l' + (++state.localSeq) });

  /** Applies a controller snapshot. Unknown or missing keys keep the previous value. */
  const apply = (state, snap) => {
    if (!snap || typeof snap !== 'object') return null;
    state.snapshotSeen = true;
    if (snap.reset === true) { state.files = []; state.ifcFiles = []; state.history = []; state.job = null; }
    if (typeof snap.status === 'string') state.status = snap.status;
    for (const [key, name] of [['commonAccessReady', 'ready'], ['connectPending', 'busy'], ['signInPending', 'signInPending'],
      ['signOutPending', 'signOutPending'], ['commonAccessNeedsSignIn', 'needsSignIn'], ['hasSavedCredential', 'hasCredential'],
      ['updateAvailable', 'updateAvailable'], ['agrInstalling', 'agrInstalling'], ['windowMaximized', 'maximized'],
      ['commonAccessSelected', 'selected']]) if (typeof snap[key] === 'boolean') state[name] = snap[key];
    if (typeof snap.connectionProgress === 'string') state.progress = snap.connectionProgress;
    if (snap.uiTheme === 'dark' || snap.uiTheme === 'light') state.theme = snap.uiTheme;
    if (typeof snap.nativeCaption === 'boolean') state.nativeCaption = snap.nativeCaption;
    if (snap.agrRuntime && typeof snap.agrRuntime === 'object') state.agrRuntime = {
      version: str(snap.agrRuntime.version), sizeBytes: Math.max(0, Number(snap.agrRuntime.archiveSizeBytes) || 0), sourceHost: str(snap.agrRuntime.sourceHost)
    };
    if (snap.allowedPages && typeof snap.allowedPages === 'object')
      state.allowed = Object.fromEntries(['structura', 'platform'].map(p => [p, Array.isArray(snap.allowedPages[p]) ? snap.allowedPages[p].filter(x => typeof x === 'string') : []]));
    else if (snap.allowedPages === null) state.allowed = { structura: [], platform: [] };
    if (snap.moduleFields && typeof snap.moduleFields === 'object') state.module = { ...snap.moduleFields };
    for (const source of [snap.savedFields, snap.fields]) if (source && typeof source === 'object')
      for (const [id, value] of Object.entries(source))
        if (!/token|password|secret|credential/i.test(id) && ['string', 'number', 'boolean'].includes(typeof value)) state.fields[id] = value;
    const files = items => items.map(file => ({ name: str(file.name), size: Math.max(0, Number(file.size) || 0), status: str(file.status) }));
    if (list(snap.files)) state.files = files(list(snap.files));
    if (list(snap.ifcFiles)) state.ifcFiles = files(list(snap.ifcFiles));
    if (list(snap.history)) state.history = list(snap.history).map(job => ({
      id: str(job.id), product: job.mode === 'platform' ? 'platform' : 'structura', kind: job.kind === 'ifc' ? 'ifc' : 'fbx',
      operation: job.operation === 'analyze' ? 'analyze' : 'optimize', status: str(job.status) || '—',
      progress: Math.max(0, Math.min(100, Number(job.progress) || 0)), output: str(job.output),
      hasResult: job.hasResult === true, hasReport: job.hasReport === true, hasValidation: job.hasValidation === true
    }));
    if (snap.job && typeof snap.job === 'object') state.job = { id: str(snap.job.id), kind: snap.job.kind === 'ifc' ? 'ifc' : 'fbx', status: str(snap.job.status) };
    else if (snap.job === null) state.job = null;
    if (list(snap.assignedFolders)) state.folders = list(snap.assignedFolders).map(folder => ({
      resourceId: str(folder.resourceId), displayName: str(folder.displayName) || str(folder.resourceId),
      drive: typeof folder.drive === 'string' && folder.drive ? folder.drive : null, viaLegacyConnector: folder.viaLegacyConnector === true
    }));
    if (typeof snap.folderStatus === 'string') state.folderStatus = snap.folderStatus;
    if (snap.legacyTransfer && typeof snap.legacyTransfer === 'object') state.legacy = {
      status: str(snap.legacyTransfer.status), transferred: snap.legacyTransfer.transferred === true,
      canTransfer: snap.legacyTransfer.canTransfer === true, canRestore: snap.legacyTransfer.canRestore === true
    };
    if (list(snap.cadSessions)) state.cadSessions = list(snap.cadSessions).filter(item => typeof item.sessionKey === 'string').map(item => ({
      key: item.sessionKey, name: str(item.displayName) || 'AutoCAD', version: str(item.version), document: str(item.documentName), selected: item.isSelected === true
    }));
    if (state.busy) state.connectError = null;
    let fresh = null;
    const notice = snap.notice;
    if (notice && typeof notice === 'object' && typeof notice.seq === 'number' && notice.seq > state.lastSeq) {
      state.lastSeq = notice.seq;
      fresh = pushNotice(state, notice);
    } else if (notice && typeof notice === 'object' && typeof notice.seq !== 'number') {
      const key = str(notice.command) + '\u0000' + str(notice.message);
      if (key !== state.lastUnnumbered) { state.lastUnnumbered = key; fresh = pushNotice(state, notice); }
    }
    return fresh;
  };

  /* ---- derived facts ------------------------------------------------------------------------------ */
  const has = (state, product, module) => {
    const pages = state.allowed?.[product] || [];
    return module === 'tekla' ? pages.includes('tekla') || pages.includes('bridge') : pages.includes(module);
  };
  const hasAny = (state, module) => has(state, 'structura', module) || has(state, 'platform', module);
  /** Product whose grant a command is checked against; the controller gates every command per product. */
  const productFor = (state, module, prefer = 'structura') => {
    const other = prefer === 'structura' ? 'platform' : 'structura';
    return has(state, prefer, module) ? prefer : has(state, other, module) ? other : prefer;
  };

  const PROBLEM_STATUSES = ['Доступ отозван', 'Нет доступа', 'Не удалось подключиться', 'Требуется установка защищённого подключения', 'Подключение не настроено'];
  const phaseOf = text => /Ожидаем подтверждения доступа|Проверяем защищённое соединение/.test(text) ? 3
    : /Запускаем фоновую работу|Подключаем общие папки/.test(text) ? 4 : 2;
  const connection = state => {
    const signingOut = state.signOutPending === true;
    const waiting = state.signInPending === true && !signingOut;
    const busy = state.busy === true;
    const ready = state.ready === true && !busy;
    const pending = !busy && !ready && state.status === 'Ожидает выдачи доступа';
    const problem = !busy && !ready && PROBLEM_STATUSES.includes(state.status);
    const errorText = !busy && !ready ? state.connectError || (problem ? state.status + '.' : null) : null;
    const phase = waiting ? 1 : busy ? phaseOf(state.progress) : pending ? 4 : 0;
    const variant = pending ? 'pending' : errorText ? 'error' : 'normal';
    const hasCredential = state.hasCredential === true, needsSignIn = state.needsSignIn === true;
    // The sign-in screen is only for a computer without an account (never signed in, signed out, revoked) and
    // for the first wait for grants. Recovery and connection failures keep the main window: local jobs, journal,
    // diagnostics, updates and the return to the previous connector must stay reachable.
    const showSignIn = !ready && (!hasCredential || needsSignIn || waiting || pending);
    const chip = ready ? ['Подключено', 't-ok', 'ok'] : busy ? [signingOut ? 'Выход…' : waiting ? 'Ожидаем вход' : 'Подключение…', 't-run', 'run']
      : pending ? ['Ожидает доступа', 't-warn', 'warn']
      : showSignIn ? [state.status || 'Не подключено', problem ? 't-bad' : 't-warn', problem ? 'bad' : 'warn']
      : ['Нет связи', problem ? 't-bad' : 't-warn', problem ? 'bad' : 'warn'];
    return { ready, busy, waiting, signingOut, pending, phase, variant, errorText, chip, hasCredential, needsSignIn, showSignIn, offline: !ready && !showSignIn };
  };

  const JOB_TONE = { 'Выполняется': 'run', 'Готово': 'ok', 'Ошибка': 'bad', 'Отменено': 'idle' };
  const jobs = state => state.history.slice().reverse().map(job => {
    const title = job.kind === 'ifc' ? (job.operation === 'analyze' ? 'Анализ IFC' : 'Оптимизация IFC') : 'FBX → GLB';
    const tone = JOB_TONE[job.status] || 'idle';
    return { ...job, title, tone, running: tone === 'run', glyph: job.kind === 'ifc' ? 'I' : 'F',
      statusText: tone === 'run' ? job.progress + ' %' : job.status === '—' ? 'В очереди' : job.status };
  });

  const bridge = state => {
    const text = str(state.module['bridge-status']) || 'Не проверено';
    const down = /недоступн|не отвечает|не удалось|не найден|ошибк|не запущен/i.test(text);
    const ok = !down && /установлен|работает|готов|подключ/i.test(text);
    return { text, state: ok ? 'ok' : down ? 'down' : 'unknown' };
  };

  const agr = state => {
    const text = str(state.module['agr-status']) || 'Не проверено';
    const missing = /ещё не готов|не пройдена|не установлен/i.test(text);
    const ready = !missing && /готов к работе/i.test(text);
    return { text, state: state.agrInstalling ? 'installing' : ready ? 'ready' : missing ? 'missing' : 'unknown' };
  };

  const cad = state => {
    const pick = state.ui.picks['cad-session'];
    const selected = state.cadSessions.find(s => s.key === pick) || state.cadSessions.find(s => s.selected) || state.cadSessions[0] || null;
    return { sessions: state.cadSessions, selected, text: str(state.module['cad-status']) || 'Не проверено' };
  };

  const folders = state => {
    const legacy = state.legacy || { status: '', transferred: false, canTransfer: false, canRestore: false };
    const neighbour = state.folders.some(folder => folder.viaLegacyConnector);
    return { list: state.folders, legacy, neighbour, viaLegacy: state.folders.filter(f => f.viaLegacyConnector) };
  };

  const fieldName = (state, id) => str(String(state.fields[id] ?? ''));
  const runningJob = state => jobs(state).find(job => job.running) || null;

  Object.assign(PC, { model: { create, apply, pushNotice, localNotice, inferKind, has, hasAny, productFor, connection, jobs, bridge, agr, cad, folders, fieldName, runningJob, CONNECTION_COMMANDS } });
})();
