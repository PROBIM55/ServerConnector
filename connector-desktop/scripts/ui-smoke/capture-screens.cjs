// Screens of the approved mockup (P01–P24, S01–S05) rendered by the real desktop.html with fixture snapshots.
// Usage: PLAYWRIGHT_PACKAGE=<playwright folder> node capture-screens.cjs <output folder>
// Fails on any page/console/network error or when Manrope is not the rendered font.
const fs = require('node:fs');
const path = require('node:path');
const { serve, launch, openPage, fixtures } = require('./harness.cjs');
const F = fixtures.variants;

const click = selector => async page => { await page.locator(selector).first().click(); await page.waitForTimeout(30); };
const nav = id => click(`[data-ui="go"][data-page="${id}"]`);
const steps = (...list) => async page => { for (const step of list) await step(page); };
// A native action asks after the page has been opened: the confirmation arrives with the next snapshot.
const ask = (fixture, ...before) => async (page, emit) => { for (const step of before) await step(page); await emit(Object.assign(fixture(), { reset: false })); await page.waitForTimeout(30); };
const typePalette = text => async page => { await page.keyboard.press('Control+K'); await page.locator('#palette-input').fill(text); await page.waitForTimeout(30); };

const SCREENS = [
  ['P01_Home', F.signedIn, null],
  ['P02_Jobs', F.signedIn, steps(nav('jobs'), click('[data-ui="job"][data-id="j-run"]'))],
  ['P02b_JobsPlatform', F.signedIn, steps(nav('jobs'), click('[data-ui="filter"][data-id="platform"]'))],
  ['P02c_JobsLocal', F.signedIn, steps(nav('jobs'), click('[data-ui="filter"][data-id="local"]'), click('[data-ui="job"][data-id="j-std"]'))],
  ['P03_JobsError', F.signedIn, steps(nav('jobs'), click('[data-ui="filter"][data-id="errors"]'), click('[data-ui="job"][data-id="j-agr-err"]'))],
  ['P04_JobsDone', F.signedIn, steps(nav('jobs'), click('[data-ui="job"][data-id="j-ifc"]'))],
  ['P05_Palette', F.signedIn, typePalette('обнов')],
  ['P06_Account', F.signedIn, click('[data-ui="account"]')],
  ['P07_TeklaStandard', F.signedIn, nav('tekla')],
  ['P08_TeklaSharing', F.signedIn, steps(nav('tekla'), click('[data-ui="tab"][data-id="sharing"]'))],
  ['P09_TeklaSharingConfirm', F.signedIn, ask(F.confirmSharing, nav('tekla'), click('[data-ui="tab"][data-id="sharing"]'))],
  ['P10_TeklaIfc', F.signedIn, steps(nav('tekla'), click('[data-ui="tab"][data-id="ifc"]'))],
  ['P11_TeklaNoBridge', F.noBridge, nav('tekla')],
  ['P12_AutoCAD', F.signedIn, nav('autocad')],
  ['P13_FoldersNeighbor', F.signedIn, nav('folders')],
  ['P14_FoldersTransferred', F.transferred, nav('folders')],
  ['P15_FoldersUnmount', F.transferred, ask(() => Object.assign(F.transferred(), { confirmation: F.confirmUnmount().confirmation }), nav('folders'))],
  ['P16_ConvIfc', F.signedIn, nav('converters')],
  ['P17_ConvFbx', F.signedIn, steps(nav('converters'), click('[data-ui="conv"][data-id="fbx"]'))],
  ['P18_AgrMissing', F.signedIn, nav('agr')],
  ['P19_AgrInstalling', F.agrInstalling, nav('agr')],
  ['P20_AgrReady', F.agrReady, nav('agr')],
  ['P21_Settings', F.signedIn, nav('settings')],
  ['P22_SettingsTransfer', F.signedIn, ask(F.confirmTransfer, nav('settings'))],
  ['P23_SettingsUpdate', F.signedIn, steps(nav('settings'), click('[data-ui="dialog"][data-dialog="update"]'))],
  ['P24_SettingsLight', () => Object.assign(F.transferred(), { uiTheme: 'light' }), nav('settings')],
  ['S01_SignInStart', F.signedOut, null],
  ['S02_SignInWaiting', F.waitingBrowser, null],
  ['S03_SignInProgress', F.connecting, null],
  ['S04_SignInError', F.connectError, null],
  ['S05_SignInPending', F.accessPending, null],
  ['R01_RecoveringHome', F.recovering, null],
  ['R02_OfflineSettings', F.offline, nav('settings')],
  ['R03_SignInLinksRestore', F.signedOutTransferred, null],
  ['L01_HomeLight', () => Object.assign(F.signedIn(), { uiTheme: 'light' }), null],
  ['L02_TeklaLight', () => Object.assign(F.signedIn(), { uiTheme: 'light' }), nav('tekla')],
  ['L03_SignInLight', () => Object.assign(F.signedOut(), { uiTheme: 'light' }), null],
  ['X01_ToastSuccess', F.signedIn, async (page, emit) => { await nav('tekla')(page); await emit(Object.assign(F.signedIn(), { reset: false, notice: { command: 'firm-sync-all', message: 'Готово.', kind: 'ok', seq: 11 } })); }],
  ['X02_ToastError', F.signedIn, async (page, emit) => { await nav('folders')(page); await emit(Object.assign(F.signedIn(), { reset: false, notice: { command: 'models-mount', message: 'Папка BIM_Models на этом компьютере открывается через прежний Structura Connector.', kind: 'warn', seq: 12 } })); await page.evaluate(() => window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE('models-open', 'Выберите папку проекта.')); }]
];

(async () => {
  const output = path.resolve(process.argv[2] || 'ui-shots');
  fs.mkdirSync(output, { recursive: true });
  const only = process.argv[3] ? new RegExp(process.argv[3]) : null;
  const { server, url } = await serve();
  const browser = await launch();
  const report = [];
  try {
    for (const [name, fixture, action] of SCREENS) {
      if (only && !only.test(name)) continue;
      const { page, problems, emit, commands } = await openPage(browser, url);
      await emit(fixture());
      if (action) await action(page, emit);
      await page.waitForTimeout(60);
      const font = await page.evaluate(() => document.fonts.check('600 13px Manrope') && [...document.fonts].some(f => f.family.replace(/"/g, '') === 'Manrope' && f.status === 'loaded'));
      await page.screenshot({ path: path.join(output, name + '.png') });
      const sent = await commands();
      report.push({ name, problems, font, commands: sent.map(c => c.command), overflowX: await page.evaluate(() => document.documentElement.scrollWidth > innerWidth) });
      await page.close();
      console.log(name, problems.length ? 'PROBLEMS ' + problems.join('; ') : 'ok', font ? '' : 'FONT-NOT-LOADED');
    }
  } finally { await browser.close(); server.close(); }
  fs.writeFileSync(path.join(output, 'capture-report.json'), JSON.stringify(report, null, 2));
  const bad = report.filter(r => r.problems.length || !r.font || r.overflowX);
  if (bad.length) { console.error('FAIL:', bad.map(r => r.name).join(', ')); process.exitCode = 1; }
  else console.log(`PASS: ${report.length} screens, 0 page/console/network errors, Manrope loaded.`);
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
