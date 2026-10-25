// States for the native window acceptance (UiSmoke window ...): the same fixtures and steps as capture-screens.cjs,
// written as JSON so the real MainWindow/WebView2 renders exactly what the browser captures show.
// Usage: node export-window-states.cjs <output.json>
const fs = require('node:fs');
const path = require('node:path');
const F = require(path.resolve(__dirname, '../../design/tests/fixtures.cjs')).variants;

const nav = id => ({ op: 'click', selector: `[data-ui="go"][data-page="${id}"]` });
const click = selector => ({ op: 'click', selector });
const emit = snapshot => ({ op: 'emit', snapshot: Object.assign(snapshot, { reset: false }) });
const palette = text => ({ op: 'palette', text });

const STATES = [
  ['P01_Home', F.signedIn(), []],
  ['P02_Jobs', F.signedIn(), [nav('jobs'), click('[data-ui="job"][data-id="j-run"]')]],
  ['P03_JobsError', F.signedIn(), [nav('jobs'), click('[data-ui="filter"][data-id="errors"]'), click('[data-ui="job"][data-id="j-agr-err"]')]],
  ['P05_Palette', F.signedIn(), [palette('обнов')]],
  ['P07_TeklaStandard', F.signedIn(), [nav('tekla')]],
  ['P11_TeklaNoBridge', F.noBridge(), [nav('tekla')]],
  ['P12_AutoCAD', F.signedIn(), [nav('autocad')]],
  ['P13_FoldersNeighbor', F.signedIn(), [nav('folders')]],
  ['P14_FoldersTransferred', F.transferred(), [nav('folders')]],
  ['P16_ConvIfc', F.signedIn(), [nav('converters')]],
  ['P17_ConvFbx', F.signedIn(), [nav('converters'), click('[data-ui="conv"][data-id="fbx"]')]],
  ['P18_AgrMissing', F.signedIn(), [nav('agr')]],
  ['P19_AgrInstalling', F.agrInstalling(), [nav('agr')]],
  ['P20_AgrReady', F.agrReady(), [nav('agr')]],
  ['P21_Settings', F.signedIn(), [nav('settings')]],
  ['P22_SettingsTransfer', F.signedIn(), [nav('settings'), emit(F.confirmTransfer())]],
  ['S01_SignInStart', F.signedOut(), []],
  ['S04_SignInError', F.connectError(), []],
  // Light theme last: the harness switches the real window theme through the ui-theme RPC once.
  ['P24_SettingsLight', Object.assign(F.transferred(), { uiTheme: 'light' }), [nav('settings')]]
].map(([name, snapshot, steps]) => ({ name, theme: snapshot.uiTheme === 'light' ? 'light' : 'dark', snapshot, steps }));

const output = path.resolve(process.argv[2] || 'window-states.json');
fs.writeFileSync(output, JSON.stringify(STATES, null, 1));
console.log(`${STATES.length} states -> ${output}`);
