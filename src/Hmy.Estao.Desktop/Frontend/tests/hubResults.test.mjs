import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/HarnessHub.tsx', import.meta.url), 'utf8');

test('installation success uses a compact status instead of the persistent results panel', () => {
  assert.doesNotMatch(source, /className="section hub-results"/);
  assert.match(source, /result\.outcome === "installed"/);
  assert.match(source, /!busy && installedIn && <InstallNotice/);
  assert.match(source, /className="hub-operation-result" role="status"/);
  assert.match(source, /t\("hub\.installed_in", installedIn\)/);
});

test('success expires after five seconds without restarting on each state poll', () => {
  assert.match(source, /setTimeout\(\(\) => setVisible\(false\), 5000\)/);
  assert.match(source, /return \(\) => window\.clearTimeout\(timer\)/);
  assert.match(source, /\}, \[installedIn\]\)/);
  assert.match(source, /return visible \?/);
});

test('failures and partial installations remain visible separately from the success notice', () => {
  assert.match(source, /result\.outcome === "failed" \|\| result\.outcome === "partial" \|\| result\.outcome === "cancelled"/);
  assert.match(source, /className="inline-warning" role="alert" key=\{result.id\}/);
  assert.match(source, /t\("hub\.install_conflicts", result.conflicts\)/);
});
