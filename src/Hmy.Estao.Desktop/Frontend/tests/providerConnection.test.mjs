import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
const source = readFileSync(new URL('../src/providerConnection.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { connectionPresentation } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));

test('disabled providers always show Disabled, regardless of authentication', () => {
  for (const signedIn of [true, false, null]) assert.deepEqual(connectionPresentation(false, { signedIn, message: 'status' }), { key: 'connection.disabled', warning: false });
  assert.deepEqual(connectionPresentation(false), { key: 'connection.disabled', warning: false });
});
test('only enabled signed-out providers use the warning color', () => {
  assert.deepEqual(connectionPresentation(true, { signedIn: false, message: 'Not signed in' }), { key: 'connection.signed_out', warning: true });
  assert.deepEqual(connectionPresentation(true, { signedIn: true, message: 'Connected' }), { key: 'connection.signed_in', warning: false });
});
test('unknown or pending checks never pretend the user is signed out', () => {
  assert.deepEqual(connectionPresentation(true), { key: 'connection.checking', warning: false });
  assert.deepEqual(connectionPresentation(true, { signedIn: null, message: 'Network failed' }), { key: 'connection.unknown', warning: false });
});
test('checking state is explicit and independent of the message language', () => {
  assert.equal(connectionPresentation(true, { signedIn: null, message: 'Comprobando…', checking: true }).key, 'connection.checking');
  assert.equal(connectionPresentation(true, { signedIn: null, message: 'Checking failed', checking: false }).key, 'connection.unknown');
});
