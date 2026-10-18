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

test('global.json permits the selected stable SDK feature band without a major-version roll forward', () => {
  const sdk = JSON.parse(read('global.json')).sdk;
  const minimum = sdk.version.split('.').map(Number);
  const selected = /sdk:(\d+)\.(\d+)\.(\d+)-noble$/.exec(expected.SDK).slice(1).map(Number);
  assert.equal(sdk.rollForward, 'latestFeature');
  assert.equal(sdk.allowPrerelease, false);
  assert.deepEqual(selected.slice(0, 2), minimum.slice(0, 2));
  assert(selected[2] >= minimum[2]);
});
