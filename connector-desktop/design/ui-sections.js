/* Platform Connector UI: the section registry. A new section is one define({...}) entry:
   id, title, hint, icon, module (grant), group in the menu, page renderer, optional tile for «Главная»,
   header actions, palette commands and the native commands whose notices belong to it.
   Entries with enabled:false stay registered but are not shown until their backend works. */
(() => {
  const PC = window.PC, M = PC.model;
  const registry = [];
  const define = entry => { registry.push({ enabled: true, group: 'tools', notices: [], commands: [], ...entry }); return entry; };

  const conn = state => M.connection(state);
  const always = () => true;

  define({
    id: 'home', title: 'Главная', hint: 'Состояние этого компьютера и то, что ждёт вашего решения', icon: 'home', group: 'main',
    visible: always, legacyPage: 'overview', page: state => PC.pages.home(state),
    badge: state => { const n = PC.pages.home.attention(state).length; return n ? { text: String(n), tone: 't-warn' } : null; }
  });
  define({
    id: 'jobs', title: 'Задания', hint: 'Всё, что выполняет коннектор: из Платформы и на этом компьютере', icon: 'jobs', group: 'main',
    visible: always, legacyPage: 'jobs', page: state => PC.pages.jobs(state),
    badge: state => { const n = M.jobs(state).filter(job => job.running).length; return n ? { text: String(n), tone: 't-run' } : null; },
    commands: [{ label: 'Очистить завершённые задания', cmd: 'history-clear', section: 'converters', when: state => M.jobs(state).some(job => !job.running) }]
  });
  define({
    id: 'tekla', title: 'Tekla', hint: 'Связь с моделью, стандарт фирмы, Model Sharing и патч IFC', icon: 'tekla', module: 'tekla',
    visible: state => M.hasAny(state, 'tekla'), legacyPage: 'tekla', page: state => PC.pages.tekla(state),
    product: (state, cmd) => /^bridge-/.test(cmd) ? 'platform' : 'structura',
    notices: ['firm-', 'xs-', 'ext-', 'lib-', 'sharing-', 'ifc-detect', 'ifc-apply', 'ifc-rollback', 'ifc-log', 'bridge-'],
    headerActions: state => M.has(state, 'platform', 'tekla')
      ? [{ label: 'Проверить связь', cls: 'b-sec', cmd: 'bridge-check' }, { label: 'Перезапустить мост', cls: 'b-sec', cmd: 'bridge-restart' }] : [],
    tileOrder: 2,
    tile: state => {
      if (M.has(state, 'platform', 'tekla')) {
        const br = M.bridge(state);
        const label = 'Tekla' + (state.tekla?.version ? ' ' + state.tekla.version : '');
        if (br.state === 'ok') return { label, value: br.model ? 'Модель открыта' : 'Мост работает', meta: br.model || br.text, tone: 'ok' };
        return { label, value: br.state === 'down' ? 'Нет связи' : 'Не проверено', meta: br.state === 'down' ? 'Откройте модель в Tekla' : br.text, tone: br.state === 'down' ? 'warn' : 'idle' };
      }
      const std = M.standard(state);
      const firm = std.sections.find(s => s.id === 'xs');
      const value = firm ? firm.installed || 'Не синхронизировано' : String(state.module['firm-version'] || '').trim() || 'Не синхронизировано';
      return { label: 'Tekla · стандарт фирмы', value, meta: std.updates.length ? 'Есть обновление' : 'Папка фирмы', tone: value === 'Не синхронизировано' || std.updates.length ? 'warn' : 'ok' };
    },
    commands: [
      { label: 'Обновить стандарт Tekla', cmd: 'firm-sync-all', tab: 'standard', when: state => M.has(state, 'structura', 'tekla') },
      { label: 'Настроить Model Sharing', cmd: 'sharing-config', tab: 'sharing', when: state => M.has(state, 'structura', 'tekla') },
      { label: 'Установить патч IFC-экспорта', cmd: 'ifc-apply', tab: 'ifc', when: state => M.has(state, 'structura', 'tekla') },
      { label: 'Проверить связь с Tekla', cmd: 'bridge-check', when: state => M.has(state, 'platform', 'tekla') },
      { label: 'Перезапустить мост к Tekla', cmd: 'bridge-restart', when: state => M.has(state, 'platform', 'tekla') }
    ]
  });
  define({
    id: 'autocad', title: 'AutoCAD', hint: 'Чертежи, в которые Платформа передаёт графику', icon: 'autocad', module: 'autocad', prefer: 'platform',
    visible: state => M.hasAny(state, 'autocad'), legacyPage: 'autocad', page: state => PC.pages.autocad(state),
    notices: ['cad-'], headerActions: () => [{ label: 'Найти сеансы', cls: 'b-sec', cmd: 'cad-refresh' }],
    tileOrder: 3,
    tile: state => {
      const cad = M.cad(state), n = cad.sessions.length;
      return { label: 'AutoCAD', value: n ? n + ' ' + PC.plural(n, 'чертёж', 'чертежа', 'чертежей') : 'Нет сеансов', meta: cad.selected?.document || cad.text, tone: n ? 'ok' : 'idle' };
    },
    commands: [{ label: 'Найти сеансы AutoCAD', cmd: 'cad-refresh' }]
  });
  define({
    id: 'folders', title: 'Папки', hint: 'Файлы компании и проектов, назначенные вам', icon: 'folders', module: 'folders',
    visible: state => M.hasAny(state, 'folders'), legacyPage: 'folders', page: state => PC.pages.folders(state),
    notices: ['models-', 'vpn-check', 'legacy-transfer', 'legacy-started'],
    tileOrder: 4,
    tile: state => {
      const info = M.folders(state), n = info.list.length, here = info.list.filter(f => !f.viaLegacyConnector).length;
      return { label: 'Папки', value: n ? here + ' из ' + n + ' здесь' : 'Не назначены',
        meta: info.neighbour ? info.viaLegacy.map(f => f.displayName).join(', ') + ' — через прежний' : info.list.map(f => f.displayName).join(', ') || 'Назначает администратор',
        tone: info.neighbour ? 'warn' : n ? 'ok' : 'idle' };
    },
    commands: [
      { label: 'Проверить защищённое соединение', cmd: 'vpn-check' },
      { label: 'Перевести компьютер на Платформа Коннектор', cmd: 'legacy-transfer', when: state => state.legacy?.canTransfer === true }
    ]
  });
  define({
    id: 'converters', title: 'Конвертеры', hint: 'Оптимизация IFC и FBX → GLB на этом компьютере', icon: 'converters', module: 'converters',
    visible: state => M.hasAny(state, 'converters'), legacyPage: 'converters', page: state => PC.pages.converters(state),
    notices: ['start', 'cancel', 'results', 'report', 'ifc-optimize', 'ifc-analyze', 'ifc-validation', 'ifc-remove', 'ifc-clear-finished', 'pick-input', 'drop-input', 'files-clear', 'history-clear'],
    commands: [
      { label: 'Добавить IFC-файлы', cmd: 'pick-input', conv: 'ifc', data: { target: 'ifc-input' } },
      { label: 'Добавить ZIP для FBX → GLB', cmd: 'pick-input', conv: 'fbx', data: { target: 'zip-input' } }
    ]
  });
  define({
    id: 'agr', title: 'АГР', hint: 'Публикация АГР через Blender на этом компьютере', icon: 'agr', module: 'agr', prefer: 'platform',
    visible: state => M.hasAny(state, 'agr'), legacyPage: 'agr', page: state => PC.pages.agr(state),
    notices: ['agr-', 'platform-open'],
    tileOrder: 5,
    tile: state => {
      const agr = M.agr(state);
      return { label: 'АГР', value: { ready: 'Blender готов', missing: 'Нужен Blender', installing: 'Установка…', unknown: 'Не проверено' }[agr.state], meta: agr.text, tone: { ready: 'ok', missing: 'warn', installing: 'run', unknown: 'idle' }[agr.state] };
    },
    commands: [{ label: 'Проверить готовность Blender', cmd: 'agr-probe' }, { label: 'Установить Blender', cmd: 'agr-install', when: state => M.agr(state).state !== 'ready' }]
  });
  // Registered, switched off until their backend works (no dead buttons in production).
  define({ id: 'services', title: 'Сервисы', icon: 'services', module: 'services', enabled: false, visible: () => false });
  define({ id: 'attributes', title: 'Атрибуты', icon: 'attributes', module: 'attributes', enabled: false, visible: () => false });
  define({ id: 'exports', title: 'Выгрузки моделей', icon: 'exports', module: 'tekla', enabled: false, visible: () => false });
  define({
    id: 'settings', title: 'Настройки', hint: 'Обновление, оформление, учётная запись и перевод компьютера', icon: 'settings', group: 'bottom',
    visible: always, legacyPage: 'settings', page: state => PC.pages.settings(state),
    badge: state => state.updateAvailable ? { dot: true, title: 'Доступно обновление' } : null,
    notices: ['update-', 'settings-', 'log-clear', 'diagnostics', 'legacy-restore', 'ui-theme', 'reconnect', 'signout', 'connect-common', 'licenses-open'],
    tileOrder: 1,
    tile: state => { const c = conn(state); return { label: 'Подключение', value: c.chip[0], meta: (c.ready && M.network(state).line) || 'Учётная запись и защищённое соединение', tone: c.ready ? 'ok' : 'warn' }; },
    commands: [
      { label: 'Проверить обновления', cmd: 'update-check' },
      { label: 'Установить обновление', dialog: 'update', when: state => state.updateAvailable, title: state => state.update?.version ? 'Установить обновление ' + state.update.version : null },
      { label: 'Открыть журнал', cmd: 'settings-log' },
      { label: 'Сохранить диагностику', cmd: 'diagnostics' },
      { label: 'Светлая тема', ui: 'theme', data: { id: 'light' }, when: state => state.theme !== 'light' },
      { label: 'Тёмная тема', ui: 'theme', data: { id: 'dark' }, when: state => state.theme === 'light' }
    ]
  });

  const byId = id => registry.find(entry => entry.id === id) || null;
  const visible = state => registry.filter(entry => entry.enabled && entry.visible(state));
  const isVisible = (state, id) => visible(state).some(entry => entry.id === id);
  const tiles = state => visible(state).filter(entry => entry.tile).sort((a, b) => (a.tileOrder || 99) - (b.tileOrder || 99))
    .map(entry => ({ page: entry.id, ...entry.tile(state) }));
  const owns = (entry, command) => entry.notices.some(prefix => prefix.endsWith('-') ? command.startsWith(prefix) : command === prefix);
  /** Section whose page shows a notice line for the command; null means «show where the user is». */
  const ownerOf = command => registry.find(entry => entry.enabled && owns(entry, command)) || null;
  /** Product (structura/platform) whose grant the controller checks for this command. */
  const productFor = (state, sectionId, command) => {
    const entry = byId(sectionId);
    if (!entry || !entry.module) return 'structura';
    return entry.product ? entry.product(state, command) : M.productFor(state, entry.module, entry.prefer || 'structura');
  };

  const palette = (state, query = '') => {
    const items = [];
    for (const entry of visible(state)) {
      items.push({ kind: 'Раздел', label: entry.title, where: entry.hint, run: { ui: 'go', page: entry.id } });
      for (const command of entry.commands) if (!command.when || command.when(state))
        items.push({ kind: 'Действие', label: (command.title && command.title(state)) || command.label, where: entry.title, run: { ...command, page: entry.id, section: command.section || entry.id } });
    }
    if (isVisible(state, 'folders')) for (const folder of state.folders)
      items.push({ kind: 'Папка', label: folder.displayName, where: folder.drive ? 'Диск ' + folder.drive : folder.viaLegacyConnector ? 'Через прежний коннектор' : 'Папки', run: { ui: 'go', page: 'folders' } });
    for (const job of M.jobs(state)) items.push({ kind: 'Задание', label: job.title + (job.subject ? ' · ' + job.subject : job.output ? ' · ' + job.output : '') + (job.started ? ' · ' + job.started : ''), where: job.statusText, run: { ui: 'job', id: job.id } });
    const words = query.toLowerCase().split(/\s+/).filter(Boolean);
    const hits = items.filter(item => words.every(word => (item.label + ' ' + item.where + ' ' + item.kind).toLowerCase().includes(word)));
    // Matches in the name come before matches found only in the description.
    const inLabel = item => words.every(word => item.label.toLowerCase().includes(word));
    return hits.filter(inLabel).concat(hits.filter(item => !inLabel(item)));
  };

  PC.sections = { define, registry, byId, visible, isVisible, tiles, ownerOf, productFor, palette };
})();
