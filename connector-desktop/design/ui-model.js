/* Platform Connector UI: state and the reading of native snapshots.
   Only what the controller sends is shown; nothing here invents data for a missing field. */
(() => {
  const PC = window.PC;
  const str = value => typeof value === 'string' ? value : '';
  const opt = value => typeof value === 'string' && value ? value : null;
  const num = value => typeof value === 'number' && Number.isFinite(value) ? value : null;
  const list = value => Array.isArray(value) ? value.filter(item => item && typeof item === 'object') : null;
  const strings = value => Array.isArray(value) ? value.filter(item => typeof item === 'string' && item) : [];

  const create = () => ({
    snapshotSeen: false,
    status: 'Не подключено', ready: false, busy: false, selected: true,
    signInPending: false, signOutPending: false, needsSignIn: false, hasCredential: false, progress: '',
    allowed: { structura: [], platform: [] },
    module: {}, fields: {}, files: [], ifcFiles: [], jobs: [],
    folders: [], folderStatus: '', legacy: null, cadSessions: [],
    updateAvailable: false, agrInstalling: false, agrInstall: null,
    account: null, tekla: null, teklaStd: null, update: null, about: null, confirmation: null,
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

  const JOB_KINDS = ['tekla', 'autocad', 'agr', 'ifc', 'fbx', 'standard', 'sharing', 'patch', 'other'];
  const JOB_STATUSES = ['queued', 'running', 'ok', 'error', 'cancelled'];
  const readJob = job => ({
    id: str(job.id), kind: JOB_KINDS.includes(job.kind) ? job.kind : 'other', source: job.source === 'platform' ? 'platform' : 'local',
    title: str(job.title) || 'Задание', subject: str(job.subject), target: str(job.target),
    startedAt: opt(job.startedAt), finishedAt: opt(job.finishedAt),
    status: JOB_STATUSES.includes(job.status) ? job.status : 'queued',
    progress: Math.max(0, Math.min(100, Number(job.progress) || 0)), error: str(job.error),
    steps: (list(job.steps) || []).map(step => ({ label: str(step.label), state: ['ok', 'run', 'err', 'wait'].includes(step.state) ? step.state : 'wait', meta: str(step.meta) })).filter(step => step.label),
    operation: job.operation === 'analyze' ? 'analyze' : 'optimize', output: str(job.output),
    hasResult: job.hasResult === true, hasReport: job.hasReport === true, hasValidation: job.hasValidation === true,
    inputBytes: num(job.inputBytes), outputBytes: num(job.outputBytes)
  });

  /** Applies a controller snapshot. Unknown or missing keys keep the previous value. */
  const apply = (state, snap) => {
    if (!snap || typeof snap !== 'object') return null;
    state.snapshotSeen = true;
    if (snap.reset === true) { state.files = []; state.ifcFiles = []; state.jobs = []; }
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
    if (snap.agrInstall && typeof snap.agrInstall === 'object') state.agrInstall = {
      phase: str(snap.agrInstall.phase), done: Math.max(0, Number(snap.agrInstall.completedBytes) || 0), total: num(snap.agrInstall.totalBytes)
    };
    else if (snap.agrInstall === null) state.agrInstall = null;
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
    if (list(snap.jobs)) state.jobs = list(snap.jobs).map(readJob).filter(job => job.id);
    if (list(snap.assignedFolders)) state.folders = list(snap.assignedFolders).map(folder => ({
      resourceId: str(folder.resourceId), displayName: str(folder.displayName) || str(folder.resourceId),
      drive: typeof folder.drive === 'string' && folder.drive ? folder.drive : null, viaLegacyConnector: folder.viaLegacyConnector === true,
      unc: str(folder.shareUnc), canWrite: folder.canWrite === true
    }));
    if (typeof snap.folderStatus === 'string') state.folderStatus = snap.folderStatus;
    if (snap.legacyTransfer && typeof snap.legacyTransfer === 'object') state.legacy = {
      status: str(snap.legacyTransfer.status), transferred: snap.legacyTransfer.transferred === true,
      canTransfer: snap.legacyTransfer.canTransfer === true, canRestore: snap.legacyTransfer.canRestore === true
    };
    if (list(snap.cadSessions)) state.cadSessions = list(snap.cadSessions).filter(item => typeof item.sessionKey === 'string').map(item => ({
      key: item.sessionKey, name: str(item.displayName) || 'AutoCAD', version: str(item.version), document: str(item.documentName), selected: item.isSelected === true
    }));
    if (snap.account && typeof snap.account === 'object') state.account = {
      device: str(snap.account.deviceName), address: str(snap.account.networkAddress), gateway: str(snap.account.gateway),
      route: snap.account.route === 'direct' || snap.account.route === 'relay' ? snap.account.route : null, netbird: str(snap.account.netBirdVersion)
    };
    else if (snap.account === null) state.account = null;
    if (snap.tekla && typeof snap.tekla === 'object') state.tekla = {
      connected: snap.tekla.connected === true, version: str(snap.tekla.version), model: str(snap.tekla.model),
      bridgeVersion: str(snap.tekla.bridgeVersion), address: str(snap.tekla.bridgeAddress), checkedAt: opt(snap.tekla.checkedAt)
    };
    if (snap.teklaStandard && typeof snap.teklaStandard === 'object') state.teklaStd = {
      source: str(snap.teklaStandard.source),
      sections: (list(snap.teklaStandard.sections) || []).map(section => ({
        id: str(section.id), path: str(section.path), installed: str(section.installed), available: str(section.available),
        updateAvailable: section.updateAvailable === true, checkedAt: opt(section.checkedAt), failed: section.failed === true
      }))
    };
    if (snap.update && typeof snap.update === 'object') state.update = {
      version: str(snap.update.version), notes: strings(snap.update.notes), checkedAt: opt(snap.update.checkedAt), checkFailed: snap.update.checkFailed === true
    };
    if (snap.about && typeof snap.about === 'object') state.about = {
      version: str(snap.about.version), build: str(snap.about.build), netbird: str(snap.about.netbird), webView2: str(snap.about.webView2), ifcEngine: str(snap.about.ifcEngine)
    };
    if (snap.confirmation && typeof snap.confirmation === 'object' && typeof snap.confirmation.id === 'string') state.confirmation = {
      id: snap.confirmation.id, title: str(snap.confirmation.title), text: str(snap.confirmation.text), items: strings(snap.confirmation.items),
      note: str(snap.confirmation.note), primary: str(snap.confirmation.primary) || 'Подтвердить', danger: snap.confirmation.danger === true,
      cancel: str(snap.confirmation.cancel) || 'Отмена'
    };
    else if (snap.confirmation === null) state.confirmation = null;
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

  /* ---- time ------------------------------------------------------------------------------------------ */
  const pad = n => String(n).padStart(2, '0');
  const dateOf = iso => { if (!iso) return null; const d = new Date(iso); return Number.isNaN(d.getTime()) ? null : d; };
  const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  const hm = d => pad(d.getHours()) + ':' + pad(d.getMinutes());
  /** «21:42» today, «вчера», «25.10» earlier. */
  const shortTime = (iso, now = new Date()) => {
    const d = dateOf(iso); if (!d) return '';
    if (sameDay(d, now)) return hm(d);
    const y = new Date(now); y.setDate(now.getDate() - 1);
    return sameDay(d, y) ? 'вчера' : pad(d.getDate()) + '.' + pad(d.getMonth() + 1);
  };
  /** «сегодня в 21:42», «вчера в 18:04», «25.10 в 21:42». */
  const fullTime = (iso, now = new Date()) => {
    const d = dateOf(iso); if (!d) return '';
    const y = new Date(now); y.setDate(now.getDate() - 1);
    return (sameDay(d, now) ? 'сегодня' : sameDay(d, y) ? 'вчера' : pad(d.getDate()) + '.' + pad(d.getMonth() + 1)) + ' в ' + hm(d);
  };
  /** «проверено в 21:40» today or «проверено 25.10 в 21:40». */
  const checkedText = (iso, now = new Date()) => {
    const d = dateOf(iso); if (!d) return '';
    return sameDay(d, now) ? 'проверено в ' + hm(d) : 'проверено ' + pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + ' в ' + hm(d);
  };
  const span = ms => {
    const s = Math.max(0, Math.round(ms / 1000));
    if (s < 60) return s + ' с';
    const m = Math.round(s / 60);
    if (m < 60) return m + ' мин';
    return Math.floor(m / 60) + ' ч ' + (m % 60) + ' мин';
  };
  const isToday = (iso, now = new Date()) => { const d = dateOf(iso); return !!d && sameDay(d, now); };

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

  /** «NetBird напрямую · шлюз 100.108.74.8» from the account facts; empty when nothing is known. */
  const network = state => {
    const a = state.account;
    if (!a) return { route: '', line: '' };
    const route = a.route === 'direct' ? 'напрямую' : a.route === 'relay' ? 'через relay' : '';
    const line = ['NetBird' + (route ? ' ' + route : ''), a.gateway ? 'шлюз ' + a.gateway : ''].filter(Boolean).join(' · ');
    return { route, line: a.address || a.gateway || route ? line : '' };
  };

  const STATUS_VIEW = { running: ['run', 'Выполняется'], queued: ['idle', 'В очереди'], ok: ['ok', 'Готово'], error: ['bad', 'Ошибка'], cancelled: ['idle', 'Отменено'] };
  const GLYPH = { tekla: 'T', autocad: 'A', agr: 'А', ifc: 'I', fbx: 'F', standard: 'S', sharing: 'M', patch: 'P', other: 'П' };
  const sourceLabel = job => job.source === 'platform' ? 'Из Платформы' + (job.target ? ' → ' + job.target : '')
    : job.kind === 'ifc' || job.kind === 'fbx' ? 'Конвертер на этом ПК'
    : job.kind === 'standard' || job.kind === 'sharing' || job.kind === 'patch' ? 'Tekla на этом ПК' : 'На этом ПК';

  /** The unified journal, newest first, with everything a row or the detail panel shows. */
  const jobs = (state, now = new Date()) => state.jobs.map(job => {
    const [tone, label] = STATUS_VIEW[job.status] || STATUS_VIEW.queued;
    const running = job.status === 'running';
    const started = dateOf(job.startedAt), finished = dateOf(job.finishedAt);
    const duration = !started ? '' : running ? span(now - started) + ', идёт' : finished ? span(finished - started) : '';
    const runStep = job.steps.slice().reverse().find(step => step.state === 'run');
    const detail = running && runStep ? runStep.label + (runStep.meta ? ' · ' + runStep.meta : '') : job.subject ? '' : job.output || '';
    return {
      ...job, tone, running, glyph: GLYPH[job.kind] || 'П',
      statusText: running ? job.progress + ' %' : label,
      sourceText: sourceLabel(job),
      started: shortTime(job.startedAt, now), startedFull: fullTime(job.startedAt, now), duration,
      sub: [job.subject || (job.source === 'local' ? 'На этом ПК' : ''), detail].filter(Boolean).join(' · '),
      today: running || isToday(job.startedAt, now)
    };
  });

  const bridge = state => {
    const t = state.tekla;
    const text = str(state.module['bridge-status']) || 'Не проверено';
    if (t && t.checkedAt) {
      const down = !t.connected;
      return { text, state: down ? 'down' : 'ok', model: t.model, version: t.version, address: t.address, checkedAt: t.checkedAt };
    }
    const down = /недоступн|не отвечает|не удалось|не найден|ошибк|не запущен/i.test(text);
    const ok = !down && /установлен|работает|готов|подключ/i.test(text);
    return { text, state: ok ? 'ok' : down ? 'down' : 'unknown', model: t?.model || '', version: t?.version || '', address: t?.address || '', checkedAt: null };
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

  const STANDARD_NAMES = { xs: 'Папка фирмы', ext: 'Расширения', lib: 'Библиотеки Grasshopper' };
  /** Sections of the Tekla standard: installed and available revisions, full local path and the last check. */
  const standard = state => {
    const sections = (state.teklaStd?.sections || []).filter(s => STANDARD_NAMES[s.id]).map(s => ({ ...s, name: STANDARD_NAMES[s.id] }));
    const checked = sections.map(s => s.checkedAt).filter(Boolean).sort().pop() || null;
    return { source: state.teklaStd?.source || '', sections, checkedAt: checked, updates: sections.filter(s => s.updateAvailable) };
  };

  const fieldName = (state, id) => str(String(state.fields[id] ?? ''));
  const runningJob = state => jobs(state).find(job => job.running) || null;

  Object.assign(PC, { model: { create, apply, pushNotice, localNotice, inferKind, has, hasAny, productFor, connection, network, jobs, bridge, agr, cad, folders, standard, fieldName, runningJob, CONNECTION_COMMANDS, time: { shortTime, fullTime, checkedText, span, isToday } } });
})();
