// NON-DOCKER networking regressions. No host ip/ss/curl, root operations,
// systemd, Compose execution, listeners, or external requests are used.
// Run: node --test tests/Deployment/networking.test.mjs
// Windows: LP_TEST_BASH may point to Git Bash's bash.exe.
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
const common = read(path.join(deploy, 'lib/common.sh'));
const stubs = read(path.join(root, 'tests/Deployment/networking-stubs.sh'));
const publicIp = '198.51.100.20'; // Synthetic documentation address; never contacted.
const privateIp = '10.42.0.10';
const otherIp = '192.168.50.20';
const healthyBody = '{"status":"healthy"}';
const address = (iface, ip, index = 2) =>
  `${index}: ${iface}    inet ${ip}/24 brd 10.42.0.255 scope global ${iface}\n`;
const defaultRoute = 'default via 10.42.0.1 dev eth0 proto dhcp metric 100\n';
const routeGet = `${publicIp} via 10.42.0.1 dev eth0 src ${privateIp} uid 1000\n`;
const blockedCommands = ['ip', 'ss', 'curl', 'docker', 'compose', 'systemctl', 'sudo',
  'chown', 'chmod', 'install', 'flock', 'iptables', 'nft', 'ufw', 'apt-get', 'yum',
  'dnf', 'service', 'reboot', 'shutdown'];
const restoreSource = read(path.join(deploy, 'restore.sh'));
const licensectlSource = read(path.join(deploy, 'licensectl'));
const hstsHeaders = 'HTTP/1.1 200 OK\r\nStrict-Transport-Security: max-age=2592000\r\n\r\n';

