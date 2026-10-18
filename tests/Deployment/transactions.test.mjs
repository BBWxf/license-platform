// Non-Docker deployment transaction fixtures. No Docker executable is invoked.
// Run: node --test tests/Deployment/transactions.test.mjs
// On Windows set LP_TEST_BASH to the full path of Git Bash's bash.exe if needed.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const deploy = path.join(root, 'deploy');
const bash = process.env.LP_TEST_BASH ?? (process.platform === 'win32'
  ? 'C:/Program Files/Git/bin/bash.exe' : '/bin/bash');
const shellPath = p => p.replaceAll('\\', '/').replace(/^([A-Za-z]):/, (_, drive) => `/${drive.toLowerCase()}`);
const read = p => fs.readFileSync(p, 'utf8');
const write = (p, contents) => {
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, contents, { mode: 0o700 });
};
const stamp = '20261018T120000.123456789Z';
const upgradeStamp = '20261017T120000.123456789Z';

// These functions replace all deployment/system entry points. Filesystem helpers
// can touch only the disposable fixture tree; no root chown/systemctl/flock or
// real Docker command is reachable. Ownership checks remain a Linux-host gap.
const stubs = '\n' + read(path.join(root, 'tests/Deployment/transaction-stubs.sh'));
const health = `#!/usr/bin/env bash\nset -eu\nprintf 'health\\n' >>"$FIXTURE_ROOT/actions"\n` +
  `if [[ "\${FAIL_HEALTH_RECOVERY:-0}" == 1 ]] && command grep -q 'APP_IMAGE=.*rollback:' "$FIXTURE_ROOT/config/deployment.env"; then exit 34; fi\n`;
const backup = '#!/usr/bin/env bash\nset -eu\nprintf "backup\\n" >>"$FIXTURE_ROOT/actions"\n';

