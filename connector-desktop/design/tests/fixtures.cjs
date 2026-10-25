// Snapshot fixtures in the shape GraphiteDesktopController.CreateSnapshot produces. Used by the node tests
// and the browser smoke/capture scripts; values are examples, never read from a live machine.
const clone = value => JSON.parse(JSON.stringify(value));

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
  savedFields: { 'xs-path': 'TeklaFirm', 'ext-path': 'Extensions', 'lib-path': '', 'sharing-bin': 'bin', 'ifc-bin': 'bin',
    'patch-folder': 'Tekla', 'output-root': 'Конвертер', 'ifc-output': 'Конвертер', autostart: true, 'heartbeat-seconds': 60 },
  fields: {},
  fieldMode: 'structura',
  files: [{ name: 'SM_TestPart_001.zip', size: 20972, status: 'Готово' }],
  ifcFiles: [{ name: 'Капсулы 15.12.25.ifc', size: 4718592, status: 'Готово' }, { name: 'Модель (стройподъем).ifc', size: 1782579, status: 'Выбран' }],
  modelFolders: [],
  history: [
    { id: 'j-fbx', mode: 'structura', kind: 'fbx', operation: 'optimize', hasResult: true, hasReport: true, hasValidation: false, status: 'Готово', progress: 100, output: 'SM_TestPart_001.glb.zip' },
    { id: 'j-ifc-err', mode: 'structura', kind: 'ifc', operation: 'analyze', hasResult: false, hasReport: false, hasValidation: false, status: 'Ошибка', progress: 0, output: '' },
    { id: 'j-ifc', mode: 'structura', kind: 'ifc', operation: 'optimize', hasResult: true, hasReport: true, hasValidation: true, status: 'Готово', progress: 100, output: 'Капсулы 15.12.25.optimized.ifc' },
    { id: 'j-run', mode: 'structura', kind: 'ifc', operation: 'optimize', hasResult: false, hasReport: false, hasValidation: false, status: 'Выполняется', progress: 62, output: '' }
  ],
  job: { id: 'j-run', mode: 'structura', kind: 'ifc', operation: 'optimize', hasResult: false, hasReport: false, hasValidation: false, status: 'Выполняется', progress: 62, output: '' },
  assignedFolders: [
    { resourceId: 'company', displayName: 'Файлы компании', drive: 'Z:', viaLegacyConnector: false },
    { resourceId: 'bim', displayName: 'BIM_Models', drive: null, viaLegacyConnector: true }
  ],
  folderStatus: 'Готово',
  legacyTransfer: { status: 'Папку BIM_Models на этом компьютере обслуживает прежний Structura Connector.', transferred: false, canTransfer: true, canRestore: false },
  availability: { agentControls: true, publishTekla: false },
  connections: { structura: { status: 'Подключено', hasSavedCredential: true }, platform: { status: 'Подключено', hasSavedCredential: true } },
  cadSessions: [{ sessionKey: 'acad-1', displayName: 'AutoCAD 2022', version: 'R24.1', documentName: '4 Генеральный план.dwg', isSelected: true }],
  updateAvailable: true, agrInstalling: false, notice: null, uiTheme: 'dark', windowMaximized: false, nativeCaption: false,
  agrRuntime: { version: '5.2.0', archiveSizeBytes: 404954661, sourceHost: 'download.blender.org' }
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
  noBridge: () => { const s = signedIn(); s.moduleFields['bridge-status'] = 'Tekla недоступна. Откройте модель в Tekla и повторите проверку.'; return s; },
  agrInstalling: () => { const s = signedIn(); s.agrInstalling = true; s.moduleFields['agr-status'] = 'Скачивание и установка Blender…'; return s; },
  agrReady: () => { const s = signedIn(); s.moduleFields['agr-status'] = 'Blender готов к работе.'; return s; },
  signedOut: () => Object.assign(signedIn(), {
    commonAccessReady: false, commonAccessNeedsSignIn: true, hasSavedCredential: false, status: 'Требуется вход',
    allowedPages: { structura: [], platform: [] }, assignedFolders: [], history: [], job: null, updateAvailable: false
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
