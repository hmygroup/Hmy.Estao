import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

const moduleUrl = source => 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');
const compile = path => ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX },
}).outputText;
const types = moduleUrl(compile('../src/hubTypes.ts'));
const translations = JSON.parse(readFileSync(new URL('../src/locales/en.json', import.meta.url), 'utf8'));
const i18n = moduleUrl(`const translations = ${JSON.stringify(translations)};
export const currentLanguage = () => 'en';
export const t = (key, ...args) => (translations[key] ?? key).replace(/\\{(\\d+)\\}/g, (_, i) => String(args[i]));
export const plural = (key, count) => t(key + (count === 1 ? '_one' : '_other'), count);
export const message = () => '';`);
const source = compile('../src/HubSkillBrowser.tsx')
  .replaceAll('"react/jsx-runtime"', JSON.stringify(import.meta.resolve('react/jsx-runtime')))
  .replaceAll('"react"', JSON.stringify(import.meta.resolve('react')))
  .replaceAll('"./hubTypes"', JSON.stringify(types)).replaceAll('"./i18n"', JSON.stringify(i18n));
const { default: HubSkillBrowser } = await import(moduleUrl(source));
// Render each tab as the initial view for static markup checks.
const { default: LibraryBrowser } = await import(moduleUrl(source.replace('useState("installed")', 'useState("library")')));
const { default: OtherBrowser } = await import(moduleUrl(source.replace('useState("installed")', 'useState("other")')));

const render = (itemType, tools, installed = true, Browser = HubSkillBrowser, curated = true) => renderToStaticMarkup(React.createElement(Browser, {
  state: { busy: false, tools, skills: [{ id: 'utility', name: 'Utility', description: 'Example',
    inLibrary: curated, curated, installable: curated, itemType, source: curated ? 'C:\\packages\\utility' : null, compatibleTools: tools.map(tool => tool.id),
    occurrences: installed ? [{ toolId: itemType === 'cli' ? 'global' : 'claude', path: 'C:\\utility.exe', shared: itemType === 'cli', hash: null }] : [],
  }] }, busy: false, run: async () => {},
}));

test('only HMY renders Install all beside GitHub and requires an installable catalog item', () => {
  const tools = [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }];
  const skill = { id: 'sample', name: 'Sample', description: 'Example', itemType: 'skill',
    curated: true, inLibrary: true, installable: true, compatibleTools: ['claude'], occurrences: [] };
  const state = { connected: true, libraryLoaded: true, libraryCommit: 'commit', tools, skills: [skill] };
  const renderView = (Browser, overrides = {}, busy = false) => renderToStaticMarkup(React.createElement(Browser, {
    state: { ...state, ...overrides }, busy, run: async () => {},
  }));

  assert.match(renderView(LibraryBrowser), /class="hub-catalog-actions"><button type="button" class="primary">Install all\.\.\.<\/button><button type="button" class="secondary">GitHub ↗<\/button>/);
  for (const Browser of [HubSkillBrowser, OtherBrowser])
    assert.doesNotMatch(renderView(Browser), />Install all\.\.\.<\/button>|>GitHub ↗<\/button>/);
  for (const overrides of [
    { connected: false }, { libraryLoaded: false }, { tools: [] },
    { skills: [{ ...skill, installable: false }] }, { skills: [{ ...skill, itemType: 'file' }] },
  ]) assert.match(renderView(LibraryBrowser, overrides), /class="primary" disabled="">Install all\.\.\.<\/button>/);
  assert.match(renderView(LibraryBrowser, {}, true), /class="primary" disabled="">Install all\.\.\.<\/button>/);
});

