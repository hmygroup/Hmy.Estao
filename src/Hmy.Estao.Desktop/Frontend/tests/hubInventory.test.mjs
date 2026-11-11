import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

const source = readFileSync(new URL('../src/hubTypes.ts', import.meta.url), 'utf8');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { detectedTools, filterSkills, skillToolStatus, localizedDescription, localizedCategory } = await import('data:text/javascript;base64,' + Buffer.from(javascript).toString('base64'));

const tool = (id, detected = true, error = null) => ({ id, name: id, detected, status: '', error });
const occurrence = (toolId, path = `/${toolId}/SKILL.md`, shared = false, error = null) => ({ toolId, path, shared, hash: null, error });
const skill = (id, options = {}) => ({
  id, name: options.name ?? id, description: options.description ?? '', inLibrary: options.inLibrary ?? false,
  installable: options.installable ?? false, compatibleTools: options.compatibleTools ?? [],
  occurrences: options.occurrences ?? [], issue: options.issue ?? null,
  curated: options.curated, itemType: options.itemType, descriptions: options.descriptions,
  categories: options.categories, defaultLanguage: options.defaultLanguage,
});
const tools = [tool('codex'), tool('claude'), tool('absent', false)];

test('detectedTools hides absent tools', () => {
  assert.deepEqual(detectedTools(tools).map(item => item.id), ['codex', 'claude']);
});

test('skill translations fall back directly to the standard description, not the catalog language', () => {
  const translated = skill('translated', { description: 'Standard fallback', descriptions: { en: 'English translation', es: 'Texto español' }, defaultLanguage: 'en' });
  assert.equal(localizedDescription(translated, 'es'), 'Texto español');
  assert.equal(localizedDescription(translated, 'en'), 'English translation');
  assert.equal(localizedDescription(translated, 'fr'), 'Standard fallback');
  assert.equal(localizedDescription(skill('legacy', { description: 'Legacy fallback' }), 'es'), 'Legacy fallback');
  assert.equal(localizedDescription(skill('file', { itemType: 'file' }), 'es'), '');
});

test('installed and library views include their distinct inventory sets', () => {
  const entries = [
    skill('local', { occurrences: [occurrence('codex')] }),
    skill('catalog', { inLibrary: true, installable: true, compatibleTools: ['codex'] }),
    skill('both', { inLibrary: true, occurrences: [occurrence('claude')] }),
    skill('undetected-only', { occurrences: [occurrence('absent')] }),
  ];
  assert.deepEqual(filterSkills(entries, tools, 'installed', '', '', 'all').map(item => item.id), ['both', 'local']);
  assert.deepEqual(filterSkills(entries, tools, 'library', '', '', 'all').map(item => item.id), ['both', 'catalog']);
});

test('catalog partitions curated manifest items from unlisted repo and local skills', () => {
  const entries = [
    skill('curated-repo', { inLibrary: true, curated: true, installable: true, compatibleTools: ['codex'] }),
    skill('unlisted-repo', { inLibrary: true, curated: false, installable: true, compatibleTools: ['codex'] }),
    skill('local', { inLibrary: false, curated: false, occurrences: [occurrence('codex')] }),
    skill('manifest-file', { inLibrary: true, curated: true, itemType: 'file' }),
    skill('unlisted-file', { inLibrary: true, curated: false, itemType: 'file' }),
  ];
  assert.deepEqual(filterSkills(entries, tools, 'library', '', '', 'all').map(item => item.id), ['curated-repo', 'manifest-file']);
  assert.deepEqual(filterSkills(entries, tools, 'other', '', '', 'all').map(item => item.id), ['local', 'unlisted-repo']);
  assert.deepEqual(filterSkills(entries, tools, 'installed', '', '', 'all').map(item => item.id), ['local']);
});

test('search, tool and status filters combine', () => {
  const entries = [
    skill('alpha', { name: 'Alpha Skill', description: 'fast search', occurrences: [occurrence('codex')] }),
    skill('beta', { name: 'Beta Skill', description: 'other', inLibrary: true, installable: true, compatibleTools: ['claude'] }),
    skill('gamma', { name: 'Gamma Skill', description: 'other', occurrences: [occurrence('claude')], issue: 'conflict' }),
  ];
  assert.deepEqual(filterSkills(entries, tools, 'installed', ' FAST ', '', 'all').map(item => item.id), ['alpha']);
  assert.deepEqual(filterSkills(entries, tools, 'library', 'beta', 'claude', 'installable').map(item => item.id), ['beta']);
  assert.deepEqual(filterSkills(entries, tools, 'installed', '', 'codex', 'missing').map(item => item.id), []);
  assert.deepEqual(filterSkills(entries, tools, 'installed', '', '', 'attention').map(item => item.id), ['gamma']);
});

