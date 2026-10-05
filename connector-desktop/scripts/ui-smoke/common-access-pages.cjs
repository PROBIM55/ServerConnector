const { chromium } = require(process.argv[2]);
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
  const output = path.resolve(process.argv[3]);
  await fs.mkdir(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
  const errors = [];
  const network = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1180, height: 900 } });
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    page.on('requestfailed', request => network.push(request.url()));
    await page.addInitScript(() => {
      const listeners = [];
      window.__commands = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage: message => window.__commands.push(message),
        addEventListener: (event, callback) => { if (event === 'message') listeners.push(callback); }
      };
      window.__snapshot = payload => listeners.forEach(callback => callback({ data: { schemaVersion: 1, event: 'snapshot', payload } }));
    });
    await page.goto('http://127.0.0.1:8768/desktop.html', { waitUntil: 'networkidle' });
    const snapshot = {
      reset: true, allowedPages: { structura: ['tekla', 'converters'], platform: ['agr'] },
      connections: { structura: { status: 'Подключено', hasSavedCredential: true }, platform: { status: 'Подключено', hasSavedCredential: true } },
      availability: { agentControls: true, publishTekla: false }
    };
    await page.evaluate(value => window.__snapshot(value), snapshot);
    assert.deepEqual(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page)), ['overview', 'tekla', 'converters']);
    assert.equal(await page.locator('#connection-status').textContent(), 'Подключено');
    await page.locator('[data-mode="platform"]').click();
    assert.deepEqual(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page)), ['overview', 'agr']);
    assert.deepEqual(await page.locator('.overview-links [data-page]').evaluateAll(items => items.map(item => item.dataset.page)), ['agr']);
    assert.equal(await page.locator('.setting-row').filter({ hasText: 'Доступ к модулям' }).count(), 0);
    assert.equal(await page.locator('#connection-status').textContent(), 'Подключено');
    await page.screenshot({ path: path.join(output, 'common-platform.png') });
    await page.evaluate(() => window.__snapshot({ allowedPages: { structura: [], platform: [] }, connections: { structura: { status: 'Доступ отозван', hasSavedCredential: true }, platform: { status: 'Доступ отозван', hasSavedCredential: true } } }));
    assert.deepEqual(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page)), ['overview']);
    assert.equal(await page.locator('#connection-status').textContent(), 'Доступ отозван');
    assert.equal(await page.locator('.nav-link.active').getAttribute('data-page'), 'overview');
    await page.screenshot({ path: path.join(output, 'common-revoked.png') });
    assert.deepEqual(errors, []);
    assert.deepEqual(network, []);
    const commands = await page.evaluate(() => window.__commands.map(message => message.command));
    assert.deepEqual(commands, []);
    await fs.writeFile(path.join(output, 'browser-results.json'), JSON.stringify({ ok: true, errors, network, commands, checks: ['per-product-modules', 'shared-connection-state', 'revoke-removes-modules'], fixtureOnly: true }, null, 2));
    console.log('PASS: common connection states and per-product module navigation in actual Chrome; fixture only.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