// These small production helpers have a same-indentation closing brace and no
// heredocs/nested function definitions. Extract definitions only: never execute
// the restore/licensectl entry points or their service/database operations.
function extractFunction(source, name) {
  const lines = source.split(/\r?\n/);
  const starts = lines.map((line, index) => ({ match: /^(\s*)([a-zA-Z_][a-zA-Z0-9_]*)\(\) \{$/.exec(line), index }))
    .filter(({ match }) => match?.[2] === name);
  assert.equal(starts.length, 1, `Expected one extractable production function ${name}`);
  const { match, index } = starts[0];
  const end = lines.findIndex((line, position) => position > index && line === `${match[1]}}`);
  assert(end > index, `Missing same-indentation closing brace for ${name}`);
  return lines.slice(index, end + 1).join('\n') + '\n';
}
const restoreFunctions = ['validate_restored_env', 'restored_env_value', 'prepare_restored_bind_ipv4']
  .map(name => extractFunction(restoreSource, name)).join('\n');
const hstsFunction = extractFunction(licensectlSource, 'probe_hsts');

class Fixture {
  constructor(t) {
    this.root = fs.mkdtempSync(path.join(os.tmpdir(), 'lp-network-'));
    this.p = shellPath(this.root);
    for (const dir of ['config', 'tmp', 'blocked']) fs.mkdirSync(path.join(this.root, dir));
    this.write('common.sh', common);
    this.write('stubs.sh', stubs);
    this.write('functions.sh', '');
    const blocked = '#!/usr/bin/env bash\n' +
      'printf "%s\\n" "$0 $*" >>"$FIXTURE_ROOT/unexpected-commands"\n' +
      'printf "External host command blocked by networking fixture\\n" >&2\nexit 97\n';
    for (const name of blockedCommands) this.write(`blocked/${name}`, blocked);
    this.environment = {
      ...process.env,
      BASH_ENV: '', ENV: '',
      FIXTURE_ROOT: this.p, FIXTURE_PUBLIC_IPV4: publicIp,
      FIXTURE_ALLOW_ENV_WRITE: '0', RESTORED_CONFIG: `${this.p}/config`,
      TMPDIR: `${this.p}/tmp`,
      LICENSE_PLATFORM_HOME: `${this.p}/home`,
      LICENSE_PLATFORM_CONFIG_DIR: `${this.p}/config`,
      LICENSE_PLATFORM_DATA_DIR: `${this.p}/data`,
      LICENSE_PLATFORM_BACKUP_DIR: `${this.p}/backups`,
      PUBLIC_IPV4: '', BIND_IPV4: '',
      IP_ADDRESS_EXIT: '0', IP_DEFAULT_EXIT: '0', IP_ROUTE_GET_EXIT: '0',
      SS_EXIT: '0', CURL_EXIT: '0', CURL_STATUS: '200',
      DOCKER_PS_EXIT: '0', DOCKER_INSPECT_EXIT: '0', DOCKER_PORT_EXIT: '0',
    };
    this.network();
    this.write('containers', '');
    this.write('container-project', 'unrelated-project\n');
    this.write('container-service', 'web\n');
    this.write('container-ports', '');
    this.write('listeners', '');
    this.write('curl-body', healthyBody);
    this.write('curl-headers', hstsHeaders);
    this.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\n`);
    t.after(() => {
      const unexpected = this.text('unexpected-commands');
      fs.rmSync(this.root, { recursive: true, force: true });
      assert.equal(unexpected, '', `Fixture attempted a blocked host operation:\n${unexpected}`);
    });
  }
  write(name, content) { fs.writeFileSync(path.join(this.root, name), content, { mode: 0o700 }); }
  text(name) { return fs.existsSync(path.join(this.root, name)) ? read(path.join(this.root, name)) : ''; }
  network({ addresses = address('eth0', privateIp), defaultRoute: defaults = defaultRoute,
    routeGet: route = routeGet } = {}) {
    for (const name of fs.readdirSync(this.root)) {
      if (name.startsWith('addresses.')) fs.rmSync(path.join(this.root, name));
    }
    this.write('addresses', addresses);
    const interfaces = new Map();
    for (const line of addresses.split('\n').filter(Boolean)) {
      const iface = /^\d+:\s+(\S+)/.exec(line)?.[1];
      assert(iface && /^[A-Za-z0-9_.:-]+$/.test(iface), `Bad synthetic interface line: ${line}`);
      interfaces.set(iface, (interfaces.get(iface) ?? '') + `${line}\n`);
    }
    for (const [iface, lines] of interfaces) this.write(`addresses.${iface}`, lines);
    this.write('default-route', defaults);
    this.write('route-get', route);
  }
  run(script, args = [], environment = {}) {
    // Windows process argv can normalize CR/LF. NUL-framed data preserves even
    // empty/whitespace-invalid addresses without evaluating them as shell code.
    this.write('arguments', args.map(arg => `${arg}\0`).join(''));
    return spawnSync(bash, ['--noprofile', '--norc', '-c',
      'export PATH="$FIXTURE_ROOT/blocked:$PATH"; source "$1"; source "$2"; source "$FIXTURE_ROOT/functions.sh"; ' +
      'mapfile -d "" -t fixture_arguments <"$FIXTURE_ROOT/arguments"; set -- "${fixture_arguments[@]}";\n' + script,
      '_', `${this.p}/common.sh`, `${this.p}/stubs.sh`], {
      cwd: this.root, env: { ...this.environment, ...environment }, encoding: 'utf8', timeout: 10000,
    });
  }
  call(name, args = [], environment = {}) {
    return this.run('declare -F "$1" >/dev/null || { printf "Missing production API: %s\\n" "$1" >&2; exit 96; }; "$@"',
      [name, ...args], environment);
  }
  output(result) { return `${result.error ?? ''}\n${result.stdout}\n${result.stderr}`; }
  ok(result) { assert.ifError(result.error); assert.equal(result.status, 0, this.output(result)); }
  failed(result) {
    assert.ifError(result.error);
    assert.equal(result.signal, null, this.output(result));
    assert.equal(typeof result.status, 'number', this.output(result));
    assert.notEqual(result.status, 0, this.output(result));
    for (const status of [95, 96, 97, 127]) assert.notEqual(result.status, status, this.output(result));
    assert.doesNotMatch(result.stderr, /command not found/, this.output(result));
  }
  calls(name) {
    return this.text(`${name}.args`).split('\0\0').filter(Boolean).map(call => call.split('\0'));
  }
  selected(args, expected = privateIp, environment = {}) {
    const result = this.call('select_bind_ipv4', args, environment);
    this.ok(result);
    assert.equal(result.stdout, `${expected}\n`, this.output(result));
    return result;
  }
  noRouteCalls() {
    assert(!this.calls('ip').some(args => args[2] === 'route'), 'Precedence must not fall through to route discovery');
  }
  noProbeFiles() { assert.deepEqual(fs.readdirSync(path.join(this.root, 'tmp')), []); }
}

const validBindAddresses = [privateIp, otherIp, publicIp, '172.16.4.5', '172.31.255.254',
  '100.64.0.2', '1.2.3.4', '223.255.255.254'];
const invalidBindAddresses = ['', '0.0.0.0', '0.1.2.3', '127.0.0.1', '127.42.0.10',
  '169.254.1.2', '224.0.0.1', '239.255.255.250', '240.0.0.1', '255.255.255.255',
  '256.1.2.3', '1.2.3.256', '-1.2.3.4', '1.2.3', '1.2.3.4.5', '01.2.3.4', '1.02.3.4',
  '1.2.3.004', '1.2.3.4/24', '198.51.100.20:80', '::1', '::ffff:10.42.0.10',
  'host.example.test', ' 10.42.0.10', '10.42.0.10 ', '10.42.0.10\n', '10.42.0.10\r', '1e2.2.3.4'];

test('is_usable_bind_ipv4 accepts canonical public/private/CGNAT unicast and rejects unsafe or malformed inputs', t => {
  const f = new Fixture(t);
  for (const ip of validBindAddresses) {
    const result = f.call('is_usable_bind_ipv4', [ip]);
    f.ok(result);
    assert.equal(result.stdout, '', `Pure validator printed output for ${JSON.stringify(ip)}`);
    assert.equal(result.stderr, '', f.output(result));
  }
  for (const ip of invalidBindAddresses) {
    const result = f.call('is_usable_bind_ipv4', [ip]);
    assert.notEqual(result.status, 0, `Accepted unsafe IPv4 ${JSON.stringify(ip)}`);
    f.failed(result);
    assert.equal(result.stdout, '', `Pure validator printed output for ${JSON.stringify(ip)}`);
    assert.equal(result.stderr, '', f.output(result));
  }
  for (const name of ['ip', 'ss', 'curl']) assert.deepEqual(f.calls(name), [], 'Validator must be pure');
});

test('is_local_ipv4 matches an exact assigned IPv4 and honors the optional interface', t => {
  const f = new Fixture(t);
  f.network({ addresses: address('eth0', privateIp) + address('eth1', otherIp, 3) });
  for (const args of [[privateIp], [privateIp, 'eth0'], [otherIp, 'eth1']]) {
    const result = f.call('is_local_ipv4', args);
    f.ok(result);
    assert.equal(result.stdout, '', f.output(result));
  }
  for (const args of [['10.42.0.1'], ['10.42.0.100'], [publicIp], [privateIp, 'eth1'], [privateIp, 'missing0']]) {
    f.failed(f.call('is_local_ipv4', args));
  }
  assert(f.calls('ip').some(args => args.join(' ') === '-o -4 address show dev eth1'));
});

test('require_local_bind_ipv4 rejects invalid/nonlocal binds and accepts the assigned private bind', t => {
  const f = new Fixture(t);
  f.ok(f.call('require_local_bind_ipv4', [privateIp]));
  for (const ip of ['0.0.0.0', '127.0.0.1', '256.0.0.1', publicIp]) {
    const result = f.call('require_local_bind_ipv4', [ip]);
    f.failed(result);
    assert.notEqual(result.stderr, '', 'Fatal wrapper must explain rejection on stderr');
    assert.equal(result.stdout, '', f.output(result));
  }
});

test('direct-public deployment chooses the locally assigned public address without consulting routes', t => {
  const f = new Fixture(t);
  f.network({ addresses: address('eth0', publicIp) + address('eth1', privateIp, 3), defaultRoute: '' });
  f.selected([publicIp, '', ''], publicIp);
  f.noRouteCalls();
});

test('NAT/EIP deployment chooses the unique private default-route source, verified on its interface', t => {
  const f = new Fixture(t);
  f.selected([publicIp, '', '']);
  const calls = f.calls('ip').map(args => args.join(' '));
  assert(calls.includes('-o -4 route show default'));
  assert(calls.includes(`-o -4 route get ${publicIp}`));
  assert(calls.includes('-o -4 address show dev eth0'), 'Route source must be assigned to the selected default interface');
});

test('a single on-link default route without a gateway remains unambiguous', t => {
  const f = new Fixture(t);
  f.network({ defaultRoute: 'default dev eth0 proto static scope link\n',
    routeGet: `${publicIp} dev eth0 src ${privateIp} uid 1000\n` });
  f.selected([publicIp, '', '']);
});

for (const [label, explicit, saved, expected] of [
  ['explicit overrides saved and locally assigned public', otherIp, privateIp, otherIp],
  ['explicit overrides an unusable saved value', otherIp, '0.0.0.0', otherIp],
  ['saved overrides locally assigned public and route discovery', '', otherIp, otherIp],
]) {
  test(label, t => {
    const f = new Fixture(t);
    f.network({ addresses: address('eth0', publicIp) + address('eth1', privateIp, 3) + address('eth2', otherIp, 4) });
    f.selected([publicIp, explicit, saved], expected);
    f.noRouteCalls();
  });
}

for (const [label, explicit, saved] of [
  ['explicit nonlocal bind cannot fall back to saved', '10.99.0.10', privateIp],
  ['explicit wildcard cannot fall back to saved', '0.0.0.0', privateIp],
  ['explicit malformed bind cannot fall back to public', '10.42.0.999', ''],
  ['nonlocal saved bind cannot drift to public', '', '10.99.0.10'],
  ['wildcard saved bind cannot drift to public', '', '0.0.0.0'],
  ['malformed saved bind cannot drift to public', '', '10.42.0.999'],
  ['saved hostname cannot drift to public', '', 'host.example.test'],
]) {
  test(label, t => {
    const f = new Fixture(t);
    f.network({ addresses: address('eth0', publicIp) + address('eth1', privateIp, 3) });
    const result = f.call('select_bind_ipv4', [publicIp, explicit, saved]);
    f.failed(result);
    assert.equal(result.stdout, '', f.output(result));
    f.noRouteCalls();
  });
}

const routeFailures = [
  ['missing default route', { defaultRoute: '' }],
  ['multiple default interfaces', { defaultRoute: defaultRoute + 'default via 192.168.50.1 dev eth1 metric 200\n' }],
  ['multiple defaults even on one interface', { defaultRoute: defaultRoute + 'default via 10.42.0.2 dev eth0 metric 200\n' }],
  ['multipath nexthops', { defaultRoute: 'default proto static\n\tnexthop via 10.42.0.1 dev eth0 weight 1\n\tnexthop via 192.168.50.1 dev eth1 weight 1\n' }],
  ['inline nexthop', { defaultRoute: 'default dev eth0 nexthop via 10.42.0.1 dev eth0 weight 1\n' }],
  ['default missing dev', { defaultRoute: 'default via 10.42.0.1 metric 100\n' }],
  ['default duplicate dev', { defaultRoute: 'default via 10.42.0.1 dev eth0 dev eth0\n' }],
  ['route-get missing src', { routeGet: `${publicIp} via 10.42.0.1 dev eth0\n` }],
  ['route-get missing dev', { routeGet: `${publicIp} via 10.42.0.1 src ${privateIp}\n` }],
  ['route-get uses a different interface', { routeGet: `${publicIp} dev eth1 src ${otherIp}\n` }],
  ['route source is not locally assigned', { routeGet: `${publicIp} dev eth0 src 10.99.0.10\n` }],
  ['route source belongs only to another interface', { routeGet: `${publicIp} dev eth0 src ${otherIp}\n` }],
  ['route source is malformed', { routeGet: `${publicIp} dev eth0 src 10.42.0.999\n` }],
  ['route source is a wildcard', { routeGet: `${publicIp} dev eth0 src 0.0.0.0\n` }],
  ['route source is loopback', { routeGet: `${publicIp} dev eth0 src 127.0.0.1\n` }],
  ['route-get duplicate src', { routeGet: `${publicIp} dev eth0 src ${privateIp} src ${privateIp}\n` }],
  ['route-get duplicate dev', { routeGet: `${publicIp} dev eth0 dev eth0 src ${privateIp}\n` }],
  ['route-get returns multiple lines', { routeGet: routeGet + routeGet }],
];
for (const [label, network] of routeFailures) {
  test(`NAT selection fails closed: ${label}`, t => {
    const f = new Fixture(t);
    f.network({ addresses: address('eth0', privateIp) + address('eth1', otherIp, 3), ...network });
    const result = f.call('select_bind_ipv4', [publicIp, '', '']);
    f.failed(result);
    assert.equal(result.stdout, '', f.output(result));
  });
}
for (const [label, environment] of [
  ['address enumeration fails', { IP_ADDRESS_EXIT: '2' }],
  ['default-route command fails', { IP_DEFAULT_EXIT: '2' }],
  ['route-get command fails', { IP_ROUTE_GET_EXIT: '2' }],
]) {
  test(`NAT selection fails closed when ${label}`, t => {
    const f = new Fixture(t);
    const result = f.call('select_bind_ipv4', [publicIp, '', ''], environment);
    f.failed(result);
    assert.equal(result.stdout, '', f.output(result));
  });
}

for (const [label, persisted, expected] of [
  ['legacy env ignores inherited BIND and falls back to saved PUBLIC', '', publicIp],
  ['new env uses its persisted private BIND instead of inherited BIND', `BIND_IPV4=${privateIp}\n`, privateIp],
]) {
  test(label, t => {
    const f = new Fixture(t);
    f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\n${persisted}`);
    const result = f.run('load_deployment_env; [[ "$(declare -p BIND_IPV4)" == "declare -x "* ]]; printf "%s|%s\\n" "$PUBLIC_IPV4" "$BIND_IPV4"',
      [], { BIND_IPV4: otherIp });
    f.ok(result);
    assert.equal(result.stdout, `${publicIp}|${expected}\n`, f.output(result));
    assert.deepEqual(f.calls('ip'), [], 'Loading old deployment state must not silently reselect a bind');
  });
}

