import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

const source = readFileSync(new URL('../src/hubTypes.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { catalogInstallItems, catalogInstallProgress, selectCatalogItems, defaultSelection, updateSelection } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));

test('Shift selects the visible range, and Ctrl adds or removes individual rows', () => {
  const visible = ['a', 'b', 'c', 'd'];
  assert.deepEqual(selectCatalogItems(visible, ['c'], 'c', 'a', true, false), ['a', 'b', 'c']);
  assert.deepEqual(selectCatalogItems(visible, ['a'], 'a', 'd', false, true), ['a', 'd']);
  assert.deepEqual(selectCatalogItems(visible, ['a', 'd'], 'd', 'a', false, true), ['d']);
  assert.deepEqual(selectCatalogItems(visible, ['a'], 'c', 'd', true, true), ['a', 'c', 'd']);
  assert.deepEqual(selectCatalogItems(visible, ['hidden'], 'hidden', 'b', true, false), ['b']);
});

test('the catalog batch counts only installable entries in the explicit selection or whole catalog', () => {
  const item = { inLibrary: true, curated: true, installable: true, compatibleTools: ['codex'] };
  const items = ['skill', 'instruction', 'mcp', 'cli', 'file'].map(type => ({ ...item, id: type, itemType: type }))
    .concat({ ...item, id: 'unlisted', curated: false }, { ...item, id: 'disabled', installable: false });
  const tools = [{ id: 'codex' }];
  assert.deepEqual(catalogInstallItems(items, tools).map(item => item.id), ['skill', 'instruction', 'mcp', 'cli']);
  assert.deepEqual(catalogInstallItems(items, tools, ['mcp', 'file']).map(item => item.id), ['mcp']);
  assert.deepEqual(catalogInstallItems(items, [], undefined).map(item => item.id), ['cli']);
  assert.deepEqual(catalogInstallItems(items, tools, []), []);
});
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

test('catalog batch progress reports only finished items while the operation is running', () => {
  assert.equal(catalogInstallProgress(undefined), undefined);
  assert.equal(catalogInstallProgress({ busy: false, installProgress: { completed: 3, total: 3 } }), undefined);
  assert.equal(catalogInstallProgress({ busy: true, installProgress: null }), undefined);
  assert.equal(catalogInstallProgress({ busy: true, installProgress: { completed: 0, total: 0 } }), undefined);
  assert.deepEqual(catalogInstallProgress({ busy: true, installProgress: { completed: 0, total: 4 } }), { completed: 0, total: 4, percent: 0 });
  assert.deepEqual(catalogInstallProgress({ busy: true, installProgress: { completed: 2.5, total: 4 } }), { completed: 2, total: 4, percent: 63 });
  assert.deepEqual(catalogInstallProgress({ busy: true, installProgress: { completed: 9, total: 4 } }), { completed: 4, total: 4, percent: 100 });
});
