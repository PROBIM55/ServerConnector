// Browser acceptance fixture for the actual Platform cabinet component.
// Every API request is intercepted; this never changes a live account or device.
const { createRequire } = require('node:module');
const { mkdirSync, writeFileSync } = require('node:fs');
const { resolve, join } = require('node:path');
const deps = createRequire('C:/Users/Lagom/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/package.json');
const { chromium } = deps('playwright');
const output = resolve(process.argv[2]);
mkdirSync(output, { recursive: true });
const errors = [], network = [], mutations = [];
let revision = 4, savedModules = [], revoked = false;
const company = 'company-fixture';
const user = 'person-fixture';
const base = '/api/platform/admin/connector/access/v1';
const catalog = { modules: [{ product: 0, moduleId: 'tekla', label: 'Tekla', permissions: [0, 1] }, { product: 1, moduleId: 'agr', label: 'АГР', permissions: [0, 1] }], resources: [{ resourceId: 'folder-fixture', resourceKind: 'smb', projectId: null, label: 'Общая папка проекта', permissions: [0] }] };
const profile = () => ({ userId: user, companyId: company, revision, modules: savedModules, resources: [] });
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1180, height: 900 } });
    await context.addInitScript(() => localStorage.setItem('platform.welcome.hideOnStartup', '1'));
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    page.on('response', response => { if (response.status() >= 400) network.push({ url: response.url(), status: response.status() }); });
    await page.route('**/api/**', async route => {
      const req = route.request(), path = new URL(req.url()).pathname;
      if (!path.startsWith('/api/')) { await route.continue(); return; }
      let body = { ok: true, items: [], devices: [], total: 0 };
      if (path.endsWith('/auth/me')) body = { ok: true, user: { id: 'admin-fixture', username: 'admin', display_name: 'Администратор', role: 'operator', product_role: 'user', company_memberships: [{ company_id: company, company_name: 'Компания', role: 'company_admin', is_active: true }] }, company: { id: company, name: 'Компания' }, devices: [], authorization: { context: { company_id: company, project_id: null }, product_role: 'user', company_role: 'company_admin', permissions: [] } };
      else if (path.endsWith('/company/users')) body = { ok: true, items: [{ userId: user, username: 'anna', displayName: 'Анна', isActive: true }], total: 1 };
      else if (path === `${base}/devices`) body = { items: [{ deviceId: 'device-fixture', userId: user, companyId: company, displayName: 'Рабочая станция', status: revoked ? 'revoked' : 'active', desiredRevision: revision, appliedRevision: 3, certificateNotAfterUtc: '2027-01-02T12:00:00Z', providers: [] }] };
      else if (path === `${base}/catalog`) body = catalog;
      else if (path === `${base}/users/${user}/profile`) body = profile();
      else if (path === `${base}/users/${user}/grants`) { const payload = req.postDataJSON(); mutations.push({ action: 'grants', ...payload }); if (payload.expectedRevision !== revision || payload.companyId !== company) throw new Error('Incorrect admin grant scope/revision'); savedModules = payload.modules; revision++; body = profile(); }
      else if (path === `${base}/enrollment-tokens`) { const payload = req.postDataJSON(); if (payload.companyId !== company || payload.userId !== user || payload.lifetimeSeconds !== 900) throw new Error('Incorrect token scope'); mutations.push({ action: 'issue-token', companyId: payload.companyId, userId: payload.userId }); body = { tokenId: 'fixture', token: 'one-time-browser-fixture', expiresAtUtc: '2026-10-02T12:00:00Z' }; }
      else if (path === `${base}/devices/device-fixture/revoke`) { mutations.push({ action: 'revoke', ...req.postDataJSON() }); revoked = true; await route.fulfill({ status: 204 }); return; }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    });
    await page.goto('http://127.0.0.1:47137/cabinet/connectors', { waitUntil: 'domcontentloaded' });
    await page.getByRole('heading', { name: 'Коннектор', exact: true }).waitFor({ timeout: 45000 }).catch(async error => {
      writeFileSync(join(output, 'page-diagnostic.json'), JSON.stringify({ errors, network, url: page.url(), body: await page.locator('body').innerText() }, null, 2));
      throw error;
    });
    await page.getByLabel('Пользователь', { exact: true }).selectOption(user);
    await page.getByRole('switch', { name: 'Tekla: Запуск' }).click();
    if (await page.getByRole('button', { name: 'Выдать токен подключения' }).isEnabled()) throw new Error('Token allowed before saving dirty permissions');
    await page.getByRole('button', { name: 'Сохранить права' }).click();
    await page.getByText(/Права сохранены/).waitFor();
    if (process.env.CONNECTOR_CAPTURE_PIXELS === '1') await page.screenshot({ path: join(output, 'admin-desktop.png') });
    await page.getByRole('button', { name: 'Выдать токен подключения' }).click();
    await page.getByText('one-time-browser-fixture').waitFor();
    await page.getByRole('button', { name: 'Закрыть', exact: true }).filter({ hasText: 'Закрыть' }).click();
    if (await page.getByText('one-time-browser-fixture').count()) throw new Error('Token remains in DOM after close');
    await page.getByRole('button', { name: 'Отозвать', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Отозвать', exact: true }).click();
    await page.getByText('Отозван', { exact: true }).waitFor();
    await page.setViewportSize({ width: 390, height: 844 });
    if (process.env.CONNECTOR_CAPTURE_PIXELS === '1') await page.screenshot({ path: join(output, 'admin-mobile.png') });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    if (overflow || errors.length || network.length) throw new Error(JSON.stringify({ overflow, errors, network }));
    writeFileSync(join(output, 'browser-results.json'), JSON.stringify({ outcome: 'passed', fixture: true, url: page.url(), desktop: [1180, 900], mobile: [390, 844], errors, network, mutations, overflow, screenshots: process.env.CONNECTOR_CAPTURE_PIXELS === '1' }, null, 2));
    console.log('PASS: Platform connector cabinet, scope/revision/save/token/revoke/mobile; fixture API only.');
  } finally { await browser.close(); }
})().catch(error => { writeFileSync(join(output, 'browser-failure.txt'), error.stack ?? String(error)); console.error(error.message); process.exitCode = 1; });