test('a bad persisted BIND fails instead of using inherited BIND or legacy PUBLIC', t => {
  const f = new Fixture(t);
  for (const ip of ['0.0.0.0', '127.0.0.1', '10.42.0.999', 'host.example.test']) {
    f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${ip}\n`);
    f.failed(f.call('load_deployment_env', [], { BIND_IPV4: privateIp }));
  }
  assert.deepEqual(f.calls('ip'), [], 'Invalid saved state must not trigger automatic reselection');
});

test('reloading a legacy env clears a previously persisted private BIND in the same shell', t => {
  const f = new Fixture(t);
  f.write('config/new.env', `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${privateIp}\n`);
  const result = f.run('command cp "$FIXTURE_ROOT/config/new.env" "$FIXTURE_ROOT/config/deployment.env"; load_deployment_env; ' +
    'printf "%s\\n" "$BIND_IPV4"; printf "PUBLIC_IPV4=%s\\n" "$FIXTURE_PUBLIC_IPV4" >"$FIXTURE_ROOT/config/deployment.env"; ' +
    'load_deployment_env; printf "%s\\n" "$BIND_IPV4"');
  f.ok(result);
  assert.equal(result.stdout, `${privateIp}\n${publicIp}\n`, f.output(result));
});

for (const [label, listeners, conflict] of [
  ['no listeners', '', false],
  ['same IPv4', `${privateIp}:80`, true],
  ['IPv4 wildcard', '0.0.0.0:80', true],
  ['unqualified wildcard', '*:80', true],
  ['IPv6 wildcard', '[::]:80', true],
  ['another local IPv4', `${otherIp}:80`, false],
  ['loopback only', '127.0.0.1:80', false],
]) {
  test(`bind-aware TCP port check: ${label}`, t => {
    const f = new Fixture(t);
    f.write('listeners', listeners ? `LISTEN 0 4096 ${listeners} 0.0.0.0:*\n` : '');
    const result = f.call('check_bind_port_available', [privateIp, '80', 'tcp']);
    if (conflict) f.failed(result); else f.ok(result);
    assert.equal(f.calls('ss').length, 1, 'Port check must use the stubbed listener inventory');
    assert(f.calls('ss')[0].some(arg => arg === '--tcp' || /^-[^-]*t/.test(arg)), 'TCP check must query TCP listeners');
    assert(f.calls('ss')[0].includes('sport = :80'), 'Listener inventory must be restricted to the requested port');
  });
}
for (const conflict of [false, true]) {
  test(`bind-aware UDP 443 check ${conflict ? 'rejects same-address listener' : 'allows unrelated-address listener'}`,
    t => {
      const f = new Fixture(t);
      f.write('listeners', `UNCONN 0 0 ${conflict ? privateIp : otherIp}:443 0.0.0.0:*\n`);
      const result = f.call('check_bind_port_available', [privateIp, '443', 'udp']);
      if (conflict) f.failed(result); else f.ok(result);
      assert.equal(f.calls('ss').length, 1);
      assert(f.calls('ss')[0].some(arg => arg === '--udp' || /^-[^-]*u/.test(arg)), 'UDP check must query UDP listeners');
      assert(f.calls('ss')[0].includes('sport = :443'));
    });
}
test('bind-aware port check fails closed when ss fails',
  t => {
    const f = new Fixture(t);
    f.failed(f.call('check_bind_port_available', [privateIp, '443', 'tcp'], { SS_EXIT: '2' }));
  });

test('port preflight rejects invalid binds, ports, and protocols before querying listeners/containers', t => {
  const f = new Fixture(t);
  for (const args of [
    ['0.0.0.0', '80', 'tcp'], ['127.0.0.1', '80', 'tcp'], [publicIp, '80', 'tcp'],
    [privateIp, '0', 'tcp'], [privateIp, '65536', 'tcp'], [privateIp, '080', 'tcp'],
    [privateIp, '443', 'sctp'],
  ]) f.failed(f.call('check_bind_port_available', args));
  assert.deepEqual(f.calls('ss'), []);
  assert.deepEqual(f.calls('docker'), []);
});

for (const [label, binding, protocol, conflict] of [
  ['target IPv4 mapping', `80/tcp -> ${privateIp}:80`, 'tcp', true],
  ['IPv4 wildcard mapping', '80/tcp -> 0.0.0.0:80', 'tcp', true],
  ['IPv6 wildcard mapping', '80/tcp -> [::]:80', 'tcp', true],
  ['another IPv4 mapping', `80/tcp -> ${otherIp}:80`, 'tcp', false],
  ['UDP mapping does not conflict with TCP', `443/udp -> ${privateIp}:443`, 'tcp', false],
  ['TCP mapping does not conflict with UDP', `443/tcp -> ${privateIp}:443`, 'udp', false],
  ['target UDP mapping', `443/udp -> ${privateIp}:443`, 'udp', true],
]) {
  test(`read-only container-port mock: ${label}`, t => {
    const f = new Fixture(t);
    const port = binding.startsWith('80/') ? '80' : '443';
    f.write('containers', 'fixture-container\n');
    f.write('container-ports', `${binding}\n`);
    const result = f.call('check_bind_port_available', [privateIp, port, protocol]);
    if (conflict) f.failed(result); else f.ok(result);
    assert.deepEqual(f.calls('docker')[0], ['ps', '--filter', `publish=${port}`, '--format', '{{.ID}}']);
    assert(f.calls('docker').some(args => args.join(' ') === 'port fixture-container'));
  });
}

test('the current project Caddy mapping is accepted without stopping or modifying its container', t => {
  const f = new Fixture(t);
  f.write('containers', 'fixture-container\n');
  f.write('container-project', 'license-platform\n');
  f.write('container-service', 'caddy\n');
  f.write('container-ports', `80/tcp -> ${privateIp}:80\n`);
  f.ok(f.call('check_bind_port_available', [privateIp, '80', 'tcp']));
  assert.deepEqual(f.calls('ss'), [], 'Owned Caddy publication is an allowed existing binding');
  assert(f.calls('docker').every(args => ['ps', 'inspect', 'port'].includes(args[0])));
});

test('a non-Caddy container in the current project is not exempt from bind conflicts', t => {
  const f = new Fixture(t);
  f.write('containers', 'fixture-container\n');
  f.write('container-project', 'license-platform\n');
  f.write('container-service', 'app\n');
  f.write('container-ports', `80/tcp -> ${privateIp}:80\n`);
  f.failed(f.call('check_bind_port_available', [privateIp, '80', 'tcp']));
});

for (const [label, environment] of [
  ['container enumeration', { DOCKER_PS_EXIT: '2' }],
  ['container port inspection', { DOCKER_PORT_EXIT: '2' }],
]) {
  test(`port preflight fails closed after ${label} failure in the shell mock`, t => {
    const f = new Fixture(t);
    f.write('containers', 'fixture-container\n');
    f.failed(f.call('check_bind_port_available', [privateIp, '80', 'tcp'], environment));
  });
}

const optionValue = (args, name) => {
  const index = args.indexOf(name);
  assert(index >= 0, `Missing curl argument ${name}: ${JSON.stringify(args)}`);
  assert(index + 1 < args.length, `Missing value for ${name}`);
  return args[index + 1];
};
for (const [scheme, host, timeout] of [
  ['http', publicIp, '7'], ['https', publicIp, '9'],
  ['http', 'license.example.test', '11'], ['https', 'license.example.test', undefined],
]) {
  test(`health probe preserves ${scheme} public ${host === publicIp ? 'numeric host' : 'domain/Host/SNI'} while connecting to private bind`, t => {
    const f = new Fixture(t);
    const args = [scheme, host, privateIp];
    if (timeout !== undefined) args.push(timeout);
    const result = f.call('probe_public_health', args);
    f.ok(result);
    assert.equal(result.stdout, '', f.output(result));
    const calls = f.calls('curl');
    assert.equal(calls.length, 1);
    const curlArgs = calls[0];
    const port = scheme === 'http' ? '80' : '443';
    assert.equal(curlArgs[0], '--disable', 'User curl config must be disabled before any other option');
    assert.equal(optionValue(curlArgs, '--noproxy'), '*');
    assert.equal(optionValue(curlArgs, '--connect-to'), `${host}:${port}:${privateIp}:${port}`);
    assert.equal(curlArgs.at(-1), `${scheme}://${host}/health`, 'Keep the public authority for Host and TLS SNI/certificate checks');
    assert.equal(optionValue(curlArgs, '--connect-timeout'), timeout ?? '15');
    assert.equal(optionValue(curlArgs, '--max-time'), timeout ?? '15');
    assert.equal(optionValue(curlArgs, '--request'), 'GET');
    assert.equal(optionValue(curlArgs, '--write-out'), '%{http_code}');
    assert.equal(optionValue(curlArgs, '--proto'), '=http,https');
    for (const forbidden of ['--resolve', '-L', '--location', '--location-trusted', '-k', '--insecure']) {
      assert(!curlArgs.includes(forbidden), `Unsafe or obsolete curl argument: ${forbidden}`);
    }
    f.noProbeFiles();
  });
}

