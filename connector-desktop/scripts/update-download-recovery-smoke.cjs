const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const desktopRoot = path.resolve(__dirname, '..');
const packageRoot = path.join(
  desktopRoot,
  'artifacts',
  'package-update',
  'preview',
  'cc9d64f879c841418191327f69be6f7a',
);
const artifactRoot = path.join(desktopRoot, 'artifacts', 'update-download-recovery');
const vpkPath = path.join(desktopRoot, '.tools', 'vpk-1.2.161', 'vpk.exe');
const channel = 'preview';
const childTimeoutMs = 85_000;
const abortAfterBytes = 1024 * 1024;

function requireInside(parent, candidate, name) {
  const root = path.resolve(parent) + path.sep;
  const value = path.resolve(candidate);
  if (!value.startsWith(root)) throw new Error(`${name} escaped its owned root: ${value}`);
  return value;
}

async function requireFile(file, name) {
  const stat = await fsp.stat(file).catch(() => null);
  if (!stat?.isFile()) throw new Error(`${name} was not found: ${file}`);
  return stat;
}

async function sha256(file) {
  return await new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha256');
    const input = fs.createReadStream(file);
    input.on('error', reject);
    input.on('data', chunk => hash.update(chunk));
    input.on('end', () => resolve(hash.digest('hex')));
  });
}

async function snapshotFiles(root, includeHashes = false) {
  const files = [];
  async function visit(directory) {
    for (const entry of await fsp.readdir(directory, { withFileTypes: true }).catch(() => [])) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile()) {
        const stat = await fsp.stat(full);
        files.push({
          path: path.relative(root, full).replaceAll('\\', '/'),
          size: stat.size,
          ...(includeHashes ? { sha256: await sha256(full) } : {}),
        });
      }
    }
  }
  await visit(root);
  return files.sort((left, right) => left.path.localeCompare(right.path));
}

function runKeyFingerprint() {
  const query = spawnSync(
    'reg.exe',
    ['query', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run', '/v', 'ConnectorAgentDesktop'],
    { windowsHide: true, encoding: 'buffer', timeout: 5_000 },
  );
  if (query.error) throw query.error;
  const hash = crypto.createHash('sha256');
  hash.update(String(query.status));
  hash.update(query.stdout ?? Buffer.alloc(0));
  hash.update(query.stderr ?? Buffer.alloc(0));
  return hash.digest('hex');
}

function parseRange(header, size) {
  if (!header) return { start: 0, end: size - 1, partial: false };
  const match = /^bytes=(\d+)-(\d*)$/.exec(header);
  if (!match) return null;
  const start = Number(match[1]);
  const end = match[2] ? Number(match[2]) : size - 1;
  if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end < start || start >= size) return null;
  return { start, end: Math.min(end, size - 1), partial: true };
}

function isExactFeedQuery(parsed, releasesName) {
  if (parsed.pathname !== `/${releasesName}`) return parsed.search === '';
  const entries = [...parsed.searchParams.entries()].sort(([left], [right]) => left.localeCompare(right));
  return JSON.stringify(entries) === JSON.stringify([
    ['arch', 'x64'],
    ['os', 'win'],
    ['rid', 'win-x64'],
  ]);
}

function runVpkDownload(url, outputDirectory, logPrefix) {
  return new Promise(resolve => {
    const args = [
      '--skip-updates',
      'download', 'http',
      '--url', url,
      '--outputDir', outputDirectory,
      '--channel', channel,
      '--timeout', '1',
    ];
    const child = spawn(vpkPath, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    const stdout = [];
    const stderr = [];
    let timedOut = false;
    let spawnError = null;
    child.stdout.on('data', chunk => stdout.push(chunk));
    child.stderr.on('data', chunk => stderr.push(chunk));
    child.on('error', error => { spawnError = error.message; });
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, childTimeoutMs);
    child.on('close', async (exitCode, signal) => {
      clearTimeout(timer);
      const stdoutText = Buffer.concat(stdout).toString('utf8');
      const stderrText = Buffer.concat(stderr).toString('utf8');
      await Promise.all([
        fsp.writeFile(`${logPrefix}.stdout.log`, stdoutText, 'utf8'),
        fsp.writeFile(`${logPrefix}.stderr.log`, stderrText, 'utf8'),
      ]);
      resolve({ exitCode, signal, timedOut, spawnError, args, stdoutBytes: Buffer.byteLength(stdoutText), stderrBytes: Buffer.byteLength(stderrText) });
    });
  });
}

