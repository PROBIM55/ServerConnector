// Main window: section registry and grants, commands per product, folders, notices, theme, palette.
// Registered by connection-ui.test.cjs, the file CI runs (node --test connector-desktop/design/tests/connection-ui.test.cjs).
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { loadUi, stateFrom, fixtures: F } = require('./load-ui.cjs');
const PC = loadUi();
const navIds = html => [...html.matchAll(/data-ui="go" data-page="([a-z]+)" class="nav/g)].map(m => m[1]);
const withGrants = (structura, platform) => ({ ...F.signedIn(), allowedPages: { structura, platform } });

test('menu shows the union of both products and no product switch', () => {
  const html = PC.shell.app(stateFrom(PC, withGrants(['folders'], ['agr'])));
  assert.deepEqual(navIds(html), ['home', 'jobs', 'folders', 'agr', 'settings']);
  assert.doesNotMatch(html, /data-mode=|Structura<\/span><\/button>|ПЛАТФОРМА/);
  assert.deepEqual(navIds(PC.shell.app(stateFrom(PC, F.signedIn()))), ['home', 'jobs', 'tekla', 'autocad', 'folders', 'converters', 'agr', 'settings']);
  assert.deepEqual(navIds(PC.shell.app(stateFrom(PC, withGrants([], [])))), ['home', 'jobs', 'settings']);
});

test('Tekla: model link needs the Platform grant, standard tabs need the Structura grant', () => {
  const platformOnly = PC.pages.tekla(stateFrom(PC, withGrants([], ['bridge'])));
  assert.match(platformOnly, /Мост к Tekla/);
  assert.doesNotMatch(platformOnly, /role="tablist"/);
  const structuraOnly = PC.pages.tekla(stateFrom(PC, withGrants(['tekla'], [])));
  assert.doesNotMatch(structuraOnly, /Мост к Tekla|Tekla не отвечает/);
  assert.match(structuraOnly, /Стандарт фирмы.*Model Sharing.*Патч IFC-экспорта/s);
  assert.match(structuraOnly, /data-cmd="firm-sync-all"/);
  assert.match(PC.pages.tekla(stateFrom(PC, F.noBridge())), /Tekla не отвечает[\s\S]*data-cmd="bridge-start"/);
});

test('each command is checked against the product that grants its module', () => {
  const both = stateFrom(PC, F.signedIn());
  assert.equal(PC.sections.productFor(both, 'tekla', 'bridge-check'), 'platform');
  assert.equal(PC.sections.productFor(both, 'tekla', 'firm-sync-all'), 'structura');
  assert.equal(PC.sections.productFor(both, 'agr', 'agr-probe'), 'platform');
  assert.equal(PC.sections.productFor(both, 'folders', 'models-mount'), 'structura');
  const platformFolders = stateFrom(PC, withGrants([], ['folders', 'converters']));
  assert.equal(PC.sections.productFor(platformFolders, 'folders', 'models-mount'), 'platform');
  assert.equal(PC.sections.productFor(platformFolders, 'converters', 'start'), 'platform');
});

test('folders next to the previous connector: banner, transfer dialog, no mount for its folder', () => {
  const state = stateFrom(PC, F.signedIn());
  const html = PC.pages.folders(state);
  assert.match(html, /На компьютере работает прежний Structura Connector/);
  // The transfer is confirmed once, natively; the page sends the request directly.
  assert.match(html, /data-cmd="legacy-transfer">Перевести компьютер/);
  assert.doesNotMatch(html, /data-dialog="transfer"/);
  assert.match(html, /Через прежний/);
  assert.match(html, /data-cmd="models-open" data-field-share-resource="company">Открыть/);
  assert.doesNotMatch(html, /data-field-share-resource="bim"/);
});

test('folders after the transfer: rollback button and a drive letter for BIM_Models', () => {
  const state = stateFrom(PC, F.transferred());
  state.ui.picks.drives.bim = 'Y:';
  const html = PC.pages.folders(state);
  assert.match(html, /Компьютер переведён на Платформа Коннектор 25\.10\.2026 21:50\./);
  assert.match(html, /data-cmd="legacy-restore">Вернуть прежний коннектор/);
  assert.match(html, /<option selected>Y:<\/option>/);
  assert.match(html, /data-cmd="models-mount" data-field-share-resource="bim" data-field-drive="Y:">Подключить диск/);
  assert.match(html, /data-cmd="models-unmount" data-field-share-resource="company" data-field-drive="Z:">Отключить диск/);
  assert.equal(PC.shell.DIALOGS.unmount, undefined, 'drive disconnection is confirmed natively only');
});

test('notices: each one is reported once, success and error keep their kind', () => {
  const state = stateFrom(PC, F.signedIn());
  const ok = PC.model.apply(state, { notice: { command: 'firm-sync-all', message: 'Готово.', kind: 'ok', seq: 3 } });
  assert.equal(ok.kind, 'ok');
  assert.equal(PC.model.apply(state, { notice: { command: 'firm-sync-all', message: 'Готово.', kind: 'ok', seq: 3 } }), null);
  const error = PC.model.apply(state, { notice: { command: 'models-mount', message: 'Выберите папку проекта.', kind: 'error', seq: 4 } });
  assert.equal(error.kind, 'error');
  assert.equal(error.message, 'Выберите папку проекта.');
  assert.equal(PC.model.localNotice(state, 'xs-open', 'Папка не найдена на этом компьютере.').kind, 'error');
  assert.equal(PC.model.inferKind('Действие не выполнено. Подробности доступны в журнале.'), 'error');
  assert.equal(PC.model.inferKind('Действие запущено.'), 'progress');
  assert.equal(PC.sections.ownerOf('bridge-check').id, 'tekla');
  assert.equal(PC.sections.ownerOf('models-unmount').id, 'folders');
  assert.equal(PC.sections.ownerOf('update-apply').id, 'settings');
  const toasts = PC.shell.toasts({ toasts: [{ id: '4', kind: 'error', message: 'Выберите папку проекта.' }, { id: '3', kind: 'ok', message: 'Готово.' }] });
  assert.match(toasts, /role="alert" class="card toast error"><span class="chip t-bad">!<\/span><span>Выберите папку проекта\.<\/span>/);
  assert.match(toasts, /role="status" class="card toast ok"><span class="chip t-ok">✓<\/span>/);
});

test('page note shows the latest message of the section, progress included', () => {
  const state = stateFrom(PC, F.signedIn(), { page: 'tekla' });
  state.notices.unshift({ command: 'firm-sync-all', message: 'Действие запущено.', kind: 'progress', seq: 9, page: 'tekla', at: Date.now() });
  assert.match(PC.shell.main(state), /class="page-note" role="status" style="background: var\(--accentSoft\)[^"]*"><span>Действие запущено\./);
  state.ui.dismissed[9] = true;
  assert.doesNotMatch(PC.shell.main(state), /page-note/);
});

test('theme comes from the native settings and is chosen on the settings page', () => {
  const state = stateFrom(PC, { ...F.signedIn(), uiTheme: 'light' });
  assert.equal(state.theme, 'light');
  assert.match(PC.pages.settings(state), /data-ui="theme" data-id="light" aria-selected="true">Светлая/);
  PC.model.apply(state, { uiTheme: 'sepia' });
  assert.equal(state.theme, 'light', 'unknown values are ignored');
});

test('palette is built from the registry and the snapshot, not from a fixed list', () => {
  const state = stateFrom(PC, F.signedIn());
  const labels = PC.sections.palette(state, 'обнов').map(item => item.label);
  assert.equal(labels[0], 'Обновить стандарт Tekla');
  assert.ok(labels.includes('Проверить обновления'));
  assert.ok(labels.includes('Установить обновление 1.1.13'));
  assert.ok(PC.sections.palette(state, 'BIM').some(item => item.kind === 'Папка'));
  assert.ok(PC.sections.palette(state, 'Анализ').some(item => item.kind === 'Задание'));
  const noTekla = stateFrom(PC, withGrants(['folders'], []));
  assert.ok(!PC.sections.palette(noTekla, 'обнов').some(item => item.label === 'Обновить стандарт Tekla'));
});

test('a new section is one registry entry; switched-off sections never appear', () => {
  const state = stateFrom(PC, withGrants(['services', 'attributes'], []));
  assert.deepEqual(navIds(PC.shell.app(state)), ['home', 'jobs', 'settings']);
  PC.sections.define({ id: 'reports', title: 'Отчёты', hint: 'Проверка', icon: 'jobs', module: 'reports',
    visible: s => PC.model.hasAny(s, 'reports'), page: () => '<p>reports</p>', commands: [{ label: 'Собрать отчёт', cmd: 'diagnostics' }] });
  const granted = stateFrom(PC, withGrants(['reports'], []));
  assert.ok(navIds(PC.shell.app(granted)).includes('reports'));
  assert.ok(PC.sections.palette(granted, 'отчёт').some(item => item.label === 'Собрать отчёт'));
  PC.sections.registry.pop();
});

test('jobs: filters, detail actions and the clear button use converter commands', () => {
  const state = stateFrom(PC, F.signedIn(), { page: 'jobs', jobId: 'j-ifc' });
  const html = PC.pages.jobs(state);
  assert.match(html, />Все<span style="opacity: .65; margin-left: 6px">7<\/span>/);
  assert.match(html, />Ошибки<span style="opacity: .65; margin-left: 6px">2<\/span>/);
  assert.match(html, />Из Платформы<span style="opacity: .65; margin-left: 6px">3<\/span>/);
  assert.match(html, />На этом ПК<span style="opacity: .65; margin-left: 6px">4<\/span>/);
  assert.match(html, /<span class="lbl">Размер<\/span><span class="ellipsis">4,5 МБ → 3,1 МБ<\/span>/);
  assert.match(html, /data-cmd="results" data-section="converters" data-target="j-ifc">Открыть результат/);
  assert.match(html, /data-cmd="ifc-validation" data-section="converters" data-target="j-ifc">Проверить результат/);
  assert.match(html, /data-cmd="history-clear" data-section="converters">Очистить завершённые/);
});

test('names from the machine are escaped', () => {
  const snapshot = F.signedIn();
  snapshot.assignedFolders[0].displayName = '<img src=x onerror=alert(1)>';
  const html = PC.pages.folders(stateFrom(PC, snapshot));
  assert.doesNotMatch(html, /<img src=x/);
  assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/);
});

test('Blender facts come from the runtime manifest in the snapshot and are hidden without it', () => {
  const html = PC.pages.agr(stateFrom(PC, F.signedIn()));
  assert.match(html, /Нужен Blender 5\.2\.0 LTS/);
  assert.match(html, /download\.blender\.org, официальный архив/);
  assert.match(html, /386,2 МБ · подлинность проверяется/);
  const bare = PC.pages.agr(stateFrom(PC, { ...F.signedIn(), agrRuntime: null }));
  assert.match(bare, /Нужен Blender</);
  assert.doesNotMatch(bare, /download\.blender\.org|МБ/);
});

test('a job chosen inside «Задания» keeps the filter and search; from another page it opens in the full list', () => {
  const state = stateFrom(PC, F.signedIn(), { page: 'jobs', jobFilter: 'errors', jobQuery: 'Мосты' });
  assert.equal(PC.model.openJob(state, 'j-agr-err'), false);
  assert.deepEqual([state.ui.page, state.ui.jobId, state.ui.jobFilter, state.ui.jobQuery], ['jobs', 'j-agr-err', 'errors', 'Мосты']);
  assert.match(PC.pages.jobs(state), /data-ui="filter" data-id="errors" aria-selected="true"/);
  state.ui.page = 'home';
  assert.equal(PC.model.openJob(state, 'j-run'), true);
  assert.deepEqual([state.ui.page, state.ui.jobId, state.ui.jobFilter, state.ui.jobQuery], ['jobs', 'j-run', 'all', '']);
});