for (const [label, status, body, exit] of [
  ['HTTP 204', '204', healthyBody, '0'],
  ['HTTP redirect', '302', healthyBody, '0'],
  ['HTTP server error', '500', healthyBody, '0'],
  ['malformed HTTP status', '200200', healthyBody, '0'],
  ['empty body', '200', '', '0'],
  ['unhealthy JSON', '200', '{"status":"unhealthy"}', '0'],
  ['JSON whitespace is not the exact health contract', '200', '{"status": "healthy"}', '0'],
  ['extra response bytes', '200', healthyBody + 'extra', '0'],
  ['connection failure', '000', healthyBody, '7'],
  ['timeout', '000', healthyBody, '28'],
  ['TLS certificate failure', '200', healthyBody, '60'],
]) {
  test(`health probe rejects ${label} and removes its temporary response body`, t => {
    const f = new Fixture(t);
    f.write('curl-body', body);
    const result = f.call('probe_public_health', ['https', publicIp, privateIp, '1'],
      { CURL_STATUS: status, CURL_EXIT: exit });
    f.failed(result);
    assert.equal(result.stdout, '', f.output(result));
    assert.equal(f.calls('curl').length, 1);
    f.noProbeFiles();
  });
}

test('health probe invalid arguments fail before invoking curl', t => {
  const f = new Fixture(t);
  for (const args of [
    ['ftp', publicIp, privateIp, '1'], ['http', '', privateIp, '1'],
    ['https', 'host.example.test/path', privateIp, '1'], ['https', `${publicIp}:443`, privateIp, '1'],
    ['http', publicIp, '', '1'], ['http', publicIp, '::1', '1'],
    ['http', publicIp, privateIp, '0'], ['http', publicIp, privateIp, 'bad'],
  ]) f.failed(f.call('probe_public_health', args));
  assert.deepEqual(f.calls('curl'), []);
  f.noProbeFiles();
});