class Fixture {
  constructor(t) {
    this.root = fs.mkdtempSync(path.join(os.tmpdir(), 'lp-deploy-'));
    this.p = shellPath(this.root);
    this.home = path.join(this.root, 'home');
    this.config = path.join(this.root, 'config');
    this.data = path.join(this.root, 'data');
    this.release = path.join(this.root, 'release');
    this.state = path.join(this.data, 'rollback', upgradeStamp);
    this.envFile = path.join(this.config, 'deployment.env');
    this.environment = {
      ...process.env,
      FIXTURE_ROOT: this.p,
      FIXTURE_FAKE_LINKS: process.platform === 'win32' ? '1' : '0',
      LICENSE_PLATFORM_HOME: `${this.p}/home`,
      LICENSE_PLATFORM_CONFIG_DIR: `${this.p}/config`,
      LICENSE_PLATFORM_DATA_DIR: `${this.p}/data`,
      LICENSE_PLATFORM_BACKUP_DIR: `${this.p}/backups`,
    };
    t.after(() => fs.rmSync(this.root, { recursive: true, force: true }));
    for (const dir of ['home/bin', 'home/lib', 'home/docs', 'home/source', 'config/secrets',
      'data/rollback', 'backups', 'systemd', 'sbin', 'release/deploy/lib', 'release/deploy/systemd']) {
      fs.mkdirSync(path.join(this.root, dir), { recursive: true });
    }
    for (const name of ['docker', 'systemctl', 'chown', 'flock', 'sync', 'sudo', 'ip', 'ss', 'curl', 'getent']) {
      write(path.join(this.root, 'blocked', name), '#!/usr/bin/env bash\nprintf "Host command blocked by fixture\\n" >&2\nexit 97\n');
    }
    this.env = `INSTALL_DIR=${this.p}/home\nCONFIG_DIR=${this.p}/config\nDATA_DIR=${this.p}/data\n` +
      `BACKUP_DIR=${this.p}/backups\nSOURCE_DIR=${this.p}/home/source\nPUBLIC_IPV4=8.8.8.8\n` +
      'APP_IMAGE=example/web:current\nAPP_SOURCE_MODE=local\nHTTPS_ENABLED=false\n' +
      'ALLOW_INSECURE_HTTP=true\nHSTS_ENABLED=false\nCADDY_SITE_ADDRESS=http://8.8.8.8\n' +
      'POSTGRES_IMAGE=postgres:18.4-bookworm\nCADDY_IMAGE=caddy:2.10.2-alpine\nSECRETS_GID=20001\n' +
      'APP_MEMORY_LIMIT=777m\nPOSTGRES_DB=license_platform\nPOSTGRES_USER=license_platform\n' +
      'CUSTOM_KEEP=retained\n';
    write(this.envFile, this.env);
    write(path.join(this.home, 'source/current.txt'), 'current source\n');
    write(path.join(this.home, `source.before-${upgradeStamp.toLowerCase()}/old.txt`), 'old source\n');
    write(path.join(this.home, 'docs/old.txt'), 'old docs\n');
    write(path.join(this.home, 'compose.yml'), 'old compose\n');
    write(path.join(this.home, 'lib/common.sh'), 'old managed library\n');
    write(path.join(this.home, 'licensectl'), 'old managed entry\n');
    write(path.join(this.config, 'Caddyfile'), 'old operator Caddyfile\n');
    write(path.join(this.home, 'bin/backup.sh'), backup);
    write(path.join(this.home, 'bin/health-check.sh'), health);
    write(path.join(this.root, 'systemd/license-platform-backup.service'), 'old service\n');
    write(path.join(this.root, 'systemd/license-platform-backup.timer'), 'old timer\n');
    write(path.join(this.root, 'timer.enabled'), 'disabled\n');
    write(path.join(this.root, 'timer.active'), 'active\n');
    for (const secret of ['postgres_password', 'card_lookup_key', 'master_encryption_key', 'admin_initial_password']) {
      write(path.join(this.config, 'secrets', secret), 'fixture secret never printed\n');
    }
    write(path.join(this.state, 'state.env'), this.upgradeState());
    for (const name of fs.readdirSync(deploy)) {
      if (fs.statSync(path.join(deploy, name)).isFile()) {
        write(path.join(this.release, 'deploy', name), read(path.join(deploy, name)));
      }
    }
    for (const name of ['license-platform-backup.service', 'license-platform-backup.timer']) {
      write(path.join(this.release, 'deploy/systemd', name), read(path.join(deploy, 'systemd', name)));
    }
    write(path.join(this.release, 'deploy/lib/common.sh'), read(path.join(deploy, 'lib/common.sh')) + stubs);
    write(path.join(this.release, 'deploy/health-check.sh'), health);
    write(path.join(this.release, 'deploy/backup.sh'), backup);
    write(path.join(this.release, 'docs/new.txt'), 'new docs\n');
    write(path.join(this.release, 'Dockerfile'), 'fixture, not built\n');
    write(path.join(this.release, 'src/LicensePlatform.Web/LicensePlatform.Web.csproj'), '<Project/>\n');
    write(path.join(this.release, 'src/LicensePlatform.Web/contract.cs'),
      '// --migrate --admin-reset /health AllowInsecureHttp MasterEncryptionKeyFile CardLookupKeyFile\n');
    let installer = read(path.join(deploy, 'install.sh'));
    if (process.platform === 'win32') {
      installer = installer.replaceAll('-L "${DEFAULT_SBIN_LINK}"', '-f "${DEFAULT_SBIN_LINK}.fixture-link"');
    }
    for (const [name, value] of Object.entries({
      DEFAULT_INSTALL_DIR: `${this.p}/home`, DEFAULT_CONFIG_DIR: `${this.p}/config`,
      DEFAULT_DATA_DIR: `${this.p}/data`, DEFAULT_BACKUP_DIR: `${this.p}/backups`,
      DEFAULT_SYSTEMD_DIR: `${this.p}/systemd`, DEFAULT_SBIN_LINK: `${this.p}/sbin/licensectl`,
    })) {
      installer = installer.replace(new RegExp(`readonly ${name}="[^"]+"`), `readonly ${name}="${value}"`);
    }
    // Network preflight is covered separately in networking.test.mjs. Here only
    // the selected candidate is injected; transaction logic is never replaced.
    // Unexpected host commands fail closed through stubs or blocked executables.
    for (const name of ['check_os', 'check_resources', 'check_docker', 'check_systemd',
      'check_install_ports']) {
      assert(installer.includes(`\n${name}\n`), `missing preflight ${name}`);
      installer = installer.replace(`\n${name}\n`, '\n:\n');
    }
    assert(installer.includes('\nselect_install_bind_ipv4\n'));
    installer = installer.replace('\nselect_install_bind_ipv4\n',
      '\nBIND_IPV4_VALUE="${BIND_IPV4_CLI:-${SAVED_BIND_IPV4:-${PUBLIC_IPV4}}}"\n');
    write(path.join(this.release, 'deploy/install.sh'), installer);
  }
  upgradeState(status = 'SUCCEEDED') {
    return `STATE_VERSION=2\nSTATUS=${status}\nROLLBACK_IMAGE=license-platform-rollback:old\n` +
      `PRE_UPGRADE_BACKUP=${this.p}/backups/pre-upgrade.tar.gz\n` +
      `PREVIOUS_SOURCE_MODE=local\nTARGET_SOURCE_MODE=local\nSOURCE_DIR=${this.p}/home/source\n` +
      `PREVIOUS_SOURCE_PRESENT=true\nPREVIOUS_SOURCE_BACKUP=${this.p}/home/source.before-${upgradeStamp.toLowerCase()}\n`;
  }
  run(script, args, extra = {}) {
    return spawnSync(bash, ['--noprofile', '--norc', shellPath(path.join(this.release, 'deploy', script)), ...args], {
      cwd: this.root, env: { ...this.environment, ...extra }, encoding: 'utf8', timeout: 20000,
    });
  }
  rollback(extra = {}) { return this.run('rollback.sh', ['--confirm-rollback', 'ROLLBACK-APPLICATION'], extra); }
  install(extra = {}, local = false, bind = null) {
    const args = ['--mode', 'http-ip', '--public-ip', '8.8.8.8', '--accept-insecure-http'];
    if (bind !== null) args.push('--bind-ip', bind);
    if (!local) args.push('--app-image', 'example/web:new');
    return this.run('install.sh', args, extra);
  }
  output(result) { return `${result.error ?? ''}\n${result.stdout}\n${result.stderr}`; }
  ok(result) { assert.equal(result.status, 0, this.output(result)); }
  failed(result, status = 1) { assert.equal(result.status, status, this.output(result)); }
  actions() { return fs.existsSync(path.join(this.root, 'actions')) ? read(path.join(this.root, 'actions')) : ''; }
  pending(name) { return fs.existsSync(path.join(this.data, name)); }
  restoredSource() {
    assert.equal(read(path.join(this.home, 'source/current.txt')), 'current source\n');
    assert.equal(read(path.join(this.home, `source.before-${upgradeStamp.toLowerCase()}/old.txt`)), 'old source\n');
  }
  restoredInstall() {
    assert.equal(read(path.join(this.home, 'compose.yml')), 'old compose\n');
    assert.equal(read(path.join(this.home, 'lib/common.sh')), 'old managed library\n');
    assert.equal(read(path.join(this.home, 'docs/old.txt')), 'old docs\n');
    assert(!fs.existsSync(path.join(this.home, 'docs/new.txt')));
    assert.equal(read(path.join(this.config, 'Caddyfile')), 'old operator Caddyfile\n');
    assert.equal(read(path.join(this.root, 'systemd/license-platform-backup.timer')), 'old timer\n');
    assert.equal(read(path.join(this.root, 'systemd/license-platform-backup.service')), 'old service\n');
    assert.equal(read(path.join(this.root, 'timer.enabled')), 'disabled\n');
    assert.equal(read(path.join(this.root, 'timer.active')), 'active\n');
    assert.match(read(this.envFile), /APP_MEMORY_LIMIT=777m/);
    assert.match(read(this.envFile), /CUSTOM_KEEP=retained/);
    for (const [key, service] of [['APP_IMAGE', 'app'], ['POSTGRES_IMAGE', 'db'], ['CADDY_IMAGE', 'caddy']]) {
      assert.match(read(this.envFile), new RegExp(`${key}=license-platform-install-${service}-rollback:${stamp.toLowerCase()}`));
    }
  }
}

