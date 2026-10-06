// Fixture-only acceptance for the native common-folder page. No command is
// executed outside the mocked WebView bridge and no SMB/VPN endpoint is used.
const { createRequire } = require('node:module');
const { mkdirSync, writeFileSync } = require('node:fs');
const { resolve, join } = require('node:path');
const deps = createRequire('C:/Users/Lagom/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright/package.json');
const { chromium } = deps('playwright');

const output = resolve(process.argv[2] || 'artifacts/n5-common-folders-browser');
mkdirSync(output, { recursive: true });
const consoleErrors = [], networkErrors = [], failures = [], checks = [];
const check = (condition, name, details = undefined) => {
  checks.push(name);
  if (!condition) failures.push({ check: name, details });
};

const folder = (resourceId, displayName, drive = null) => ({ resourceId, displayName, drive });
const baseSnapshot = assignedFolders => ({
  reset: true,
  commonAccessSelected: true,
  allowedPages: { structura: ['folders'], platform: ['agr'] },
  connections: {
    structura: { status: 'Подключено', hasSavedCredential: true },
    platform: { status: 'Подключено', hasSavedCredential: true }
  },
  status: 'Подключено',
  assignedFolders,
  folderStatus: assignedFolders.length ? 'Готово' : 'Папки не назначены'
});

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
    headless: true
  });
  let page;
  try {
    const context = await browser.newContext({ viewport: { width: 1180, height: 900 } });
    await context.addInitScript(() => {
      const listeners = [];
      window.__commands = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage: message => {
          const copy = JSON.parse(JSON.stringify(message));
          window.__commands.push(copy);
          queueMicrotask(() => listeners.forEach(callback => callback({
            data: { schemaVersion: 1, id: copy.id, ok: true, result: {} }
          })));
        },
        addEventListener: (event, callback) => { if (event === 'message') listeners.push(callback); }
      };
      window.__snapshot = payload => listeners.forEach(callback => callback({
        data: { schemaVersion: 1, event: 'snapshot', payload }
      }));
    });
    page = await context.newPage();
    page.on('pageerror', error => consoleErrors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
    page.on('requestfailed', request => networkErrors.push({ url: request.url(), error: request.failure()?.errorText }));
    page.on('response', response => { if (response.status() >= 400) networkErrors.push({ url: response.url(), status: response.status() }); });
    await page.goto('http://127.0.0.1:8768/desktop.html', { waitUntil: 'networkidle' });

    const emit = async snapshot => {
      check(snapshot.assignedFolders.every(item =>
        Object.keys(item).sort().join(',') === 'displayName,drive,resourceId'),
      'snapshot-folder-contract-safe');
      await page.evaluate(value => window.__snapshot(value), snapshot);
      await page.waitForTimeout(30);
    };
    const noHorizontalOverflow = async name => check(
      !await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), name,
      await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, innerWidth })));
    const bodyText = () => page.locator('body').innerText();

    await emit(baseSnapshot([]));
    check(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page).join(',')) === 'overview,folders',
      'structura-folder-permission');
    await page.locator('[data-mode="platform"]').click();
    check(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page).join(',')) === 'overview,agr',
      'platform-permission-does-not-leak-folders');
    await page.locator('[data-mode="structura"]').click();
    await page.locator('#navigation [data-page="folders"]').click();
    check((await bodyText()).includes('Папки не назначены'), 'zero-folders-empty-state');
    check(await page.locator('[data-act="models-open"]').isDisabled(), 'zero-folders-open-disabled');
    check(await page.locator('[data-act="models-mount"]').isDisabled(), 'zero-folders-mount-disabled');
    check(await page.locator('[data-act="models-unmount"]').isDisabled(), 'zero-folders-unmount-disabled');
    await noHorizontalOverflow('desktop-zero-no-horizontal-overflow');
    await page.screenshot({ path: join(output, 'folders-0-desktop.png'), fullPage: true });

    await emit(baseSnapshot([folder('project-alpha', 'Проект Альфа')]));
    check((await bodyText()).includes('Проект Альфа'), 'one-folder-content');
    check(await page.locator('#share-resource').count() === 0, 'one-folder-no-redundant-selector');
    check(!await page.locator('[data-act="models-open"]').isDisabled(), 'one-folder-open-enabled');
    check(!await page.locator('[data-act="models-mount"]').isDisabled(), 'one-folder-mount-enabled');
    check(await page.locator('[data-act="models-unmount"]').isDisabled(), 'one-folder-unmount-disabled');
    await page.locator('#drive').selectOption({ label: 'Y:' });
    await page.locator('[data-act="models-mount"]').click();
    await page.locator('[data-act="models-open"]').click();
    await noHorizontalOverflow('desktop-one-no-horizontal-overflow');
    await page.screenshot({ path: join(output, 'folders-1-desktop.png'), fullPage: true });

    const twoFolders = [folder('project-alpha', 'Проект Альфа', 'Z:'), folder('project-beta', 'Проект Бета')];
    await emit(baseSnapshot(twoFolders));
    check(await page.locator('#share-resource').count() === 1, 'two-folders-selector-visible');
    check(await page.locator('#share-resource').inputValue() === 'project-alpha', 'two-folders-first-selected');
    check((await bodyText()).includes('Подключено как Z:'), 'mounted-drive-visible');
    await page.locator('#share-resource').selectOption('project-beta');
    await page.waitForTimeout(30);
    await page.screenshot({ path: join(output, 'folders-2-beta-selection-desktop.png'), fullPage: true });
    check((await bodyText()).includes('Проект Бета') && !(await bodyText()).includes('Подключено как Z:'),
      'selector-switches-visible-folder-content', { selected: await page.locator('#share-resource').inputValue() });
    await page.locator('#drive').selectOption({ label: 'X:' });
    await page.locator('[data-act="models-mount"]').click();
    await page.locator('[data-act="models-open"]').click();
    await page.locator('#share-resource').selectOption('project-alpha');
    await page.waitForTimeout(30);
    check((await bodyText()).includes('Подключено как Z:'), 'selector-restores-mounted-drive-content');
    check(!await page.locator('[data-act="models-unmount"]').isDisabled(), 'mounted-folder-unmount-enabled');
    await page.locator('[data-act="models-open"]').click();
    await page.locator('[data-act="models-unmount"]').click();
    await noHorizontalOverflow('desktop-two-no-horizontal-overflow');
    await page.screenshot({ path: join(output, 'folders-2-desktop.png'), fullPage: true });

    await page.setViewportSize({ width: 390, height: 844 });
    await emit(baseSnapshot(twoFolders));
    await noHorizontalOverflow('mobile-two-no-horizontal-overflow');
    check(await page.locator('[data-act="models-open"]').isVisible(), 'mobile-primary-action-visible');
    await page.screenshot({ path: join(output, 'folders-2-mobile.png'), fullPage: true });

    await page.setViewportSize({ width: 1180, height: 900 });
    await emit({
      ...baseSnapshot([]),
      allowedPages: { structura: [], platform: [] },
      connections: {
        structura: { status: 'Доступ отозван', hasSavedCredential: true },
        platform: { status: 'Доступ отозван', hasSavedCredential: true }
      },
      status: 'Доступ отозван', folderStatus: 'Доступ отозван'
    });
    check(await page.locator('#navigation [data-page]').evaluateAll(items => items.map(item => item.dataset.page).join(',')) === 'overview',
      'revoke-removes-folder-page');
    check(await page.locator('.nav-link.active').getAttribute('data-page') === 'overview', 'revoke-returns-overview');
    check(await page.locator('#connection-status').textContent() === 'Доступ отозван', 'revoke-status-visible');
    check(!(await bodyText()).includes('Проект Альфа') && !(await bodyText()).includes('Проект Бета'), 'revoke-clears-folder-content');
    await noHorizontalOverflow('desktop-revoke-no-horizontal-overflow');
    await page.screenshot({ path: join(output, 'folders-revoked-desktop.png'), fullPage: true });

    const commands = await page.evaluate(() => window.__commands);
    const byCommand = command => commands.filter(item => item.command === command);
    const betaMount = byCommand('models-mount').find(item => item.payload?.fields?.['share-resource'] === 'project-beta');
    check(betaMount?.payload?.fields?.drive === 'X:', 'mount-captures-current-resource-and-drive', betaMount?.payload);
    check(byCommand('models-open').some(item => item.payload?.fields?.['share-resource'] === 'project-beta'),
      'open-captures-current-resource');
    const alphaOpen = byCommand('models-open').find(item => item.payload?.fields?.['share-resource'] === 'project-alpha');
    check(alphaOpen?.payload?.fields?.drive === 'Z:', 'open-captures-switched-resource-and-mounted-drive', alphaOpen?.payload);
    const alphaUnmount = byCommand('models-unmount').find(item => item.payload?.fields?.['share-resource'] === 'project-alpha');
    check(alphaUnmount?.payload?.fields?.drive === 'Z:', 'unmount-captures-current-resource-and-mounted-drive', alphaUnmount?.payload);
    const serialized = JSON.stringify({ commands, dom: await page.content() });
    check(!/\\\\(?:\d{1,3}\\.){3}\d{1,3}\\/.test(serialized) &&
      !serialized.includes('SMBHOST\\\\cnb_fixture') && !serialized.includes('fixture-smb-secret'),
      'no-unc-or-credentials-in-dom-or-commands');
    check(consoleErrors.length === 0, 'no-console-errors', consoleErrors);
    check(networkErrors.length === 0, 'no-network-errors', networkErrors);

    const result = {
      outcome: failures.length ? 'failed' : 'passed', fixtureOnly: true,
      url: page.url(), desktop: [1180, 900], mobile: [390, 844],
      checks, failures, consoleErrors, networkErrors,
      commands: commands.map(item => ({ command: item.command, payload: item.payload }))
    };
    writeFileSync(join(output, 'browser-results.json'), JSON.stringify(result, null, 2));
    if (failures.length) throw new Error(`Common-folder browser acceptance failed: ${failures.map(item => item.check).join(', ')}`);
    console.log(`PASS: ${checks.length} common-folder checks in actual headless Google Chrome; fixture only.`);
  } catch (error) {
    writeFileSync(join(output, 'browser-failure.txt'), error.stack || String(error));
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