const prepareRestored = (f, environment = {}) => {
  f.write('functions.sh', restoreFunctions);
  return f.run('validate_restored_env "$RESTORED_CONFIG/deployment.env"; prepare_restored_bind_ipv4; ' +
    'printf "RESTORED=%s|%s\\n" "$RESTORED_PUBLIC_IPV4" "$RESTORED_BIND_IPV4"', [], environment);
};

test('extracted restore preflight preserves the NAT archive BIND, not current process addresses', t => {
  const f = new Fixture(t);
  const archived = `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${privateIp}\nHSTS_ENABLED=false\n`;
  f.write('config/deployment.env', archived);
  const result = prepareRestored(f, { PUBLIC_IPV4: '203.0.113.88', BIND_IPV4: otherIp });
  f.ok(result);
  assert.equal(result.stdout, `RESTORED=${publicIp}|${privateIp}\n`, f.output(result));
  assert.equal(f.text('config/deployment.env'), archived, 'A new archive must not be rebound or rewritten');
  f.noRouteCalls();
  assert.deepEqual(f.calls('docker'), []);
});

test('extracted legacy restore preflight persists its own archived PUBLIC instead of inherited BIND', t => {
  const f = new Fixture(t);
  f.network({ addresses: address('eth0', publicIp) + address('eth1', privateIp, 3) });
  f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\nHSTS_ENABLED=false\n`);
  const result = prepareRestored(f, { PUBLIC_IPV4: '203.0.113.88', BIND_IPV4: privateIp, FIXTURE_ALLOW_ENV_WRITE: '1' });
  f.ok(result);
  assert(result.stdout.endsWith(`RESTORED=${publicIp}|${publicIp}\n`), f.output(result));
  assert.equal(f.text('config/deployment.env'), `PUBLIC_IPV4=${publicIp}\nHSTS_ENABLED=false\nBIND_IPV4=${publicIp}\n`);
  assert.deepEqual(fs.readdirSync(path.join(f.root, 'config')), ['deployment.env'], 'Atomic env write must not leave a temporary file');
  f.noRouteCalls();
  assert.deepEqual(f.calls('docker'), []);
});

test('extracted restore preflight rejects stale/nonlocal/unsafe archive binds without choosing a replacement', t => {
  const f = new Fixture(t);
  for (const bind of ['10.99.0.10', '0.0.0.0', '127.0.0.1', '169.254.1.2', '10.42.0.999', 'host.example.test']) {
    f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${bind}\n`);
    const result = prepareRestored(f, { PUBLIC_IPV4: publicIp, BIND_IPV4: privateIp });
    f.failed(result);
    assert.notEqual(result.stderr, '', `Archive bind ${bind} must be explicitly rejected`);
  }
  // Legacy PUBLIC is not local on this host: even a usable current BIND cannot
  // silently migrate an archive that deliberately restores the old binding.
  f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\n`);
  f.failed(prepareRestored(f, { BIND_IPV4: privateIp, FIXTURE_ALLOW_ENV_WRITE: '1' }));
  assert.match(f.text('config/deployment.env'), new RegExp(`^BIND_IPV4=${publicIp.replaceAll('.', '\\.')}\\n`, 'm'));
  f.noRouteCalls();
  assert.deepEqual(f.calls('docker'), []);
});

test('extracted restore preflight cannot fill missing or invalid archived PUBLIC from process environment', t => {
  const f = new Fixture(t);
  for (const archived of [`BIND_IPV4=${privateIp}\n`, `PUBLIC_IPV4=0.0.0.0\nBIND_IPV4=${privateIp}\n`]) {
    f.write('config/deployment.env', archived);
    f.failed(prepareRestored(f, { PUBLIC_IPV4: publicIp, BIND_IPV4: privateIp }));
  }
  assert.deepEqual(f.calls('ip'), []);
});

test('extracted restore environment whitelist accepts BIND and rejects duplicate/unknown/unsafe entries', t => {
  const f = new Fixture(t);
  f.write('functions.sh', restoreFunctions);
  f.write('config/deployment.env', `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${privateIp}\n`);
  f.ok(f.call('validate_restored_env', [`${f.p}/config/deployment.env`]));
  for (const archived of [
    `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=${privateIp}\nBIND_IPV4=${otherIp}\n`,
    `PUBLIC_IPV4=${publicIp}\nUNAPPROVED_SETTING=value\n`,
    `PUBLIC_IPV4=${publicIp}\nBIND_IPV4=$(touch fixture-must-not-exist)\n`,
  ]) {
    f.write('config/deployment.env', archived);
    f.failed(f.call('validate_restored_env', [`${f.p}/config/deployment.env`]));
  }
  assert(!fs.existsSync(path.join(f.root, 'fixture-must-not-exist')));
  assert.deepEqual(f.calls('ip'), []);
});

test('restore preflights archived or current BIND before safety backup, stop, and database overwrite (text only)', () => {
  const position = expression => {
    const match = expression.exec(restoreSource);
    assert(match, `Missing restore preflight boundary ${expression}`);
    return match.index;
  };
  const configBranch = position(/^if \(\( RESTORE_CONFIG == 1 \)\); then\s*$/m);
  const validation = position(/^\s*validate_restored_env "\$\{RESTORED_CONFIG\}\/deployment\.env"\s*$/m);
  const preparation = position(/^\s*prepare_restored_bind_ipv4\s*$/m);
  const required = position(/^\s*for required_key in [^\r\n]*\bBIND_IPV4\b[^\r\n]*; do\s*$/m);
  const currentBindPreflight = position(/^else\r?\n[ \t]+require_local_bind_ipv4 "\$\{BIND_IPV4\}"\r?\nfi\s*$/m);
  const backup = position(/^LICENSE_PLATFORM_OPERATION_LOCK_HELD=1 "\$\{LP_HOME\}\/bin\/backup\.sh" >\/dev\/null\s*$/m);
  const stop = position(/^compose stop caddy app\s*$/m);
  const database = position(/^compose exec --no-TTY db pg_restore /m);
  assert(configBranch < validation && validation < preparation && preparation < required && required < currentBindPreflight
    && currentBindPreflight < backup && backup < stop && stop < database,
  'Archive bind validation and database-only current-bind validation must remain before destructive restore operations');
});

for (const host of [publicIp, 'license.example.test']) {
  test(`extracted HSTS probe retains ${host === publicIp ? 'numeric' : 'domain'} public authority with private connect-to`, t => {
    const f = new Fixture(t);
    f.write('functions.sh', hstsFunction);
    f.ok(f.call('probe_hsts', ['present'], { host, BIND_IPV4: privateIp }));
    const calls = f.calls('curl');
    assert.equal(calls.length, 1);
    const args = calls[0];
    assert.equal(args[0], '--disable');
    assert.equal(optionValue(args, '--noproxy'), '*');
    assert.equal(optionValue(args, '--connect-to'), `${host}:443:${privateIp}:443`);
    assert.equal(optionValue(args, '--proto'), '=https');
    assert.equal(args.at(-1), `https://${host}/health`);
    assert(optionValue(args, '--dump-header').startsWith(`${f.p}/tmp/`));
    for (const forbidden of ['--resolve', '-L', '--location', '--location-trusted', '-k', '--insecure']) {
      assert(!args.includes(forbidden), `Unsafe HSTS curl argument ${forbidden}`);
    }
    f.noProbeFiles();
  });
}

