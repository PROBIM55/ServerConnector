const { chromium } = require(process.argv[2]);
const fs = require('node:fs/promises');
const path = require('node:path');
(async () => {
  const output = path.resolve(process.argv[3]); await fs.mkdir(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1180, height: 900 }, deviceScaleFactor: 1 });
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    page.on('console', e => { if (e.type() === 'error') errors.push(e.text()); });
    page.on('requestfailed', e => errors.push(`${e.url()} ${e.failure()?.errorText}`));
    const snapshots = [];
    for (const [name, query] of [['tekla-ifc', '?page=tekla&sub=ifc'], ['tekla-exports', '?page=tekla&sub=exports'], ['converters', '?page=converters']]) {
      await page.goto('http://127.0.0.1:8768/' + query, { waitUntil: 'networkidle' });
      await page.locator('.app-shell').screenshot({ path: path.join(output, name + '.png') });
      snapshots.push(await page.evaluate(name => {
        const measure = e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return { tag: e.tagName, text: e.innerText?.slice(0, 180), x: r.x, y: r.y, width: r.width, height: r.height, color: s.color, background: s.backgroundColor, border: s.borderColor, font: s.font, padding: s.padding, margin: s.margin }; };
        return { name, url: location.href, title: document.querySelector('#page-title').textContent, text: document.querySelector('#page-content').innerText, elements: [...document.querySelectorAll('.side-nav,.workspace,.page-head,.product-switch,.nav-link,.panel,.field,.tabs,.converter-catalog,.converter-picker,button.primary,button.secondary')].map(measure), tokens: window.CONNECTOR_KIT_TOKENS ?? null };
      }, name));
    }
    await fs.writeFile(path.join(output, 'approved-metrics.json'), JSON.stringify({ errors, snapshots }, null, 2));
    if (errors.length) throw new Error(errors.join('\n'));
    console.log('PASS: approved live HTML captured in headless Google Chrome, 3 pages, zero console/page/request errors.');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