async function main() {
  await requireFile(vpkPath, 'Pinned vpk');
  const metadata = JSON.parse(await fsp.readFile(path.join(packageRoot, 'local-package.json'), 'utf8'));
  if (metadata.applicationId !== 'Structura.Connector.UpdateSmoke' || metadata.version !== '1.1.0-smoke.8' ||
      metadata.channel !== channel || metadata.packTitle !== 'Structura Connector Update Smoke' || metadata.shortcuts !== 'None') {
    throw new Error('The accepted isolated smoke.8 package metadata is not exact.');
  }

  const releasesName = `releases.${channel}.json`;
  const legacyName = `RELEASES-${channel}`;
  const releasesPath = path.join(packageRoot, releasesName);
  const legacyPath = path.join(packageRoot, legacyName);
  const releases = JSON.parse(await fsp.readFile(releasesPath, 'utf8'));
  const asset = releases.Assets?.find(item => item.Type === 'Full' && item.Version === metadata.version);
  if (!asset || path.basename(asset.FileName) !== asset.FileName) throw new Error('The accepted feed has no exact full package.');
  const packagePath = path.join(packageRoot, asset.FileName);
  const packageStat = await requireFile(packagePath, 'Accepted full package');
  const sourceHash = await sha256(packagePath);
  if (packageStat.size !== asset.Size || sourceHash !== String(asset.SHA256).toLowerCase()) {
    throw new Error('The accepted package does not match its feed size/SHA-256.');
  }

  const runId = `${new Date().toISOString().replace(/[-:.TZ]/g, '')}-${crypto.randomUUID().replaceAll('-', '')}`;
  const runRoot = requireInside(artifactRoot, path.join(artifactRoot, runId), 'Run root');
  const outputDirectory = requireInside(runRoot, path.join(runRoot, 'downloaded'), 'Download output');
  await fsp.mkdir(outputDirectory, { recursive: true });
  const runKeyBefore = runKeyFingerprint();

  const exactFiles = new Map([
    [`/${releasesName}`, { file: releasesPath, contentType: 'application/json' }],
    [`/${legacyName}`, { file: legacyPath, contentType: 'text/plain' }],
    [`/${asset.FileName}`, { file: packagePath, contentType: 'application/octet-stream', package: true }],
  ]);
  const requests = [];
  let mode = 'interrupted';

  const server = http.createServer(async (request, response) => {
    const entry = {
      mode,
      method: request.method,
      url: request.url,
      range: request.headers.range ?? null,
      status: 0,
      bytesSent: 0,
      connectionAborted: false,
    };
    requests.push(entry);
    try {
      const parsed = new URL(request.url, 'http://127.0.0.1');
      const allowed = isExactFeedQuery(parsed, releasesName) ? exactFiles.get(parsed.pathname) : null;
      if (!allowed || (request.method !== 'GET' && request.method !== 'HEAD')) {
        entry.status = 404;
        response.writeHead(404, { 'Content-Length': '0', 'Cache-Control': 'no-store' });
        response.end();
        return;
      }
      const stat = await fsp.stat(allowed.file);
      const range = allowed.package ? parseRange(request.headers.range, stat.size) : { start: 0, end: stat.size - 1, partial: false };
      if (!range) {
        entry.status = 416;
        response.writeHead(416, { 'Content-Range': `bytes */${stat.size}`, 'Content-Length': '0' });
        response.end();
        return;
      }
      const contentLength = range.end - range.start + 1;
      entry.status = range.partial ? 206 : 200;
      entry.rangeStart = range.start;
      entry.rangeEnd = range.end;
      entry.advertisedContentLength = contentLength;
      const headers = {
        'Content-Type': allowed.contentType,
        'Content-Length': String(contentLength),
        'Cache-Control': 'no-store',
      };
      if (allowed.package) headers['Accept-Ranges'] = 'bytes';
      if (range.partial) headers['Content-Range'] = `bytes ${range.start}-${range.end}/${stat.size}`;
      response.writeHead(entry.status, headers);
      if (request.method === 'HEAD') {
        response.end();
        return;
      }

      if (allowed.package && mode === 'interrupted') {
        const bytesToSend = Math.min(abortAfterBytes, Math.max(1, contentLength - 1));
        const input = fs.createReadStream(allowed.file, { start: range.start, end: range.start + bytesToSend - 1 });
        input.on('data', chunk => { entry.bytesSent += chunk.length; });
        input.on('error', () => response.destroy());
        input.on('end', () => {
          entry.connectionAborted = true;
          response.socket?.destroy();
        });
        input.pipe(response, { end: false });
        return;
      }

      const input = fs.createReadStream(allowed.file, { start: range.start, end: range.end });
      input.on('data', chunk => { entry.bytesSent += chunk.length; });
      input.on('error', error => response.destroy(error));
      input.pipe(response);
    } catch (error) {
      entry.serverError = error.message;
      if (!response.headersSent) response.writeHead(500, { 'Content-Length': '0' });
      response.end();
    }
  });

  const listen = new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  await listen;
  const address = server.address();
  const baseUrl = `http://127.0.0.1:${address.port}/`;
  const evidencePath = path.join(runRoot, 'results.json');
  let evidence;
  try {
    const interruptedStart = requests.length;
    const interruptedProcess = await runVpkDownload(baseUrl, outputDirectory, path.join(runRoot, 'interrupted-vpk'));
    const interruptedRequests = requests.slice(interruptedStart);
    const interruptedFiles = await snapshotFiles(outputDirectory, true);
    const interruptedFinal = path.join(outputDirectory, asset.FileName);
    const interruptedFinalStat = await fsp.stat(interruptedFinal).catch(() => null);
    const interruptedFinalHash = interruptedFinalStat?.isFile() ? await sha256(interruptedFinal) : null;
    const partialAccepted = interruptedFinalStat?.size === packageStat.size && interruptedFinalHash === sourceHash;
    const bodyWasAborted = interruptedRequests.some(item => item.mode === 'interrupted' && item.url === `/${asset.FileName}` &&
      item.connectionAborted && item.advertisedContentLength === packageStat.size && item.bytesSent < item.advertisedContentLength);
    if (!bodyWasAborted || partialAccepted || interruptedFinalStat || interruptedProcess.timedOut ||
        interruptedProcess.spawnError || interruptedProcess.exitCode === 0) {
      throw new Error('Interrupted body must fail within the bound and leave no final package.');
    }

    mode = 'healthy';
    const recoveryStart = requests.length;
    const recoveryProcess = await runVpkDownload(baseUrl, outputDirectory, path.join(runRoot, 'recovery-vpk'));
    const recoveryRequests = requests.slice(recoveryStart);
    const recoveredPath = path.join(outputDirectory, asset.FileName);
    const recoveredStat = await requireFile(recoveredPath, 'Recovered full package');
    const recoveredHash = await sha256(recoveredPath);
    if (recoveryProcess.timedOut || recoveryProcess.exitCode !== 0 || recoveredStat.size !== packageStat.size || recoveredHash !== sourceHash) {
      throw new Error('Healthy retry did not recover the exact accepted package.');
    }
    const runKeyAfter = runKeyFingerprint();
    if (runKeyAfter !== runKeyBefore) throw new Error('Production Run key changed during download-only recovery smoke.');

    evidence = {
      schemaVersion: 1,
      outcome: 'passed',
      scope: 'loopback HTTP adversarial transport fixture; production HTTPS trust is not asserted',
      appId: metadata.applicationId,
      version: metadata.version,
      channel,
      source: { fileName: asset.FileName, size: packageStat.size, sha256: sourceHash },
      server: {
        host: '127.0.0.1',
        port: address.port,
        allowedPaths: [...exactFiles.keys()],
        acceptedFeedQuery: 'arch=x64&os=win&rid=win-x64',
      },
      sameOutputDirectory: true,
      outputDirectory,
      interrupted: {
        process: interruptedProcess,
        bodyWasAborted,
        acceptedExactPackage: partialAccepted,
        finalFileSize: interruptedFinalStat?.size ?? null,
        finalFileSha256: interruptedFinalHash,
        files: interruptedFiles,
        requests: interruptedRequests,
      },
      recovery: {
        process: recoveryProcess,
        exactSize: recoveredStat.size === packageStat.size,
        exactSha256: recoveredHash === sourceHash,
        finalFileSize: recoveredStat.size,
        finalFileSha256: recoveredHash,
        files: await snapshotFiles(outputDirectory),
        requests: recoveryRequests,
      },
      setupOrApplyExecuted: false,
      writesConfinedToArtifactRoot: true,
      productionRunKeyUnchanged: true,
      productionRunKeyFingerprint: runKeyAfter,
    };
    await fsp.writeFile(evidencePath, JSON.stringify(evidence, null, 2), 'utf8');
    process.stdout.write(`PASS: interrupted HTTP package body was not accepted; healthy retry recovered exact SHA-256. Evidence: ${evidencePath}\n`);
  } catch (error) {
    evidence = {
      schemaVersion: 1,
      outcome: 'failed',
      error: error.stack ?? String(error),
      requests,
      setupOrApplyExecuted: false,
      writesConfinedToArtifactRoot: true,
    };
    await fsp.writeFile(evidencePath, JSON.stringify(evidence, null, 2), 'utf8');
    throw error;
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
}

main().catch(error => {
  process.stderr.write(`${error.stack ?? error}\n`);
  process.exitCode = 1;
});
