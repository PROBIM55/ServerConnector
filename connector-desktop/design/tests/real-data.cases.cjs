// ИТ-Б: real snapshot facts in the interface — account and device, the unified job journal, Tekla, folders,
// update, components, converters, Blender download and the page confirmations of native actions.
// Registered by connection-ui.test.cjs, the file CI runs.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { loadUi, stateFrom, fixtures: F } = require('./load-ui.cjs');
const PC = loadUi();

test('account card, popover, settings and the connection tile show the device and its network route', () => {
  const state = stateFrom(PC, F.signedIn());
  const side = PC.shell.sidebar(state);
  assert.match(side, /<span class="avatar">LA<\/span>/);
  assert.match(side, /font-size: 13px">LAGOM<\/span><span class="lbl ellipsis">100\.108\.253\.19 · напрямую<\/span>/);
  state.ui.accountOpen = true;
  const popover = PC.shell.sidebar(state);
  assert.match(popover, /<span class="lbl">Устройство<\/span><span>LAGOM<\/span><span class="lbl">Сеть<\/span><span>NetBird · напрямую<\/span><span class="lbl">Адрес<\/span><span>100\.108\.253\.19<\/span>/);
  const settings = PC.pages.settings(state);
  assert.match(settings, /<span class="lbl">Адрес в сети<\/span><span class="mono">100\.108\.253\.19<\/span><span class="lbl">Соединение<\/span><span>NetBird напрямую · шлюз 100\.108\.74\.8<\/span>/);
  // E-mail and company are not known locally and are not invented.
  assert.doesNotMatch(settings + popover, /<span class="lbl">(Вход|Компания)<\/span>/);
  const tile = PC.sections.tiles(state).find(t => t.page === 'settings');
  assert.deepEqual({ ...tile }, { page: 'settings', label: 'Подключение', value: 'Подключено', meta: 'NetBird напрямую · шлюз 100.108.74.8', tone: 'ok' });
  const relay = stateFrom(PC, { ...F.signedIn(), account: { ...F.signedIn().account, route: 'relay' } });
  assert.equal(PC.model.network(relay).line, 'NetBird через relay · шлюз 100.108.74.8');
  const unknown = stateFrom(PC, { ...F.signedIn(), account: null });
  assert.match(PC.shell.sidebar(unknown), /font-size: 13px">Учётная запись<\/span>/);
});

test('jobs: one journal for Platform jobs and work on this computer, with source, steps and times', () => {
  const state = stateFrom(PC, F.signedIn(), { page: 'jobs', jobId: 'j-run' });
  const jobs = PC.model.jobs(state);
  assert.deepEqual(jobs.map(j => j.id), ['j-run', 'j-tekla-done', 'j-ifc', 'j-agr-err', 'j-std', 'j-ifc-err', 'j-fbx']);
  const run = jobs[0];
  assert.equal(run.sourceText, 'Из Платформы → Tekla');
  assert.equal(run.statusText, '62 %');
  assert.match(run.duration, /^6 мин, идёт$/);
  assert.match(run.startedFull, /^сегодня в \d\d:\d\d$/);
  assert.equal(run.sub, 'Мосты · Применение в Tekla · 6 950 из 11 207');
  const html = PC.pages.jobs(state);
  assert.match(html, /<span class="lbl">Из Платформы → Tekla<\/span><h2[^>]*>Применение плана Tekla<\/h2>/);
  assert.match(html, /Выполняется · 62 %/);
  assert.match(html, /<span class="lbl">Выполняется<\/span><span class="ellipsis">Tekla 2025\.0 на LAGOM<\/span>/);
  assert.match(html, /<span style="font-weight: 600">Применение в Tekla<\/span><span class="lbl"[^>]*>6 950 из 11 207<\/span>/);
  assert.match(html, /data-cmd="settings-log" data-section="settings">Журнал<\/button>/);
  assert.doesNotMatch(html, /data-cmd="cancel"[^>]*data-target="j-run"/, 'Platform jobs offer no cancel that does not exist');
  assert.equal(PC.model.jobs(stateFrom(PC, F.signedIn())).find(j => j.id === 'j-std').sourceText, 'Tekla на этом ПК');
  assert.equal(PC.model.jobs(stateFrom(PC, F.signedIn())).find(j => j.id === 'j-fbx').started, 'вчера');
});

test('jobs: errors carry their text, an АГР failure offers the Blender install, filters split by source', () => {
  const state = stateFrom(PC, F.signedIn(), { page: 'jobs', jobId: 'j-agr-err' });
  const html = PC.pages.jobs(state);
  assert.match(html, /<div class="note" style="background: var\(--badSoft\); color: var\(--badInk\)">Blender не завершил публикацию вовремя\.<\/div>/);
  assert.match(html, /data-ui="go" data-page="agr">Установить Blender/);
  state.ui.jobFilter = 'platform';
  assert.equal((PC.pages.jobs(state).match(/data-ui="job" data-id=/g) || []).length, 3);
  state.ui.jobFilter = 'local';
  const local = PC.pages.jobs(state);
  assert.match(local, /data-id="j-std"/);
  assert.doesNotMatch(local, /data-id="j-run"/);
  state.ui.jobId = 'j-std';
  assert.match(PC.pages.jobs(state), /data-cmd="firm-log" data-section="tekla">Журнал синхронизации/);
});

test('home: «Сегодня» lists today\'s jobs and attention shows the standard revision and the update version', () => {
  const state = stateFrom(PC, F.signedIn());
  const html = PC.pages.home(state);
  assert.match(html, /<h2 class="h2" style="flex: 1">Сегодня<\/h2>/);
  assert.match(html, /Tekla и AutoCAD доступны, одно задание выполняется\. Требуют решения: \d+\./);
  assert.doesNotMatch(html.split('Сегодня</h2>')[1], /data-id="j-fbx"/, 'yesterday is not «Сегодня»');
  assert.match(html, /Новая ревизия: Расширения Tekla[\s\S]*2026\.03\.0 r1 → 2026\.04\.0 r1\. Закройте Tekla перед обновлением\.[\s\S]*data-cmd="ext-sync" data-section="tekla">Обновить/);
  assert.match(html, /Доступна версия 1\.1\.13/);
  const tekla = PC.sections.tiles(state).find(t => t.page === 'tekla');
  assert.deepEqual({ ...tekla }, { page: 'tekla', label: 'Tekla 2025.0', value: 'Модель открыта', meta: 'Иртыш ПС4.1', tone: 'ok' });
});

test('Tekla: open model and version from the bridge, standard sections with full paths and the manifest revision', () => {
  const state = stateFrom(PC, F.signedIn());
  const html = PC.pages.tekla(state);
  assert.match(html, /<span class="lbl">Открытая модель<\/span><span[^>]*>Иртыш ПС4\.1<\/span><span class="lbl">Tekla Structures 2025\.0<\/span>/);
  assert.match(html, /127\.0\.0\.1:39421 · проверено в \d\d:\d\d/);
  assert.match(html, /Применение плана Tekla · 62 %[\s\S]*data-ui="job"[^>]*data-id="j-run"[^>]*>Открыть задание/);
  assert.match(html, /Источник: github\.com\/PROBIM55\/tekla-firm-standard · проверено в \d\d:\d\d\. Перед обновлением закройте Tekla/);
  assert.match(html, /C:\\Company\\TeklaFirm/);
  assert.match(html, /C:\\TeklaStructures\\2025\.0\\Environments\\common\\Extensions/);
  assert.match(html, />2026\.03\.0 r1<\/span><span style="color: var\(--dim\)">→<\/span><span class="mono"[^>]*>2026\.04\.0 r1<\/span>/);
  assert.match(html, /Есть обновление[\s\S]*data-cmd="ext-sync"[^>]*>Обновить/);
  assert.match(html, /Актуально[\s\S]*data-cmd="xs-open"[^>]*>Открыть папку/);
  assert.match(html, /Нужна папка[\s\S]*data-target="lib-path"[^>]*>Выбрать папку/);
  assert.match(html, /Стандарт фирмы<span class="tab-badge"/);
  const down = PC.pages.tekla(stateFrom(PC, F.noBridge()));
  assert.match(down, /Откройте модель в Tekla Structures 2025\.0 и нажмите «Подключить»/);
});

test('folders show the UNC address and the rights of the grant', () => {
  const html = PC.pages.folders(stateFrom(PC, F.signedIn()));
  assert.match(html, /<div class="mono ellipsis"[^>]*>\\\\100\.108\.74\.8\\Company_cbd34e43a129<\/div>/);
  assert.match(html, /<span class="lbl">Права<\/span><span>чтение и запись<\/span>/);
  assert.match(html, /Защищённое соединение NetBird: напрямую, шлюз 100\.108\.74\.8\./);
  const readOnly = F.signedIn();
  readOnly.assignedFolders[0].canWrite = false;
  assert.match(PC.pages.folders(stateFrom(PC, readOnly)), /<span class="lbl">Права<\/span><span>только чтение<\/span>/);
});

test('settings: available version with its changes, build and component versions', () => {
  const html = PC.pages.settings(stateFrom(PC, F.signedIn()));
  assert.match(html, />1\.1\.12<\/span><span style="color: var\(--dim\)">→<\/span><span class="mono"[^>]*>1\.1\.13<\/span><span class="lbl">проверено в \d\d:\d\d<\/span>/);
  assert.match(html, /<li>Стандарт Tekla обновляется и в новом режиме<\/li>/);
  assert.match(html, /<span class="lbl">Версия<\/span><span>1\.1\.12 · сборка 8f3c99b<\/span>/);
  assert.match(html, /<span class="lbl">Компоненты<\/span><span>NetBird 0\.79\.0 · WebView2 141\.0\.3537\.71 · движок IFC 0\.1\.2<\/span>/);
  const noNotes = F.signedIn();
  noNotes.update.notes = [];
  assert.doesNotMatch(PC.pages.settings(stateFrom(PC, noNotes)), /<ul class="bullets"/);
  const state = stateFrom(PC, F.signedIn());
  state.ui.dialog = { name: 'update', args: {} };
  assert.match(PC.shell.dialog(state), /<h2>Установить версию 1\.1\.13\?<\/h2>[\s\S]*дождётся окончания задания «Применение плана Tekla»/);
});

test('a native confirmation is one page dialog answered by the explicit command with its id', () => {
  const state = stateFrom(PC, F.confirmTransfer(), { page: 'folders' });
  const html = PC.shell.app(state);
  assert.match(html, /role="dialog" aria-modal="true" aria-label="Перевести компьютер на Платформа Коннектор\?"/);
  assert.match(html, /Закройте файлы, открытые из BIM_Models\./);
  assert.match(html, /data-cmd="confirm-answer" data-target="0123456789abcdef0123456789abcdef" data-field-accept="false">Не сейчас/);
  assert.match(html, /class="b b-pri" data-cmd="confirm-answer" data-target="0123456789abcdef0123456789abcdef" data-field-accept="true">Перевести/);
  assert.equal((html.match(/role="dialog"/g) || []).length, 1, 'one confirmation only');
  const unmount = PC.shell.dialog(stateFrom(PC, F.confirmUnmount()));
  assert.match(unmount, /class="b b-danger" data-cmd="confirm-answer"[^>]*data-field-accept="true">Отключить/);
  PC.model.apply(state, { confirmation: null });
  assert.equal(PC.shell.dialog(state), '');
  // The page never confirms by itself: the folder buttons only request the action.
  const folders = PC.pages.folders(stateFrom(PC, F.transferred()));
  assert.match(folders, /data-cmd="models-unmount" data-field-share-resource="company" data-field-drive="Z:">Отключить диск/);
  assert.doesNotMatch(folders, /confirm-answer/);
});

test('converters: drop zone, engine version and the size before and after', () => {
  const ifc = PC.pages.converters(stateFrom(PC, F.signedIn()));
  assert.match(ifc, /data-cmd="pick-input" data-target="ifc-input" data-drop="ifc-input" class="dropzone"/);
  assert.match(ifc, /Перетащите IFC-файлы сюда/);
  assert.match(ifc, /<span class="lbl">Движок<\/span><span>IFC 0\.1\.2<\/span>/);
  assert.match(ifc, /<span class="lbl">Размер<\/span><span>4,5 МБ → 3,1 МБ<\/span>/);
  assert.doesNotMatch(ifc, /Studio/);
  const fbx = PC.pages.converters(stateFrom(PC, F.signedIn(), { conv: 'fbx' }));
  assert.match(fbx, /data-drop="zip-input"/);
  assert.match(fbx, /<span class="lbl">Время<\/span><span>15 с<\/span>/);
});

test('Blender download progress is shown in megabytes from the installer', () => {
  const html = PC.pages.agr(stateFrom(PC, F.agrInstalling()));
  assert.match(html, /<span>Загрузка архива<\/span><span class="lbl">212 из 386 МБ<\/span><\/span><span class="bar"><span style="width: 55%">/);
  const unknown = PC.pages.agr(stateFrom(PC, { ...F.agrInstalling(), agrInstall: null }));
  assert.match(unknown, /class="bar indeterminate"/);
});

test('snapshot facts are escaped and unknown values are not shown', () => {
  const snapshot = F.signedIn();
  snapshot.account.deviceName = '<b>PC</b>';
  snapshot.tekla.model = '<img src=x>';
  snapshot.jobs[0].error = '<script>';
  const state = stateFrom(PC, snapshot);
  const html = PC.shell.sidebar(state) + PC.pages.tekla(state);
  assert.doesNotMatch(html, /<b>PC<\/b>|<img src=x>/);
  assert.match(html, /&lt;b&gt;PC&lt;\/b&gt;/);
  PC.model.apply(state, { account: { deviceName: 'X', route: 'tunnel', token: 'secret' } });
  assert.equal(state.account.route, null);
  assert.equal('token' in state.account, false);
});
