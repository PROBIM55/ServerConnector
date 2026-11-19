// Fixture-only browser acceptance of the shared account: one menu for both products, commands sent with the
// product that grants them, sign-in on revoke, theme, palette, window buttons and notifications.
// Usage: PLAYWRIGHT_PACKAGE=<playwright folder> node common-access-pages.cjs <output>
const fs = require('node:fs');
const path = require('node:path');
const { serve, launch, openPage, fixtures } = require('./harness.cjs');
const F = fixtures.variants;

(async () => {
  const output = path.resolve(process.argv[2] || 'artifacts/common-access-browser');
  fs.mkdirSync(output, { recursive: true });
  const failures = [], checks = [];
  const check = (condition, name, details) => { checks.push(name); if (!condition) failures.push({ name, details }); };
  const { server, url } = await serve();
  const browser = await launch();
  let problems = [];
  try {
    const opened = await openPage(browser, url);
    const { page, emit, commands } = opened;
    problems = opened.problems;
    const nav = () => page.locator('.side [data-ui="go"]').evaluateAll(items => items.map(item => item.dataset.page).join(','));
    const last = async () => (await commands()).at(-1);

    await emit({ ...F.signedIn(), allowedPages: { structura: ['tekla', 'converters'], platform: ['agr'] } });
    check(await nav() === 'home,jobs,tekla,converters,agr,settings', 'union-of-product-grants', await nav());
    check(await page.locator('[data-mode]').count() === 0, 'no-product-switch');
    check((await page.locator('.titlebar .chip').innerText()).includes('Подключено'), 'status-chip');

    await page.click('[data-ui="go"][data-page="tekla"]');
    check(await page.locator('[role="tablist"][aria-label="Разделы Tekla"]').count() === 1, 'structura-tekla-tabs');
    check(!(await page.locator('#app').innerText()).includes('Мост к Tekla'), 'no-bridge-without-platform-grant');
    await page.click('[data-cmd="firm-sync-all"]');
    check((await last()).payload.mode === 'structura', 'firm-sync-structura-product');

    await page.click('[data-ui="go"][data-page="agr"]');
    await page.click('[data-cmd="agr-probe"]');
    check((await last()).command === 'agr-probe' && (await last()).payload.mode === 'platform', 'agr-platform-product');
    await page.screenshot({ path: path.join(output, 'common-agr.png') });

    await page.click('[data-ui="go"][data-page="settings"]');
    await page.click('[data-ui="theme"][data-id="light"]');
    const theme = await last();
    check(theme.command === 'ui-theme' && theme.payload.fields['ui-theme'] === 'light', 'theme-saved-natively', theme);
    check(await page.evaluate(() => document.documentElement.dataset.theme) === 'light', 'theme-applied');
    await page.screenshot({ path: path.join(output, 'common-settings-light.png') });

    await page.keyboard.press('Control+K');
    await page.locator('#palette-input').fill('blender');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(30);
    check((await last()).command === 'agr-probe', 'palette-runs-registry-command', await last());

    for (const command of ['window-minimize', 'window-maximize', 'window-close']) {
      await page.click(`[data-cmd="${command}"]`);
      check((await last()).command === command, command);
    }

    await emit({ ...F.signedIn(), reset: false, notice: { command: 'firm-sync-all', message: 'Готово.', kind: 'ok', seq: 40 } });
    check(await page.locator('.toast.ok').count() === 1, 'success-toast');
    await emit({ ...F.signedIn(), reset: false, notice: { command: 'firm-sync-all', message: 'Готово.', kind: 'ok', seq: 40 } });
    check(await page.locator('.toast.ok').count() === 1, 'notice-shown-once');

    await emit({ ...F.signedOut(), status: 'Доступ отозван', hasSavedCredential: true });
    check(await page.locator('.side').count() === 0, 'revoke-removes-menu');
    check((await page.locator('#app').innerText()).includes('Доступ отозван.'), 'revoke-explained');
    await page.screenshot({ path: path.join(output, 'common-revoked.png') });
    await page.click('[data-cmd="connect"]');
    check((await last()).command === 'connect', 'sign-in-again');
  } finally { await browser.close(); server.close(); }
  const result = { ok: !failures.length && !problems.length, checks, failures, problems, fixtureOnly: true };
  fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify(result, null, 2));
  if (!result.ok) { console.error(JSON.stringify({ failures, problems }, null, 2)); process.exitCode = 1; }
  else console.log(`PASS: ${checks.length} shared-account checks in Chrome; fixture only.`);
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
