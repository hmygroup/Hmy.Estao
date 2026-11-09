import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

const source = readFileSync(new URL('../src/hubTypes.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { showHubConnect } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));

test('Connect stays hidden until authorization is conclusively checked', () => {
  assert.equal(showHubConnect(), false);
  assert.equal(showHubConnect({ connected: false }), false);
  assert.equal(showHubConnect({ connected: false, authorizationChecked: false, checkingAuthorization: true }), false);
  assert.equal(showHubConnect({ connected: true, authorizationChecked: true, checkingAuthorization: false }), false);
  assert.equal(showHubConnect({ connected: false, authorizationChecked: true, checkingAuthorization: false }), true);
});
test('rechecking a signed-out session hides Connect until the result returns', () => {
  assert.equal(showHubConnect({ connected: false, authorizationChecked: true, checkingAuthorization: true }), false);
});
