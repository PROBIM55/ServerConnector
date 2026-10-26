// Sign-in screen (SignIn.dc.html S01–S05) and the switch to the main window.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { loadUi, stateFrom, fixtures: F } = require('./load-ui.cjs');
const PC = loadUi();
const app = state => PC.shell.app(state);

test('signed-out computer sees only the sign-in screen with one way in', () => {
  const html = app(stateFrom(PC, F.signedOut()));
  assert.match(html, /<button type="button" class="si-btn" data-cmd="connect">Войти через Платформу<\/button>/);
  assert.match(html, /страницей входа Платформы \(bimplatforma\.ru\)/);
  assert.doesNotMatch(html, /Учётная запись Structura|Войти через Structura/);
  assert.doesNotMatch(html, /class="side"|data-cmd="signout"|data-cmd="reconnect"|data-ui="palette"/);
  assert.match(html, /Войдите, чтобы подключить компьютер/);
});

test('waiting for the browser shows step 1 and lets the user cancel the request', () => {
  const view = PC.pages.signinModel(stateFrom(PC, F.waitingBrowser()));
  assert.equal(view.phase, 1);
  assert.deepEqual({ ...view.primary }, { label: 'Ожидаем вход в браузере…', waiting: true });
  assert.deepEqual({ ...view.secondary }, { label: 'Отменить', cmd: 'signout' });
  const html = app(stateFrom(PC, F.waitingBrowser()));
  assert.match(html, /class="si-btn waiting" disabled aria-busy="true">Ожидаем вход в браузере…/);
  assert.match(html, /<span class="si-step active">1<\/span>/);
});

test('connection phases advance the steps from the native progress text', () => {
  const state = stateFrom(PC, F.connecting());
  assert.equal(PC.pages.signinModel(state).phase, 3);
  const html = app(state);
  assert.match(html, /Настраиваем подключение…/);
  assert.equal((html.match(/si-step done/g) || []).length, 2);
  for (const [text, phase] of [['Проверяем сетевые компоненты…', 2], ['Подключаем общие папки…', 4], ['Вход подтверждён. Настраиваем защищённое соединение…', 2]])
    assert.equal(PC.pages.signinModel(stateFrom(PC, { ...F.connecting(), connectionProgress: text })).phase, phase, text);
});

test('a failed first sign-in keeps its own message on the sign-in screen', () => {
  const state = stateFrom(PC, F.connectError());
  const html = app(state);
  assert.match(html, /role="alert">Не удалось завершить подключение\. Повторите попытку\.<\/div>/);
  assert.match(html, /data-cmd="connect">Повторить подключение<\/button>/);
  PC.model.apply(state, { ...F.connecting(), notice: null });
  assert.equal(PC.model.connection(state).errorText, null, 'a new attempt clears the previous error');
});