test('localized description search, category selection, and type filters combine', () => {
  const entries = [
    skill('guide', { curated: true, itemType: 'skill', description: 'Build a garden', descriptions: { es: 'Crear jardines' }, categories: ['gardening'] }),
    skill('asset', { curated: true, itemType: 'file', descriptions: { en: 'Garden template' }, categories: ['gardening'] }),
    skill('uncategorized', { curated: true, itemType: 'file', description: 'Garden notes' }),
  ];
  const labels = { gardening: { en: 'Gardening', es: 'Jardinería' } };
  assert.equal(localizedDescription(entries[0], 'es'), 'Crear jardines');
  assert.equal(localizedDescription(entries[0], 'fr'), 'Build a garden');
  assert.equal(localizedCategory('gardening', labels, 'es', 'en'), 'Jardinería');
  assert.equal(localizedCategory('gardening', { gardening: { es: 'Jardinería' } }, 'fr', 'es'), 'Jardinería');
  assert.deepEqual(filterSkills(entries, tools, 'library', 'jardinería', '', 'all', 'es', 'gardening', 'skill', labels).map(item => item.id), ['guide']);
  assert.deepEqual(filterSkills(entries, tools, 'library', 'jardinería', '', 'all', 'fr', 'gardening', 'skill', labels, 'es').map(item => item.id), ['guide']);
  assert.deepEqual(filterSkills(entries, tools, 'library', '', '', 'all', 'en', 'gardening', 'file', labels).map(item => item.id), ['asset']);
  assert.deepEqual(filterSkills(entries, tools, 'library', '', '', 'all', 'en', '__uncategorized', 'file', labels).map(item => item.id), ['uncategorized']);
});

test('tool status distinguishes present, missing, unknown and unsupported', () => {
  const codex = tool('codex');
  assert.equal(skillToolStatus(skill('present', { occurrences: [occurrence('codex')] }), codex), 'present');
  assert.equal(skillToolStatus(skill('missing'), codex), 'missing');
  assert.equal(skillToolStatus(skill('library-no-compat-declaration', { inLibrary: true, compatibleTools: [] }), codex), 'missing');
  assert.equal(skillToolStatus(skill('explicitly-other-tool', { curated: true, compatibleTools: ['claude'] }), codex), 'unsupported');
  assert.equal(skillToolStatus(skill('bad-occurrence', { occurrences: [occurrence('codex', undefined, false, 'read failed')] }), codex), 'unknown');
  assert.equal(skillToolStatus(skill('bad-tool'), tool('codex', true, 'scan failed')), 'unknown');
});

test('installed skills can be filtered by inherited categories', () => {
  const categorized = skill('curated', { curated: true, categories: ['core', 'database'], occurrences: [occurrence('codex')] });
  const local = skill('local', { occurrences: [occurrence('codex')] });
  assert.deepEqual(filterSkills([categorized, local], tools, 'installed', '', '', 'all', 'en', 'database'), [categorized]);
  assert.deepEqual(filterSkills([categorized, local], tools, 'installed', '', '', 'all', 'en', '__uncategorized'), [local]);
});

test('tool installation filters do not present exportable files as installed or missing skills', () => {
  const file = skill('template', { curated: true, inLibrary: true, itemType: 'file' });
  for (const status of ['missing', 'attention', 'installable'])
    assert.deepEqual(filterSkills([file], tools, 'library', '', '', status), []);
  assert.deepEqual(filterSkills([file], tools, 'library', '', 'codex', 'all'), []);
  assert.deepEqual(filterSkills([file], tools, 'library', '', '', 'all'), [file]);
});

test('shared occurrences remain present and independently represented per tool', () => {
  const shared = skill('shared', { occurrences: [occurrence('codex', '/shared/SKILL.md', true), occurrence('claude', '/shared/SKILL.md', true)] });
  assert.equal(skillToolStatus(shared, tools[0]), 'present');
  assert.equal(skillToolStatus(shared, tools[1]), 'present');
  assert.equal(shared.occurrences.filter(item => item.shared).length, 2);
});

test('a partial tool scan failure does not hide a confirmed occurrence', () => {
  assert.equal(skillToolStatus(skill('known', { occurrences: [occurrence('codex')] }), tool('codex', true, 'Another root failed')), 'present');
});

test('attention filter does not attribute another tool metadata error to the selected tool', () => {
  const entry = skill('local', { issue: 'Claude could not read this skill', occurrences: [occurrence('codex'), occurrence('claude', '/bad/SKILL.md', false, 'read failed')] });
  assert.deepEqual(filterSkills([entry], tools, 'installed', '', 'codex', 'attention'), []);
  assert.deepEqual(filterSkills([entry], tools, 'installed', '', 'claude', 'attention'), [entry]);
});

test('library is optional; installed inventory remains available without library entries', () => {
  const local = skill('local', { occurrences: [occurrence('codex')] });
  assert.deepEqual(filterSkills([local], [tools[0]], 'installed', '', '', 'all'), [local]);
  assert.deepEqual(filterSkills([local], [tools[0]], 'library', '', '', 'all'), []);
});
