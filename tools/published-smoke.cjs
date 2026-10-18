// Loopback-only published-app verification. No Docker, database, or credentials.
// Usage: node tools/published-smoke.cjs <dotnet-executable> <publish-directory>
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { setTimeout: delay } = require('node:timers/promises');

async function listen(server) {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return server.address().port;
}

function request(port, url, data) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, path: url,
      method: data === undefined ? 'GET' : 'POST', timeout: 10000,
      headers: data === undefined ? {} : { 'Content-Type': 'application/x-www-form-urlencoded' } }, res => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', chunk => { body += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('Loopback request timed out')));
    req.end(data);
  });
}

async function main() {
  assert.equal(process.argv.length, 4, 'Supply dotnet executable and publish directory');
  const dotnet = path.resolve(process.argv[2]);
  const publish = path.resolve(process.argv[3]);
  assert(fs.existsSync(path.join(publish, 'LicensePlatform.Web.dll')), 'Missing published application');
  assert(fs.existsSync(path.join(publish, 'LicensePlatform.Web.staticwebassets.endpoints.json')), 'Missing static asset manifest');
  const forbidden = fs.readdirSync(publish, { recursive: true }).filter(name =>
    /BuildHost|(^|[\\/])Microsoft\.(Build|CodeAnalysis)/.test(name));
  assert.equal(forbidden.length, 0, 'Unexpected design-time build components in publish');
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'license-platform-smoke-'));
  const database = net.createServer(socket => socket.destroy());
  const databasePort = await listen(database);
  const reservation = net.createServer();
  const port = await listen(reservation);
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(dotnet, [path.join(publish, 'LicensePlatform.Web.dll')], {
    cwd: publish, stdio: ['ignore', 'pipe', 'pipe'], env: {
      ...process.env,
      COMPlus_DefaultStackSize: '400000',
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
      ConnectionStrings__Database: `Host=127.0.0.1;Port=${databasePort};Database=license_platform_smoke;Username=unused;Timeout=1;Command Timeout=1;Pooling=false`,
      Security__AllowInsecureHttp: 'true',
      Security__HstsEnabled: 'false',
      Security__BootstrapAdminPasswordFile: '',
      Security__DataProtectionPath: path.join(scratch, 'keyring'),
    },
  });
  let spawnError;
  let diagnostic = '';
  child.stdout.on('data', chunk => { diagnostic = (diagnostic + chunk).slice(-16384); });
  child.stderr.on('data', chunk => { diagnostic = (diagnostic + chunk).slice(-16384); });
  child.on('error', error => { spawnError = error; });
  const exited = once(child, 'exit');
  try {
    let login;
    for (let attempt = 0; attempt < 100; attempt++) {
      const exceptionType = diagnostic.match(/Unhandled exception\. ([A-Za-z0-9_.]+)/)?.[1] || '';
      assert(!spawnError && child.exitCode === null,
        `Published process exited before accepting requests (code ${child.exitCode}; ${exceptionType})`);
      try {
        login = await request(port, '/Account/Login');
        break;
      } catch {
        await delay(200);
      }
    }
    assert(login, 'Published app did not become reachable');
    assert.equal(login.status, 200, 'Login page failed');
    assert(login.body.includes('当前为公网明文 HTTP 模式'), 'Missing persistent HTTP warning');
    assert.equal(login.headers['strict-transport-security'], undefined, 'HTTP must not send HSTS');
    assert.equal(login.headers['x-content-type-options'], 'nosniff', 'Missing security headers');
    console.log('PASS published login, HTTP warning, security headers, no HTTP HSTS');

    for (const asset of ['/css/site.css', '/js/site.js', '/LicensePlatform.Web.styles.css', '/lib/bootstrap/dist/css/bootstrap.min.css']) {
      const response = await request(port, asset);
      assert.equal(response.status, 200, 'Static asset failed: ' + asset);
      assert(response.body.length > 0, 'Empty static asset: ' + asset);
    }
    console.log('PASS published CSS, JS, scoped CSS and Bootstrap endpoints');

    const admin = await request(port, '/Admin/Dashboard');
    assert.equal(admin.status, 302, 'Administration must require login');
    assert(admin.headers.location.includes('/Account/Login'), 'Invalid login redirect');
    const csrf = await request(port, '/Account/Login', 'Input.UserName=test&Input.Password=');
    assert.equal(csrf.status, 400, 'Missing antiforgery token must be rejected');
    console.log('PASS anonymous administration rejection and login antiforgery');

    const health = await request(port, '/health');
    assert.equal(health.status, 503, 'Unavailable database must not report healthy');
    assert.deepEqual(JSON.parse(health.body), { status: 'unhealthy' });
    assert(health.headers['cache-control'].includes('no-store'), 'Health must not be cached');
    console.log('PASS exact generic /health 503 with unavailable DB (healthy DB path not tested)');
  } finally {
    if (child.exitCode === null) child.kill();
    await Promise.race([exited, delay(10000, undefined, { ref: false }).then(() => child.kill('SIGKILL'))]);
    await new Promise(resolve => database.close(resolve));
    fs.rmSync(scratch, { recursive: true });
  }
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