test('background recovery keeps the main window: jobs, journal and updates stay reachable', () => {
  const state = stateFrom(PC, F.recovering());
  const conn = PC.model.connection(state);
  assert.equal(conn.showSignIn, false);
  const html = app(state);
  assert.match(html, /<aside class="side"/);
  assert.match(html, /Подключаемся к Платформе/);
  assert.match(html, /Восстанавливаем вход и защищённое соединение…/);
  assert.match(html, /class="chip t-run"[^>]*>.*Подключение…<\/span>/);
  assert.deepEqual([...html.matchAll(/data-ui="go" data-page="([a-z]+)" class="nav/g)].map(m => m[1]), ['home', 'jobs', 'settings'],
    'sections that need the server are gated by the empty grants');
  assert.match(PC.shell.app(stateFrom(PC, F.recovering(), { page: 'jobs' })), /Оптимизация IFC/);
});

test('a failed connection with a stored device shows «Нет связи» with a retry and keeps the rollback', () => {
  const state = stateFrom(PC, F.offline(), { page: 'settings' });
  const html = app(state);
  assert.doesNotMatch(html, /Войдите, чтобы подключить компьютер/);
  assert.match(html, /class="chip t-bad"[^>]*>.*Нет связи<\/span>/);
  assert.match(html, /Нет связи с Платформой[\s\S]*Не удалось завершить подключение\. Повторите попытку\.[\s\S]*data-cmd="reconnect" data-section="settings">Повторить подключение/);
  assert.match(html, /data-cmd="legacy-restore"[^>]*>Вернуть прежний коннектор/);
  assert.match(html, /data-cmd="settings-log"/);
  assert.match(html, /data-ui="palette" class="search-btn"/);
});

test('sign-in screen keeps the journal, diagnostics and the return to the previous connector', () => {
  const html = app(stateFrom(PC, F.signedOutTransferred()));
  assert.match(html, /Войдите, чтобы подключить компьютер/);
  assert.match(html, /data-cmd="settings-log" data-section="settings">Журнал/);
  assert.match(html, /data-cmd="diagnostics" data-section="settings">Сохранить диагностику/);
  assert.match(html, /data-cmd="legacy-restore" data-section="settings">Вернуть прежний Structura Connector/);
  assert.doesNotMatch(app(stateFrom(PC, F.signedOut())), /data-cmd="legacy-restore"/);
});

test('revoked access is reported and asks for a new sign-in', () => {
  const html = app(stateFrom(PC, { ...F.signedOut(), status: 'Доступ отозван', hasSavedCredential: true, commonAccessNeedsSignIn: true }));
  assert.match(html, /role="alert">Доступ отозван\.<\/div>/);
  assert.match(html, /data-cmd="connect">Войти через Платформу<\/button>/);
});

test('a device revoked on the server returns a signed-in window to the sign-in screen', () => {
  // Restore found the device revoked: the native side cleared the stored credential («Требуется вход»).
  const state = stateFrom(PC, F.signedIn());
  PC.model.apply(state, { ...F.signedIn(), commonAccessReady: false, commonAccessNeedsSignIn: true, hasSavedCredential: false,
    status: 'Требуется вход', allowedPages: { structura: [], platform: [] }, account: null,
    notice: { command: 'reconnect', message: 'Доступ этого компьютера отозван на сервере. Войдите через Платформу заново.', kind: 'warn', seq: 21 } });
  const conn = PC.model.connection(state);
  assert.equal(conn.showSignIn, true);
  assert.equal(conn.offline, false);
  const view = PC.pages.signinModel(state);
  assert.deepEqual({ ...view.primary }, { label: 'Войти через Платформу', cmd: 'connect' });
  assert.equal(view.secondary, null, 'nothing is left to sign out of');
  const html = app(state);
  assert.match(html, /Войдите, чтобы подключить компьютер/);
  assert.doesNotMatch(html, /<aside class="side"|data-cmd="reconnect"|data-cmd="signout"|Нет связи с Платформой/);
});

test('a revoked device whose credential is still stored also gets the sign-in screen', () => {
  const state = stateFrom(PC, { ...F.signedIn(), commonAccessReady: false, commonAccessNeedsSignIn: true, hasSavedCredential: true,
    status: 'Требуется вход', allowedPages: { structura: [], platform: [] } });
  assert.equal(PC.model.connection(state).showSignIn, true);
  const view = PC.pages.signinModel(state);
  assert.deepEqual({ ...view.primary }, { label: 'Войти через Платформу', cmd: 'connect' });
  assert.deepEqual({ ...view.secondary }, { label: 'Выйти', cmd: 'signout' });
});

test('sign-out in the main window says that access is being revoked on the server', () => {
  const html = app(stateFrom(PC, { ...F.signedIn(), commonAccessReady: false, connectPending: true, signOutPending: true,
    connectionProgress: 'Отзываем доступ на сервере…', status: 'Отзываем доступ на сервере…' }));
  assert.match(html, /Выходим из учётной записи<\/div><div style="color: var\(--text2\)">Отзываем доступ на сервере…/);
  assert.doesNotMatch(html, /Подключаемся к Платформе/);
  assert.match(html, /class="chip t-run"[^>]*>.*Выход…<\/span>/);
});

test('while signing out there is no second «Выйти» in the account menu or in the settings', () => {
  const signingOut = { ...F.signedIn(), commonAccessReady: false, connectPending: true, signOutPending: true,
    connectionProgress: 'Отзываем доступ на сервере…', status: 'Отзываем доступ на сервере…' };
  for (const page of ['home', 'settings']) {
    const html = app(stateFrom(PC, signingOut, { page, accountOpen: true }));
    assert.match(html, /data-ui="popover"/);
    assert.doesNotMatch(html, /data-cmd="signout"/, page);
  }
  assert.match(app(stateFrom(PC, signingOut, { page: 'settings' })), /Выходим из учётной записи…/);
  const idle = app(stateFrom(PC, F.signedIn(), { page: 'settings', accountOpen: true }));
  assert.equal((idle.match(/data-cmd="signout"/g) || []).length, 2, 'both «Выйти» buttons return after sign-out');
});

test('signed in without granted sections waits for the administrator', () => {
  const view = PC.pages.signinModel(stateFrom(PC, F.accessPending()));
  assert.equal(view.phase, 4);
  assert.equal(view.conn.variant, 'pending');
  assert.equal(view.primary.waiting, true);
  assert.deepEqual({ ...view.secondary }, { label: 'Выйти', cmd: 'signout' });
  assert.match(app(stateFrom(PC, F.accessPending())), /Администратор ещё не выдал доступ к разделам/);
});

test('a ready connection opens the main window with the menu and the search', () => {
  const html = app(stateFrom(PC, F.signedIn()));
  assert.match(html, /<aside class="side"/);
  assert.match(html, /data-ui="palette" class="search-btn"/);
  assert.match(html, /<span class="chip t-ok" style="align-self: center">.*Подключено<\/span>/);
  assert.doesNotMatch(html, /Войдите, чтобы подключить компьютер/);
});

test('with the system caption the page draws no window buttons and no drag region', () => {
  const html = app(stateFrom(PC, { ...F.signedIn(), nativeCaption: true }));
  assert.match(html, /<header class="titlebar native"/);
  assert.doesNotMatch(html, /data-cmd="window-/);
});

test('window buttons are native commands and the close button keeps the tray behaviour', () => {
  const html = app(stateFrom(PC, F.signedOut()));
  for (const cmd of ['window-minimize', 'window-maximize', 'window-close']) assert.match(html, new RegExp(`data-cmd="${cmd}"`));
  assert.match(html, /data-cmd="window-close" aria-label="Свернуть в трей"/);
  assert.match(app(stateFrom(PC, { ...F.signedOut(), windowMaximized: true })), /aria-label="Восстановить"/);
});

// CI runs only this file; the main-window cases are registered from here.
require('./shell-ui.cases.cjs');
require('./real-data.cases.cjs');
