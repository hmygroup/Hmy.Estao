import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

const source = readFileSync(new URL('../src/hubTypes.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { defaultSelection, updateSelection } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));
const artifact = (id, status = 'new', dependencies = []) => ({ id, status, dependencies });
const tool = { id: 'codex', artifacts: [artifact('base'), artifact('dependent', 'new', ['base']), artifact('blocked', 'conflict'), artifact('needs-blocked', 'new', ['blocked']), artifact('present', 'present')] };

test('default selection includes compatible and already-present skills, excluding conflicts and their dependents', () => {
  assert.deepEqual(defaultSelection({ tools: [tool] }), { codex: ['base', 'dependent', 'present'] });
});
test('selecting a skill includes its required dependencies', () => {
  assert.deepEqual(new Set(updateSelection(tool, [], 'dependent', true)), new Set(['dependent', 'base']));
});
test('deselecting a dependency deselects dependent skills but keeps unrelated choices', () => {
  assert.deepEqual(updateSelection(tool, ['base', 'dependent', 'present'], 'base', false), ['present']);
});
test('a skill that needs a conflicting dependency cannot be selected', () => {
  assert.deepEqual(updateSelection(tool, ['present'], 'needs-blocked', true), ['present']);
});
test('install-all selections remain independent for each tool', () => {
  assert.deepEqual(defaultSelection({ tools: [tool, { id: 'claude', artifacts: [artifact('base', 'conflict')] }] }), {
    codex: ['base', 'dependent', 'present'], claude: []
  });
});