test('the HMY button confirms the exact selection and cancelling sends no command', async () => {
  const hooks = moduleUrl(`
    const values = [];
    let cursor = 0;
    export const beginRender = () => { cursor = 0; };
    export function useState(initial) {
      const index = cursor++;
      if (!(index in values)) values[index] = initial;
      return [values[index], value => { values[index] = value; }];
    }
  `);
  const { beginRender } = await import(hooks);
  const { default: Browser } = await import(moduleUrl(source
    .replace(JSON.stringify(import.meta.resolve('react')), JSON.stringify(hooks))
    .replace('useState("installed")', 'useState("library")')));
  const state = { connected: true, libraryLoaded: true, libraryCommit: 'commit', tools: [{ id: 'claude', detected: true }],
    skills: ['Alpha', 'Beta', 'Gamma'].map(name => ({ id: name, name, inLibrary: true, curated: true,
      installable: true, compatibleTools: ['claude'], occurrences: [] })) };
  const commands = [];
  const prompts = [];
  let accept = false;
  const originalWindow = globalThis.window;
  globalThis.window = { confirm: prompt => { prompts.push(prompt); return accept; } };
  function buttons(node) {
    if (Array.isArray(node)) return node.flatMap(buttons);
    if (!node?.props) return [];
    return node.type === 'button' ? [node.props] : buttons(node.props.children);
  }
  function renderButtons() {
    beginRender();
    return buttons(Browser({ state, busy: false, run: async command => { commands.push(command); } }));
  }
  const click = (label, event = {}) => renderButtons().find(button => button.children === label).onClick(event);
  const clickRow = (index, event = {}) => renderButtons().filter(button => button.className?.startsWith('hub-skill-row'))[index].onClick(event);
  try {
    click('Install all...');
    assert.match(prompts[0], /Install 3 HMY catalog items\?/);
    assert.deepEqual(commands, []);

    clickRow(0);
    clickRow(2, { ctrlKey: true });
    accept = true;
    click('Install selected...');
    assert.match(prompts[1], /Install 2 HMY catalog items\?/);
    assert.deepEqual(commands[0], { action: 'install-catalog', itemIds: ['Alpha', 'Gamma'], catalogCommit: 'commit' });

    clickRow(1);
    click('Install all...');
    assert.deepEqual(commands[1].itemIds, ['Alpha', 'Beta', 'Gamma']);
  } finally {
    if (originalWindow === undefined) delete globalThis.window;
    else globalThis.window = originalWindow;
  }
});

test('installed CLI renders Update and Uninstall without any harness or target selector', () => {
  const html = render('cli', []);
  assert.match(html, />Update<\/button>/);
  assert.match(html, />Uninstall<\/button>/);
  assert.match(html, /C:\\utility.exe/);
  assert.match(html, /Windows user/);
  assert.match(html, /Source folder/);
  assert.doesNotMatch(html, /Network folder/);
  assert.doesNotMatch(html, /Installs for your Windows user|Existing installations with the same name/);
  assert.doesNotMatch(html, /aria-label="Install target"|Browse only|No supported tools were detected/);
});

test('installed MCP renders the skills-style target selector and Update/Uninstall', () => {
  const html = render('mcp', [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }]);
  assert.match(html, />Update<\/button>/);
  assert.match(html, />Uninstall<\/button>/);
  assert.match(html, /All compatible detected tools/);
  assert.match(html, /Claude Code/);
  assert.match(html, /aria-label="Install target"/);
  assert.doesNotMatch(html, />Install target</);
  assert.doesNotMatch(html, /Browse only/);
});

test('skills show a type pill in both list entries and details, like other item types', () => {
  const tools = [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }];
  const html = render('skill', tools);
  assert.equal((html.match(/class="hub-badge">Skill<\/span>/g) ?? []).length, 2);
});

test('instruction actions omit installation guidance while file exports retain their hint', () => {
  const tools = [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }];
  const instruction = render('instruction', tools, false, LibraryBrowser);
  assert.match(instruction, />Install instruction<\/button>/);
  assert.doesNotMatch(instruction, /Install in all detected tools|Existing guidance is preserved|Start a new conversation/);
  const file = render('file', tools, false, LibraryBrowser);
  assert.match(file, /class="hub-helper"/);
});

for (const type of ['mcp', 'cli']) test(`missing ${type} renders Install rather than Update`, () => {
  const tools = type === 'cli' ? [] : [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }];
  const html = render(type, tools, false, LibraryBrowser);
  assert.match(html, /class="primary">Install<\/button>/);
  assert.doesNotMatch(html, />Update<\/button>|>Uninstall<\/button>|Browse only/);
});

test('non-HMY installed MCP offers Uninstall without a catalog or repository connection', () => {
  const tools = [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }];
  const html = render('mcp', tools, true, HubSkillBrowser, false);
  assert.match(html, /class="secondary">Uninstall<\/button>/);
  assert.doesNotMatch(html, />Install<\/button>|>Update<\/button>|Source folder/);
  assert.doesNotMatch(html, /identifiable application files|Shared runtimes and unrelated files|Tool logos show item status/);
  assert.match(html, /aria-label="Install target"/);
});