test('latest chooses a successful application state, not HTTPS/HSTS/journals/unfinished upgrades', t => {
  const f = new Fixture(t);
  for (const name of ['https-20990101T000000Z', 'hsts-20990101T000000Z', 'manual-20990101T000000Z', 'install-20990101T000000Z']) {
    write(path.join(f.data, 'rollback', name, 'state.env'), 'STATUS=SUCCEEDED\n');
  }
  write(path.join(f.data, 'rollback/20990101T000000.123456789Z/state.env'), f.upgradeState('PREPARED'));
  write(path.join(f.data, 'rollback/20990102T000000.123456789Z/state.env'), f.upgradeState('MANUALLY_ROLLED_BACK'));
  f.ok(f.rollback());
  assert.match(read(f.envFile), /APP_IMAGE=license-platform-rollback:old/);
  assert.match(read(path.join(f.state, 'state.env')), /STATUS=MANUALLY_ROLLED_BACK/);
  assert(!f.pending('manual-rollback.pending'));
  assert.equal(read(path.join(f.home, 'source/old.txt')), 'old source\n');
});

test('latest orders whole-second and shorter fractional timestamps chronologically', t => {
  const f = new Fixture(t);
  for (const name of ['20261017T120000Z', '20261017T120000.12Z']) {
    write(path.join(f.data, 'rollback', name, 'state.env'),
      'STATE_VERSION=1\nROLLBACK_IMAGE=license-platform-rollback:older\nPRE_UPGRADE_BACKUP=\n');
  }
  f.ok(f.rollback());
  assert.match(read(f.envFile), /APP_IMAGE=license-platform-rollback:old\n/);
  assert.match(read(path.join(f.state, 'state.env')), /STATUS=MANUALLY_ROLLED_BACK/);
  assert(!read(path.join(f.data, 'rollback/20261017T120000Z/state.env')).includes('MANUALLY_ROLLED_BACK'));
});

