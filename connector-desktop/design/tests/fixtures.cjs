// Snapshot fixtures in the shape GraphiteDesktopController.CreateSnapshot produces. Used by the node tests
// and the browser smoke/capture scripts; values are examples, never read from a live machine.
const clone = value => JSON.parse(JSON.stringify(value));
// Times are relative to the moment the fixture is built, so «сегодня» and durations read naturally.
const ago = minutes => new Date(Date.now() - minutes * 60000).toISOString();
const yesterdayAt = (hours, minutes, plusSeconds = 0) => { const d = new Date(); d.setDate(d.getDate() - 1); d.setHours(hours, minutes, plusSeconds, 0); return d.toISOString(); };
const step = (label, state, meta) => meta ? { label, state, meta } : { label, state };
const job = fields => Object.assign({ subject: null, target: null, finishedAt: null, progress: 0, error: null, steps: [], mode: null,
  operation: null, output: '', hasResult: false, hasReport: false, hasValidation: false, inputBytes: null, outputBytes: null }, fields);

const signedIn = () => ({
  reset: true,
  commonAccessSelected: true, commonAccessReady: true, commonAccessNeedsSignIn: false,
  signInPending: false, connectPending: false, signOutPending: false, connectionProgress: '',
  status: 'Подключено', hasSavedCredential: true,
  allowedPages: { structura: ['tekla', 'folders', 'converters', 'bridge'], platform: ['tekla', 'agr', 'bridge', 'autocad'] },
  moduleFields: {
    'agent-status': 'Подключено', 'agr-status': 'Blender ещё не готов. Установите его или повторите проверку.',
    'bridge-status': 'Связь с Tekla установлена.', 'cad-status': 'Найден 1 сеанс AutoCAD.',
    'sharing-status': 'Доступно для настройки', 'ifc-patch-status': 'Патч не установлен или ещё не проверен',
    'vpn-status': 'Подключено', 'app-version': '1.1.12', 'cloud-status': 'Недоступно',
    'firm-version': '2026.03.0 r1', 'extensions-version': '2026.03.0 r1', 'libraries-version': 'Не синхронизировано'
  },
  savedFields: { 'xs-path': 'C:\\Company\\TeklaFirm', 'ext-path': 'C:\\TeklaStructures\\2025.0\\Environments\\common\\Extensions', 'lib-path': '',
    'sharing-bin': 'C:\\TeklaStructures\\2025.0\\bin', 'ifc-bin': 'C:\\TeklaStructures\\2025.0\\bin', 'patch-folder': '',
    'output-root': 'C:\\Users\\user\\Documents\\Structura\\Конвертер', 'ifc-output': 'C:\\Users\\user\\Documents\\Structura\\Конвертер', autostart: true, 'heartbeat-seconds': 60 },
  fields: {},
  fieldMode: 'structura',
  files: [{ name: 'SM_TestPart_001.zip', size: 20972, status: 'Готово' }],
  ifcFiles: [{ name: 'Капсулы 15.12.25.ifc', size: 4718592, status: 'Готово' }, { name: 'Модель (стройподъем).ifc', size: 1782579, status: 'Выбран' }],
  modelFolders: [],
  // The unified journal, newest first: Platform jobs, local operations and converter jobs (GraphiteSnapshotFacts.Jobs).
  jobs: [
    job({ id: 'j-run', kind: 'tekla', source: 'platform', title: 'Задание Tekla', subject: 'Мосты', target: 'Tekla', startedAt: ago(6), status: 'running', progress: 62,
      steps: [step('Получено из Платформы', 'ok', '21:42'), step('Выполнение', 'ok'), step('Применение в Tekla', 'run', '6 950 из 11 207')] }),
    job({ id: 'j-tekla-done', kind: 'tekla', source: 'platform', title: 'Задание Tekla', subject: 'Мосты', target: 'Tekla', startedAt: ago(12), finishedAt: ago(11), status: 'ok', progress: 100,
      steps: [step('Получено из Платформы', 'ok', '21:36'), step('Выполнение', 'ok'), step('Готово', 'ok')] }),
    job({ id: 'j-ifc', kind: 'ifc', source: 'local', title: 'Оптимизация IFC', subject: 'Капсулы 15.12.25.ifc', startedAt: ago(30), finishedAt: ago(28), status: 'ok', progress: 100,
      steps: [step('Принято к выполнению', 'ok', '21:18'), step('Выполнение', 'ok'), step('Готово', 'ok')], mode: 'structura', operation: 'optimize',
      output: 'Капсулы 15.12.25.optimized.ifc', hasResult: true, hasReport: true, hasValidation: true, inputBytes: 4718592, outputBytes: 3250585 }),
    job({ id: 'j-agr-err', kind: 'agr', source: 'platform', title: 'Публикация АГР', subject: 'Мосты', target: 'АГР', startedAt: ago(50), finishedAt: ago(50), status: 'error',
      error: 'Blender не завершил публикацию вовремя.', steps: [step('Получено из Платформы', 'ok', '20:55'), step('Ошибка', 'err')] }),
    job({ id: 'j-std', kind: 'standard', source: 'local', title: 'Обновление стандарта Tekla', subject: 'Папка фирмы', startedAt: ago(70), finishedAt: ago(69), status: 'ok', progress: 100,
      steps: [step('Сверка версий', 'ok'), step('Проверяем раздел: Папка фирмы', 'ok', 'Копируем обновления в локальную папку: Папка фирмы'), step('Готово', 'ok', 'Папка фирмы: актуально (r1)')] }),
    job({ id: 'j-ifc-err', kind: 'ifc', source: 'local', title: 'Анализ IFC', subject: 'Модель (стройподъем).ifc', startedAt: yesterdayAt(18, 30), finishedAt: yesterdayAt(18, 30, 4), status: 'error',
      error: 'Конвертация не выполнена. Подробности — в отчёте или журнале.', mode: 'structura', operation: 'analyze' }),
    job({ id: 'j-fbx', kind: 'fbx', source: 'local', title: 'FBX → GLB', subject: 'SM_TestPart_001.zip', startedAt: yesterdayAt(18, 4), finishedAt: yesterdayAt(18, 4, 15), status: 'ok', progress: 100,
      mode: 'structura', operation: 'optimize', output: 'SM_TestPart_001.glb.zip', hasResult: true, hasReport: true, inputBytes: 20972, outputBytes: 18011 })
  ],
  assignedFolders: [
    { resourceId: 'company', displayName: 'Файлы компании', drive: 'Z:', viaLegacyConnector: false, legacyShare: false, shareUnc: '\\\\100.108.74.8\\Company_cbd34e43a129', canWrite: true },
    { resourceId: 'bim', displayName: 'BIM_Models', drive: null, viaLegacyConnector: true, legacyShare: true, shareUnc: '\\\\62.113.36.107\\BIM_Models', canWrite: true }
  ],
  folderStatus: 'Готово',
  legacyTransfer: { status: 'Папку BIM_Models на этом компьютере обслуживает прежний Structura Connector.', transferred: false, canTransfer: true, canRestore: false },
  availability: { agentControls: true, publishTekla: false },
  connections: { structura: { status: 'Подключено', hasSavedCredential: true }, platform: { status: 'Подключено', hasSavedCredential: true } },
  cadSessions: [{ sessionKey: 'acad-1', displayName: 'AutoCAD 2022', version: 'R24.1', documentName: '4 Генеральный план.dwg', isSelected: true }],
  updateAvailable: true, agrInstalling: false, notice: null, uiTheme: 'dark', windowMaximized: false, nativeCaption: false,
  agrRuntime: { version: '5.2.0', archiveSizeBytes: 404954661, sourceHost: 'download.blender.org' }, agrInstall: null,
  account: { deviceName: 'LAGOM', networkAddress: '100.108.253.19', gateway: '100.108.74.8', route: 'direct', netBirdVersion: '0.79.0' },
  tekla: { connected: true, version: '2025.0', model: 'Иртыш ПС4.1', bridgeVersion: '1.4.2', bridgeAddress: '127.0.0.1:39421', checkedAt: ago(4) },
  teklaStandard: { source: 'github.com/PROBIM55/tekla-firm-standard', sections: [
    { id: 'xs', path: 'C:\\Company\\TeklaFirm', installed: '2026.03.0 r1', available: null, updateAvailable: false, checkedAt: ago(8), failed: false },
    { id: 'ext', path: 'C:\\TeklaStructures\\2025.0\\Environments\\common\\Extensions', installed: '2026.03.0 r1', available: '2026.04.0 r1', updateAvailable: true, checkedAt: ago(8), failed: false },
    { id: 'lib', path: null, installed: null, available: null, updateAvailable: false, checkedAt: null, failed: false }] },
  update: { available: true, version: '1.1.13', notes: ['Работа рядом с прежним Structura Connector', 'Стандарт Tekla обновляется и в новом режиме', 'Защита заданий AutoCAD и Tekla'], checkedAt: ago(10), checkFailed: false },
  about: { version: '1.1.12', build: '8f3c99b', netbird: '0.79.0', webView2: '141.0.3537.71', ifcEngine: '0.1.2' },
  confirmation: null
});

