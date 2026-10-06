const { chromium } = require(process.argv[2]);
const fs = require('node:fs/promises');
const path = require('node:path');

const baseUrl = 'http://127.0.0.1:8768';
const cases = [
  ['tekla-ifc', '?page=tekla&sub=ifc'],
  ['tekla-exports', '?page=tekla&sub=exports'],
  ['converters', '?page=converters'],
];
const fixture = query => {
  const params = new URLSearchParams(query);
  return { reset:true, mode:params.get('mode') === 'platform' ? 'platform' : 'structura', page:params.get('page') || 'overview', tekla:params.get('page') === 'tekla' ? params.get('sub') || 'standard' : 'standard', converter:params.get('page') === 'converters' ? params.get('sub') || 'catalog' : 'catalog', files:[], ifcFiles:[], history:[], moduleFields:{} };
};
const normalize = page => page.addStyleTag({ content: '.review-bar,.review-note{display:none!important}.prototype,.app-shell{min-height:100vh!important}' });
const metrics = page => page.evaluate(() => [...document.querySelectorAll('.app-shell,.side-nav,.workspace,.page-head,#page-content,.tabs,.panel,.field')].map(element => {
  const rect = element.getBoundingClientRect();
  return { selector: element.id ? `#${element.id}` : element.className, x:rect.x, y:rect.y, width:rect.width, height:rect.height };
}));

(async () => {
  const output = path.resolve(process.argv[3]);
  await fs.mkdir(output, { recursive:true });
  const browser = await chromium.launch({ executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe', headless:true });
  const report = { viewport:{width:Number(process.argv[4])||1180,height:Number(process.argv[5])||900}, cases:[], errors:[] };
  try {
    for (const [name, query] of cases) {
      const approved = await browser.newPage({ viewport:report.viewport, deviceScaleFactor:1 });
      const desktop = await browser.newPage({ viewport:report.viewport, deviceScaleFactor:1 });
      const errors = [];
      for (const page of [approved, desktop]) {
        page.on('pageerror', error => errors.push(error.message));
        page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
        page.on('requestfailed', request => errors.push(`${request.url()} ${request.failure()?.errorText}`));
      }
      await desktop.addInitScript(() => {
        const listeners = [];
        window.__desktopCommandCount = 0;
        window.chrome = window.chrome || {};
        window.chrome.webview = { postMessage:() => { window.__desktopCommandCount += 1; }, addEventListener:(name, listener) => { if (name === 'message') listeners.push(listener); } };
        window.__emitDesktopSnapshot = snapshot => listeners.forEach(listener => listener({ data:{schemaVersion:1,event:'snapshot',payload:snapshot} }));
      });
      await approved.goto(baseUrl + '/' + query, { waitUntil:'networkidle' });
      await desktop.goto(baseUrl + '/desktop.html' + query, { waitUntil:'networkidle' });
      await desktop.evaluate(snapshot => window.__emitDesktopSnapshot(snapshot), fixture(query));
      await Promise.all([normalize(approved), normalize(desktop)]);
      const [approvedPng, desktopPng, approvedMetrics, desktopMetrics, commandCount] = await Promise.all([
        approved.locator('.app-shell').screenshot({ path:path.join(output, `${name}.approved.png`) }),
        desktop.locator('.app-shell').screenshot({ path:path.join(output, `${name}.desktop.png`) }),
        metrics(approved), metrics(desktop), desktop.evaluate(() => window.__desktopCommandCount),
      ]);
      const pixelEqual = approvedPng.equals(desktopPng);
      const layoutEqual = JSON.stringify(approvedMetrics) === JSON.stringify(desktopMetrics);
      const result = { name, query, pixelEqual, layoutEqual, commandCount, errors, approvedBytes:approvedPng.length, desktopBytes:desktopPng.length, approvedMetrics, desktopMetrics };
      report.cases.push(result);
      if (!pixelEqual || !layoutEqual || commandCount !== 0 || errors.length) report.errors.push(name);
      await Promise.all([approved.close(), desktop.close()]);
    }
    await fs.writeFile(path.join(output, 'desktop-comparison.json'), JSON.stringify(report, null, 2));
    if (report.errors.length) throw new Error(`desktop comparison failed: ${report.errors.join(', ')}`);
    console.log('PASS: exact desktop/app-shell pixel and layout equality for 3 pages; 0 desktop commands; 0 console/network errors.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