for (const point of ['stop', 'source-to-safety', 'old-to-source', 'env-switch', 'commit']) {
  test(`manual rollback durable recovery after interruption at ${point}`, t => {
    const f = new Fixture(t);
    const interrupted = f.rollback({ CRASH_POINT: point });
    assert.notEqual(interrupted.status, 0, f.output(interrupted));
    assert(f.pending('manual-rollback.pending'), f.output(interrupted));
    f.failed(f.rollback()); // Recovery stops, requiring a fresh explicit retry.
    assert(!f.pending('manual-rollback.pending'));
    f.restoredSource();
    assert.match(read(f.envFile), /APP_IMAGE=license-platform-manual-rollback:/);
    assert.match(read(path.join(f.state, 'state.env')), /STATUS=SUCCEEDED/);
  });
}

test('legacy version 1 image-only rollback remains selectable', t => {
  const f = new Fixture(t);
  write(path.join(f.state, 'state.env'), 'STATE_VERSION=1\nROLLBACK_IMAGE=license-platform-rollback:old\nPRE_UPGRADE_BACKUP=\n');
  f.ok(f.rollback());
  assert.match(read(f.envFile), /APP_SOURCE_MODE=image/);
  assert(fs.existsSync(path.join(f.home, 'source/current.txt')));
});

test('manual rollback recovery handles an image baseline without an old source copy', t => {
  const f = new Fixture(t);
  fs.rmSync(path.join(f.home, `source.before-${upgradeStamp.toLowerCase()}`), { recursive: true });
  write(path.join(f.state, 'state.env'), f.upgradeState().replace('PREVIOUS_SOURCE_MODE=local', 'PREVIOUS_SOURCE_MODE=image')
    .replace('PREVIOUS_SOURCE_PRESENT=true', 'PREVIOUS_SOURCE_PRESENT=false'));
  const interrupted = f.rollback({ CRASH_POINT: 'env-switch' });
  assert.notEqual(interrupted.status, 0, f.output(interrupted));
  assert(f.pending('manual-rollback.pending'));
  f.failed(f.rollback());
  assert(!f.pending('manual-rollback.pending'));
  assert.equal(read(path.join(f.home, 'source/current.txt')), 'current source\n');
  assert(!fs.existsSync(path.join(f.home, `source.before-${upgradeStamp.toLowerCase()}`)));
});