const variants = {
  signedIn,
  transferred: () => Object.assign(signedIn(), {
    legacyTransfer: { status: 'Компьютер переведён на Платформа Коннектор 25.10.2026 21:50.', transferred: true, canTransfer: false, canRestore: true },
    assignedFolders: [
      { resourceId: 'company', displayName: 'Файлы компании', drive: 'Z:', viaLegacyConnector: false },
      { resourceId: 'bim', displayName: 'BIM_Models', drive: null, viaLegacyConnector: false }
    ]
  }),
  noBridge: () => { const s = signedIn(); s.moduleFields['bridge-status'] = 'Tekla недоступна. Откройте модель в Tekla и повторите проверку.'; s.tekla = { ...s.tekla, connected: false, model: null }; return s; },
  agrInstalling: () => { const s = signedIn(); s.agrInstalling = true; s.moduleFields['agr-status'] = 'Скачивание и установка Blender…'; s.agrInstall = { phase: 'download', completedBytes: 222298112, totalBytes: 404954661 }; return s; },
  // A native action waits for the user's answer in the page dialog.
  confirmTransfer: () => Object.assign(signedIn(), { confirmation: { id: '0123456789abcdef0123456789abcdef', title: 'Перевести компьютер на Платформа Коннектор?',
    text: 'После перевода BIM_Models открывается здесь, а прежний коннектор не мешает.',
    items: ['Прежний Structura Connector закроется и не будет запускаться с Windows.', 'Его токен, настройки и VPN не изменяются — Tekla MultiUser работает как раньше.', 'Вернуть прежний коннектор можно одной кнопкой в «Настройках».'],
    note: 'Закройте файлы, открытые из BIM_Models.', primary: 'Перевести', danger: false, cancel: 'Не сейчас' } }),
  confirmUnmount: () => Object.assign(signedIn(), { confirmation: { id: 'fedcba9876543210fedcba9876543210', title: 'Отключить диск Z:?',
    text: 'Папка «Файлы компании» перестанет быть видна как диск Z:. Файлы на сервере не меняются.',
    items: ['Закройте файлы, открытые с диска Z:.', 'Открыть папку можно снова в любой момент.'], note: null, primary: 'Отключить', danger: true, cancel: 'Отмена' } }),
  confirmSharing: () => Object.assign(signedIn(), { confirmation: { id: 'aaaabbbbccccddddeeeeffff00001111', title: 'Настроить Model Sharing?',
    text: 'Коннектор подключит службу кэша Model Sharing к Tekla Structures на этом компьютере.',
    items: ['Сервер: 62.113.36.107:9990', 'Папка Tekla: C:\\TeklaStructures\\2025.0\\bin', 'Ход и итог появятся в «Заданиях».'],
    note: 'Tekla Structures должна быть закрыта.', primary: 'Настроить', danger: false, cancel: 'Отмена' } }),
  agrReady: () => { const s = signedIn(); s.moduleFields['agr-status'] = 'Blender готов к работе.'; return s; },
  signedOut: () => Object.assign(signedIn(), {
    commonAccessReady: false, commonAccessNeedsSignIn: true, hasSavedCredential: false, status: 'Требуется вход',
    allowedPages: { structura: [], platform: [] }, assignedFolders: [], jobs: [], updateAvailable: false, account: null
  }),
  waitingBrowser: () => Object.assign(variants.signedOut(), { signInPending: true, connectPending: true, connectionProgress: '' }),
  connecting: () => Object.assign(variants.signedOut(), { connectPending: true, connectionProgress: 'Ожидаем подтверждения доступа от сервера…' }),
  // First sign-in that failed before a device credential was stored: the sign-in screen shows the error.
  connectError: () => Object.assign(variants.signedOut(), {
    commonAccessNeedsSignIn: false, hasSavedCredential: false, status: 'Не удалось подключиться',
    notice: { command: 'connect-common', message: 'Не удалось завершить подключение. Повторите попытку.', kind: 'error', seq: 7 }
  }),
  // A signed-in computer whose connection is being restored or has failed keeps the main window.
  recovering: () => Object.assign(signedIn(), {
    commonAccessReady: false, connectPending: true, connectionProgress: 'Восстанавливаем вход и защищённое соединение…',
    status: 'Подключение…', allowedPages: { structura: [], platform: [] }
  }),
  offline: () => Object.assign(signedIn(), {
    commonAccessReady: false, status: 'Не удалось подключиться', allowedPages: { structura: [], platform: [] },
    legacyTransfer: { status: 'Компьютер переведён на Платформа Коннектор 25.10.2026 21:50.', transferred: true, canTransfer: false, canRestore: true },
    notice: { command: 'connect-common', message: 'Не удалось завершить подключение. Повторите попытку.', kind: 'error', seq: 8 }
  }),
  signedOutTransferred: () => Object.assign(variants.signedOut(), {
    legacyTransfer: { status: 'Компьютер переведён на Платформа Коннектор 25.10.2026 21:50.', transferred: true, canTransfer: false, canRestore: true }
  }),
  accessPending: () => Object.assign(variants.signedOut(), { commonAccessNeedsSignIn: false, hasSavedCredential: true, status: 'Ожидает выдачи доступа' })
};

module.exports = { variants: Object.fromEntries(Object.entries(variants).map(([k, f]) => [k, () => clone(f())])) };
