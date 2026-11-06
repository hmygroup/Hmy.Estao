import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
const source = readFileSync(new URL('../src/providerConnection.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { connectionPresentation } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));

test('disabled providers always show Disabled, regardless of authentication', () => {
  for (const signedIn of [true, false, null]) assert.deepEqual(connectionPresentation(false, { signedIn, message: 'status' }), { label: 'Disabled', warning: false });
  assert.deepEqual(connectionPresentation(false), { label: 'Disabled', warning: false });
});
test('only enabled signed-out providers use the warning color', () => {
  assert.deepEqual(connectionPresentation(true, { signedIn: false, message: 'Not signed in' }), { label: 'Not signed in', warning: true });
  assert.deepEqual(connectionPresentation(true, { signedIn: true, message: 'Connected' }), { label: 'Signed in', warning: false });
});
test('unknown or pending checks never pretend the user is signed out', () => {
  assert.deepEqual(connectionPresentation(true), { label: 'Checking sign-in…', warning: false });
  assert.deepEqual(connectionPresentation(true, { signedIn: null, message: 'Network failed' }), { label: 'Could not check sign-in', warning: false });
});
