import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/HarnessHub.tsx', import.meta.url), 'utf8');

test('installation success is reported to the Settings footer without an inline notice', () => {
  assert.doesNotMatch(source, /className="section hub-results"/);
  assert.match(source, /result\.outcome === "installed"/);
  assert.doesNotMatch(source, /InstallNotice|className="hub-operation-result"/);
  assert.match(source, /t\("hub\.installed_in", installedIn\)/);
  assert.match(source, /onStatusChange\?\.\(status\)/);
});

test('footer receives progress and errors and is cleared when leaving Harness Hub', () => {
  assert.match(source, /const status = error/);
  assert.match(source, /message\(state\?\.progressMessage\)/);
  assert.match(source, /useEffect\(\(\) => \(\) => onStatusChange\?\.\(""\), \[onStatusChange\]\)/);
  const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
  assert.match(app, /<HarnessHub onStatusChange=\{setHubMessage\}/);
  assert.match(app, /<footer className="settings-footer"><span role="status">\{\(page === "hub" && hubMessage\)/);
  const browser = readFileSync(new URL('../src/HubSkillBrowser.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(browser, /completedMessage|hub-operation-result/);
});

test('failures and partial installations remain visible separately from the success notice', () => {
  assert.match(source, /result\.outcome === "failed" \|\| result\.outcome === "partial" \|\| result\.outcome === "cancelled"/);
  assert.match(source, /className="inline-warning" role="alert" key=\{result.id\}/);
  assert.match(source, /t\("hub\.install_conflicts", result.conflicts\)/);
});