test('extracted HSTS probe rejects TLS errors and removes both temporary files', t => {
  const f = new Fixture(t);
  f.write('functions.sh', hstsFunction);
  f.failed(f.call('probe_hsts', ['present'], { host: publicIp, BIND_IPV4: privateIp, CURL_EXIT: '60' }));
  assert.equal(f.calls('curl').length, 1);
  f.noProbeFiles();
});

test('extracted HSTS probe rejects redirects, unhealthy bodies, missing/wrong policy and includeSubDomains', t => {
  const f = new Fixture(t);
  f.write('functions.sh', hstsFunction);
  for (const [status, body, headers] of [
    ['302', healthyBody, hstsHeaders],
    ['200', '{"status":"unhealthy"}', hstsHeaders],
    ['200', healthyBody, 'HTTP/1.1 200 OK\r\n\r\n'],
    ['200', healthyBody, 'HTTP/1.1 200 OK\r\nStrict-Transport-Security: max-age=60\r\n\r\n'],
    ['200', healthyBody, 'HTTP/1.1 200 OK\r\nStrict-Transport-Security: max-age=2592000; includeSubDomains\r\n\r\n'],
  ]) {
    f.write('curl-body', body);
    f.write('curl-headers', headers);
    f.failed(f.call('probe_hsts', ['present'], { host: 'license.example.test', BIND_IPV4: privateIp, CURL_STATUS: status }));
    f.noProbeFiles();
  }
});