test('manual rollback recovery is itself restartable between source renames', t => {
  const f = new Fixture(t);
  f.rollback({ CRASH_POINT: 'old-to-source' });
  f.rollback({ CRASH_POINT: 'recovery-source-to-backup' });
  assert(f.pending('manual-rollback.pending'));
  f.failed(f.rollback());
  f.restoredSource();
  assert(!f.pending('manual-rollback.pending'));
});

test('ordinary manual rollback failure restores baseline; failed recovery preserves marker', t => {
  const f = new Fixture(t);
  f.failed(f.rollback({ FAIL_SWITCH: '1', FAIL_HEALTH_RECOVERY: '1' }), 2);
  assert(f.pending('manual-rollback.pending'));
  f.failed(f.rollback());
  assert(!f.pending('manual-rollback.pending'));
  f.restoredSource();
});

test('recovery command failures cannot be hidden by conditional errexit suppression', t => {
  const f = new Fixture(t);
  f.rollback({ CRASH_POINT: 'old-to-source' });
  f.failed(f.rollback({ FAIL_RECOVERY: '1' }));
  assert(f.pending('manual-rollback.pending'));
  f.failed(f.rollback());
  f.restoredSource();
  assert(!f.pending('manual-rollback.pending'));
});

test('manual recovery rejects traversal marker without touching source or environment', t => {
  const f = new Fixture(t);
  write(path.join(f.data, 'manual-rollback.pending'), `${f.p}/data/rollback/manual-${stamp}/../${upgradeStamp}\n`);
  f.failed(f.rollback());
  assert.equal(read(f.envFile), f.env);
  f.restoredSource();
  assert.equal(f.actions(), '');
});

for (const failure of ['FAIL_MIGRATION', 'FAIL_TIMER_ENABLE']) {
  test(`installer rerun restores managed files/env/images/timer after ${failure}`, t => {
    const f = new Fixture(t);
    f.failed(f.install({ [failure]: '1' }), failure === 'FAIL_TIMER_ENABLE' ? 33 : 1);
    assert(!f.pending('install.pending'));
    f.restoredInstall();
    assert(fs.existsSync(path.join(f.home, 'source/current.txt')));
    assert(!fs.existsSync(path.join(f.root, 'sbin/licensectl')));
  });
}

for (const point of ['managed-files', 'install-old-to-backup', 'install-stage-to-source', 'commit']) {
  test(`local installer rerun recovers durably after ${point}`, t => {
    const f = new Fixture(t);
    const interrupted = f.install({ CRASH_POINT: point }, true);
    assert.notEqual(interrupted.status, 0, f.output(interrupted));
    assert(f.pending('install.pending'), f.output(interrupted));
    f.failed(f.install());
    assert(!f.pending('install.pending'));
    f.restoredInstall();
    assert.equal(read(path.join(f.home, 'source/current.txt')), 'current source\n');
    assert(!fs.existsSync(path.join(f.home, 'source/LicensePlatform.Web')));
  });
}

test('installer recovery failure preserves journal and succeeds on a later rerun', t => {
  const f = new Fixture(t);
  f.failed(f.install({ FAIL_MIGRATION: '1', FAIL_RECOVERY: '1' }), 2);
  assert(f.pending('install.pending'));
  f.failed(f.install());
  assert(!f.pending('install.pending'));
  f.restoredInstall();
});

test('successful installer rerun preserves tuning/secrets and commits its journal', t => {
  const f = new Fixture(t);
  f.ok(f.install());
  assert(!f.pending('install.pending'));
  assert.match(read(f.envFile), /APP_IMAGE=example\/web:new/);
  assert.match(read(f.envFile), /APP_MEMORY_LIMIT=777m/);
  assert.match(read(f.envFile), /CUSTOM_KEEP=retained/);
  assert.equal(read(path.join(f.config, 'secrets/master_encryption_key')), 'fixture secret never printed\n');
  assert.equal(read(path.join(f.root, 'timer.enabled')), 'enabled\n');
  assert.equal(read(path.join(f.root, 'timer.active')), 'active\n');
});

test('initial environment creation persists a private binding with the public identity', t => {
  const f = new Fixture(t);
  fs.unlinkSync(f.envFile); // Exercise the first-write path, with no prior env or journal.
  f.ok(f.install({}, false, '10.42.0.10'));
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
  assert.match(read(f.envFile), /^PUBLIC_IPV4=8\.8\.8\.8$/m);
  assert.match(read(f.envFile), /^CADDY_SITE_ADDRESS=http:\/\/8\.8\.8\.8$/m);
  assert(!f.pending('install.pending'));
});

