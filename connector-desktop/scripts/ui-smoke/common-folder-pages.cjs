// Fixture-only browser acceptance of the «Папки» section. No command leaves the mocked WebView bridge;
// no SMB/VPN endpoint is touched. Usage: PLAYWRIGHT_PACKAGE=<playwright folder> node common-folder-pages.cjs <output>
const fs = require('node:fs');
const path = require('node:path');
const { serve, launch, openPage, fixtures } = require('./harness.cjs');
const F = fixtures.variants;

const folder = (resourceId, displayName, drive = null, viaLegacyConnector = false) => ({ resourceId, displayName, drive, viaLegacyConnector });

(async () => {
  const output = path.resolve(process.argv[2] || 'artifacts/common-folders-browser');
  fs.mkdirSync(output, { recursive: true });
  const failures = [], checks = [];
  const check = (condition, name, details) => { checks.push(name); if (!condition) failures.push({ name, details }); };
  const { server, url } = await serve();
  const browser = await launch();
  let problems = [];
  try {
    const opened = await openPage(browser, url, { width: 920, height: 720 });
    const { page, emit, commands } = opened;
    problems = opened.problems;
    const text = () => page.locator('#app').innerText();
    const last = async () => (await commands()).at(-1);
    const noOverflow = async name => check(!await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), name);

    // No folders: the section is still reachable and explains who assigns folders.
    await emit({ ...F.signedIn(), assignedFolders: [], folderStatus: 'Папки не назначены', legacyTransfer: { status: '', transferred: false, canTransfer: true, canRestore: false } });
    await page.click('[data-ui="go"][data-page="folders"]');
    check((await text()).includes('Папки ещё не назначены'), 'zero-folders-empty-state');
    check(await page.locator('[data-cmd="models-mount"],[data-cmd="models-open"]').count() === 0, 'zero-folders-no-actions');
    await noOverflow('zero-no-horizontal-overflow');

    // Neighbour mode: BIM_Models stays with the previous connector, the company folder works here.
    await emit(F.signedIn());
    check((await text()).includes('На компьютере работает прежний Structura Connector'), 'neighbour-banner');
    check((await text()).includes('Через прежний'), 'neighbour-folder-state');
    check(await page.locator('[data-cmd][data-field-share-resource="bim"]').count() === 0, 'neighbour-folder-has-no-native-action');
    await page.click('[data-cmd="models-open"][data-field-share-resource="company"]');
    const open = await last();
    check(open.command === 'models-open' && open.payload.fields['share-resource'] === 'company' && open.payload.mode === 'structura', 'open-sends-folder-and-product', open);
    await page.screenshot({ path: path.join(output, 'folders-neighbour.png') });

    // Disconnection and transfer are confirmed once, natively; the page sends the request directly.
    await page.click('[data-cmd="models-unmount"][data-field-share-resource="company"]');
    check(await page.locator('[role="dialog"]').count() === 0, 'no-second-confirmation-in-page');
    const unmount = await last();
    check(unmount.command === 'models-unmount' && unmount.payload.fields.drive === 'Z:' && unmount.payload.fields['share-resource'] === 'company', 'unmount-command', unmount);

    await page.locator('.banner [data-cmd="legacy-transfer"]').click();
    check((await last()).command === 'legacy-transfer', 'transfer-command');

    // After the transfer: BIM_Models can get a drive letter here; the rollback is offered.
    await emit(F.transferred());
    check((await text()).includes('Компьютер переведён на Платформа Коннектор'), 'transferred-banner');
    await page.selectOption('select[data-ui="drive"][data-id="bim"]', 'Y:');
    await page.click('[data-cmd="models-mount"][data-field-share-resource="bim"]');
    const mount = await last();
    check(mount.command === 'models-mount' && mount.payload.fields.drive === 'Y:', 'mount-uses-chosen-letter', mount);
    await page.click('[data-cmd="legacy-restore"]');
    check((await last()).command === 'legacy-restore', 'restore-command');
    await noOverflow('transferred-no-horizontal-overflow');
    await page.screenshot({ path: path.join(output, 'folders-transferred.png') });

    // Folders granted only by the Platform product are checked against that product.
    await emit({ ...F.transferred(), allowedPages: { structura: [], platform: ['folders'] } });
    await page.click('[data-cmd="models-open"][data-field-share-resource="company"]');
    check((await last()).payload.mode === 'platform', 'platform-only-grant-product');

    // A refusal is shown as an error toast and as the page note; nothing is lost.
    await page.evaluate(() => window.CONNECTOR_DESKTOP_COMMAND_UNAVAILABLE('models-open', 'Выберите папку проекта.'));
    await page.waitForTimeout(20);
    check(await page.locator('.toast.error').innerText().then(t => t.includes('Выберите папку проекта.')), 'error-toast');
    check(await page.locator('.page-note').innerText().then(t => t.includes('Выберите папку проекта.')), 'error-page-note');
    await page.screenshot({ path: path.join(output, 'folders-error.png') });
  } finally { await browser.close(); server.close(); }
  const result = { ok: !failures.length && !problems.length, checks, failures, problems, fixtureOnly: true };
  fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify(result, null, 2));
  if (!result.ok) { console.error(JSON.stringify({ failures, problems }, null, 2)); process.exitCode = 1; }
  else console.log(`PASS: ${checks.length} folder checks in Chrome; fixture only.`);
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
