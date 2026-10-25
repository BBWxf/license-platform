// Offline image-selection regressions. No Docker or registry request is made.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const expected = {
  SDK: 'mcr.microsoft.com/dotnet/sdk:10.0.302-noble',
  ASPNET: 'mcr.microsoft.com/dotnet/aspnet:10.0.10-noble',
};

test('Dockerfile, installer and documentation agree on the verified explicit .NET image tags', () => {
  for (const [kind, image] of Object.entries(expected)) {
    const dockerfile = new RegExp(`^ARG DOTNET_${kind}_IMAGE=(\\S+)$`, 'm').exec(read('Dockerfile'));
    const installer = new RegExp(`^readonly DEFAULT_DOTNET_${kind}_IMAGE="([^"]+)"$`, 'm').exec(read('deploy/install.sh'));
    assert.equal(dockerfile?.[1], image);
    assert.equal(installer?.[1], image);
    assert(read('docs/deployment.md').includes('`' + image + '`'));
  }
});

test('local image build bounds MSBuild and restore parallelism without disabling static assets', () => {
  const dockerfile = read('Dockerfile').replace(/\\\r?\n\s*/g, ' ');
  const build = /^RUN dotnet restore ([^\r\n]+)$/m.exec(dockerfile)?.[1];
  assert(build, 'The real Dockerfile must contain the restore/publish build step.');
  const [restore, publish] = build.split('&&');
  assert(restore.includes('--disable-parallel'));
  assert.match(restore, /(?:^|\s)-m:1(?:\s|$)/);
  assert(publish.includes('dotnet publish'));
  assert.match(publish, /(?:^|\s)-m:1(?:\s|$)/);
  assert(publish.includes('--disable-build-servers'));
  assert(publish.includes('/p:UseSharedCompilation=false'));
  assert(publish.includes('--no-restore'));
  assert(!/StaticWebAssetsEnabled\s*=\s*false/i.test(dockerfile));
});

test('global.json permits the selected stable SDK feature band without a major-version roll forward', () => {
  const sdk = JSON.parse(read('global.json')).sdk;
  const minimum = sdk.version.split('.').map(Number);
  const selected = /sdk:(\d+)\.(\d+)\.(\d+)-noble$/.exec(expected.SDK).slice(1).map(Number);
  assert.equal(sdk.rollForward, 'latestFeature');
  assert.equal(sdk.allowPrerelease, false);
  assert.deepEqual(selected.slice(0, 2), minimum.slice(0, 2));
  assert(selected[2] >= minimum[2]);
});
