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
// Render the library's initial state as well as the default Installed view.
const { default: LibraryBrowser } = await import(moduleUrl(source.replace('useState("installed")', 'useState("library")')));

const render = (itemType, tools, installed = true, Browser = HubSkillBrowser, curated = true) => renderToStaticMarkup(React.createElement(Browser, {
  state: { busy: false, tools, skills: [{ id: 'utility', name: 'Utility', description: 'Example',
    inLibrary: curated, curated, installable: curated, itemType, source: curated ? 'C:\\packages\\utility' : null, compatibleTools: tools.map(tool => tool.id),
    occurrences: installed ? [{ toolId: itemType === 'cli' ? 'global' : 'claude', path: 'C:\\utility.exe', shared: itemType === 'cli', hash: null }] : [],
  }] }, busy: false, run: async () => {},
}));

test('installed CLI renders Update and Uninstall without any harness or target selector', () => {
  const html = render('cli', []);
  assert.match(html, />Update<\/button>/);
  assert.match(html, />Uninstall<\/button>/);
  assert.match(html, /C:\\utility.exe/);
  assert.match(html, /Windows user/);
  assert.match(html, /Source folder/);
  assert.doesNotMatch(html, /Network folder/);
  assert.doesNotMatch(html, /aria-label="Install target"|Browse only|No supported tools were detected/);
});

test('installed MCP renders the skills-style target selector and Update/Uninstall', () => {
  const html = render('mcp', [{ id: 'claude', name: 'Claude Code', detected: true, status: '', error: null }]);
  assert.match(html, />Update<\/button>/);
  assert.match(html, />Uninstall<\/button>/);
  assert.match(html, /All compatible detected tools/);
  assert.match(html, /Claude Code/);
  assert.doesNotMatch(html, /Browse only/);
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
  assert.match(html, /identifiable application files/);
  assert.match(html, /aria-label="Install target"/);
});
