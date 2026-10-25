// Browser harness for connector-desktop/design: a loopback static server, headless Chrome through Playwright
// and a fake WebView2 bridge (records commands, replays snapshots). Nothing native runs.
// Environment: PLAYWRIGHT_PACKAGE — folder of the playwright package (required, no download);
//              CHROME_PATH — Chrome executable (default: the standard Google Chrome location).
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');

const DESIGN = path.resolve(__dirname, '../../design');
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.ttf': 'font/ttf', '.txt': 'text/plain; charset=utf-8', '.png': 'image/png', '.svg': 'image/svg+xml' };

function serve(root = DESIGN) {
  const server = http.createServer((request, response) => {
    const relative = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname).replace(/^\/+/, '');
    const file = path.resolve(root, relative || 'desktop.html');
    if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    fs.createReadStream(file).pipe(response);
  });
  return new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve({ server, url: `http://127.0.0.1:${server.address().port}` })));
}

function playwright() {
  const pkg = process.env.PLAYWRIGHT_PACKAGE;
  if (!pkg) throw new Error('Set PLAYWRIGHT_PACKAGE to the folder of an installed playwright package.');
  return createRequire(path.join(pkg, 'package.json'))('playwright');
}

async function launch() {
  const { chromium } = playwright();
  return chromium.launch({ executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
}

// Installed before the page scripts: chrome.webview as WebView2 exposes it to the document.
function installBridge() {
  const listeners = [];
  window.__commands = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: message => {
      const copy = JSON.parse(JSON.stringify(message));
      window.__commands.push(copy);
      const reply = window.__reply ? window.__reply(copy) : { ok: true, result: {} };
      queueMicrotask(() => listeners.forEach(callback => callback({ data: { schemaVersion: 1, id: copy.id, ...reply } })));
    },
    addEventListener: (event, callback) => { if (event === 'message') listeners.push(callback); }
  };
  window.__snapshot = payload => listeners.forEach(callback => callback({ data: { schemaVersion: 1, event: 'snapshot', payload } }));
}

async function openPage(browser, baseUrl, { width = 1280, height = 840 } = {}) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
  const problems = [];
  page.on('pageerror', error => problems.push('pageerror: ' + error.message));
  page.on('console', message => { if (message.type() === 'error') problems.push('console: ' + message.text()); });
  page.on('requestfailed', request => problems.push('requestfailed: ' + request.url()));
  page.on('response', response => { if (response.status() >= 400) problems.push(`http ${response.status()}: ${response.url()}`); });
  await page.addInitScript(installBridge);
  await page.goto(baseUrl + '/desktop.html', { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  const emit = async snapshot => { await page.evaluate(value => window.__snapshot(value), snapshot); await page.waitForTimeout(20); };
  const commands = () => page.evaluate(() => window.__commands.map(message => ({ command: message.command, payload: message.payload })));
  return { page, problems, emit, commands };
}

module.exports = { DESIGN, serve, launch, openPage, fixtures: require(path.join(DESIGN, 'tests/fixtures.cjs')) };