test('every deployment public-health caller connects through BIND rather than PUBLIC (text only)', () => {
  const shellFiles = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) return shellFiles(file);
    return entry.isFile() && (entry.name.endsWith('.sh') || entry.name === 'licensectl') ? [file] : [];
  });
  const callers = [];
  for (const file of shellFiles(deploy)) {
    const lines = read(file).replace(/\\\r?\n\s*/g, ' ').split(/\r?\n/);
    for (const line of lines) {
      if (!/\bprobe_public_health\b/.test(line) || /^\s*(?:#|probe_public_health\s*\(\))/.test(line)) continue;
      const call = /\bprobe_public_health\s+(\S+)\s+(\S+)\s+(\S+)/.exec(line);
      assert(call, `Unrecognized public-health call in ${file}: ${line}`);
      assert.equal(call[3], '"${BIND_IPV4}"', `Third/connection argument must be BIND_IPV4 in ${file}: ${line}`);
      callers.push(path.basename(file));
    }
  }
  assert(callers.length >= 6, 'Expected health-check and HTTPS/HSTS lifecycle probe call sites');
  assert(callers.includes('health-check.sh'));
  assert(callers.includes('licensectl'));
});

test('Compose HTTP and HTTPS published ports use BIND with fail-closed legacy PUBLIC fallback (text only)', () => {
  const expression = String.raw`\$\{BIND_IPV4:-\$\{PUBLIC_IPV4:\?[^}\r\n]+\}\}`;
  for (const [file, port, protocol] of [
    ['compose.yml', 80, 'tcp'], ['compose.https.yml', 443, 'tcp'], ['compose.https.yml', 443, 'udp'],
  ]) {
    const publishedPort = read(path.join(deploy, file)).split('\n')
      .find(line => line.includes(`:${port}:${port}/${protocol}`)) ?? '';
    assert.match(publishedPort,
      new RegExp(`^\\s*-\\s*"${expression}:${port}:${port}/${protocol}"\\s*$`, 'm'),
      `${file} must publish ${port}/${protocol} on BIND_IPV4, with PUBLIC_IPV4 fallback for old environments`);
  }
});