test('installer migrates legacy binding using its saved public IP, not an inherited bind', t => {
  const f = new Fixture(t);
  f.ok(f.install({ BIND_IPV4: '10.42.0.99' }));
  assert.match(read(f.envFile), /^BIND_IPV4=8\.8\.8\.8$/m);
});

test('NAT installer rerun preserves the saved private binding and public identity', t => {
  const f = new Fixture(t);
  write(f.envFile, f.env + 'BIND_IPV4=10.42.0.10\n');
  f.ok(f.install());
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
  assert.match(read(f.envFile), /^PUBLIC_IPV4=8\.8\.8\.8$/m);
  assert.match(read(f.envFile), /^CADDY_SITE_ADDRESS=http:\/\/8\.8\.8\.8$/m);
});

test('explicit binding is committed without altering the public identity or secrets', t => {
  const f = new Fixture(t);
  write(f.envFile, f.env + 'BIND_IPV4=10.42.0.10\n');
  f.ok(f.install({}, false, '192.168.50.20'));
  assert.match(read(f.envFile), /^BIND_IPV4=192\.168\.50\.20$/m);
  assert.match(read(f.envFile), /^PUBLIC_IPV4=8\.8\.8\.8$/m);
  assert.equal(read(path.join(f.config, 'secrets/master_encryption_key')), 'fixture secret never printed\n');
});

test('failed explicit binding change restores the previous NAT binding', t => {
  const f = new Fixture(t);
  write(f.envFile, f.env + 'BIND_IPV4=10.42.0.10\n');
  f.failed(f.install({ FAIL_MIGRATION: '1' }, false, '192.168.50.20'));
  f.restoredInstall();
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
  assert.doesNotMatch(read(f.envFile), /192\.168\.50\.20/);
});

test('interrupted binding change reloads its saved NAT baseline before any new installation', t => {
  const f = new Fixture(t);
  write(f.envFile, f.env + 'BIND_IPV4=10.42.0.10\n');
  const interrupted = f.install({ CRASH_POINT: 'commit' }, true, '192.168.50.20');
  assert.notEqual(interrupted.status, 0, f.output(interrupted));
  assert(f.pending('install.pending'));
  f.failed(f.install({ BIND_IPV4: '10.42.0.99' }, false, '192.168.50.20'));
  assert(!f.pending('install.pending'));
  f.restoredInstall();
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
});

test('application rollback and its interrupted recovery retain the NAT binding', t => {
  const f = new Fixture(t);
  write(f.envFile, f.env + 'BIND_IPV4=10.42.0.10\n');
  const interrupted = f.rollback({ CRASH_POINT: 'env-switch' });
  assert.notEqual(interrupted.status, 0, f.output(interrupted));
  f.failed(f.rollback({ BIND_IPV4: '192.168.50.20' }));
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
  f.ok(f.rollback({ FIXTURE_TIMESTAMP: '20261018T120001.123456789Z' }));
  assert.match(read(f.envFile), /^BIND_IPV4=10\.42\.0\.10$/m);
});

test('all pending marker guards block cross-command mutations and exempt only matching recovery', t => {
  const f = new Fixture(t);
  const common = shellPath(path.join(f.release, 'deploy/lib/common.sh'));
  const names = ['upgrade.pending', 'manual-rollback.pending', 'install.pending', 'https-switch.pending', 'hsts-enable.pending'];
  for (const marker of names) {
    write(path.join(f.data, marker), 'fixture marker\n');
    for (const exempt of ['', ...names]) {
      const result = spawnSync(bash, ['--noprofile', '--norc', '-c',
        'source "$1"; reject_pending_transactions "$2"', '_', common, exempt], {
        cwd: f.root, env: f.environment, encoding: 'utf8', timeout: 10000,
      });
      assert.equal(result.status, exempt === marker ? 0 : 1, `${marker}/${exempt}: ${f.output(result)}`);
    }
    fs.unlinkSync(path.join(f.data, marker));
  }
});
