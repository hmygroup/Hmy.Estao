import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import ts from 'typescript';

const en = JSON.parse(readFileSync(new URL('../src/locales/en.json', import.meta.url), 'utf8'));
const es = JSON.parse(readFileSync(new URL('../src/locales/es.json', import.meta.url), 'utf8'));
const branding = JSON.parse(readFileSync(new URL('../src/branding.json', import.meta.url), 'utf8'));
const placeholders = value => [...value.matchAll(/\{(?:\d+|appName)\}/g)].map(m => m[0]).sort();

test('catalogs have identical keys and interpolation arguments', () => {
  assert.deepEqual(Object.keys(en).sort(), Object.keys(es).sort());
  for (const key of Object.keys(en)) {
    assert.ok(es[key].trim(), key);
    assert.deepEqual(placeholders(en[key]), placeholders(es[key]), key);
  }
});

test('literal frontend translation keys and all theme names have translations', () => {
  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
      if (entry.isDirectory()) visit(file);
      else if (/\.tsx?$/.test(entry.name)) {
        const text = readFileSync(file, 'utf8');
        for (const match of text.matchAll(/\bt\("([\w.-]+)"/g)) assert.ok(match[1] in en, `${entry.name}: ${match[1]}`);
      }
    }
  }
  visit(new URL('../src/', import.meta.url));
  const themes = JSON.parse(readFileSync(new URL('../src/themes/themes.json', import.meta.url), 'utf8'));
  for (const theme of themes) {
    assert.ok(`theme.${theme.id}` in en);
    assert.ok(`theme.${theme.id}.description` in en);
  }
});

Object.defineProperty(globalThis, 'navigator', { value: { language: 'en-US' }, configurable: true });
globalThis.document = { documentElement: { lang: '' } };
globalThis.localStorage = { getItem: () => { throw new Error('Storage disabled'); }, setItem: () => { throw new Error('Storage disabled'); } };
const source = readFileSync(new URL('../src/i18n.ts', import.meta.url), 'utf8')
  .replace('import en from "./locales/en.json";', `const en = ${JSON.stringify(en)};`)
  .replace('import es from "./locales/es.json";', `const es = ${JSON.stringify(es)};`)
  .replace('import { appName } from "./branding";', `const appName = ${JSON.stringify(branding.displayName)};`);
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const i18n = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));

test('regional system languages resolve and unsupported languages fall back to English', () => {
  assert.equal(i18n.resolveLanguage('system', 'es-MX'), 'es');
  assert.equal(i18n.resolveLanguage('en-GB', 'es-ES'), 'en');
  assert.equal(i18n.resolveLanguage('system', 'de-DE'), 'en');
});
test('frontend works without native hosting or browser storage', () => {
  i18n.setLanguage('es');
  assert.equal(document.documentElement.lang, 'es');
  assert.equal(i18n.t('settings'), 'Configuración');
  assert.equal(i18n.t('startup.enable'), `Iniciar ${branding.displayName} al entrar en Windows`);
});
test('plural forms and numeric formatting use the selected locale', () => {
  i18n.setLanguage('es');
  assert.equal(i18n.plural('minutes', 1), '1 minuto');
  assert.equal(i18n.plural('minutes', 5), '5 minutos');
  assert.equal(i18n.number(1234.5), new Intl.NumberFormat('es').format(1234.5));
  assert.equal(i18n.percent(.42), new Intl.NumberFormat('es', { style: 'percent', maximumFractionDigits: 0 }).format(.42));
});
test('structured messages translate data and nested errors without parsing English diagnostics', () => {
  i18n.setLanguage('es');
  assert.equal(i18n.message({ code: 'error.http', arguments: ['Claude', 401] }), 'Claude devolvió el código HTTP 401.');
  assert.equal(i18n.message({ code: 'signin.failed', arguments: [{ code: 'signin.rejected', arguments: [] }] }), `${es['signin.failed'].replace('{0}', es['signin.rejected'])}`);
  assert.equal(i18n.errorText(new Error('secret-server-message')), es['error.operation']);
  assert.equal(i18n.errorText({ userMessage: { code: 'hub.review_changed', arguments: [] } }), es['hub.review_changed']);
  assert.equal(i18n.t('unknown.server.code'), es['error.operation']);
  assert.equal(i18n.t('window.named', '{appName} {0}'), '{appName} {0}');
  assert.equal(i18n.message({ code: 'message.list', arguments: [{ code: 'config.version', arguments: [2] }, { code: 'config.provider', arguments: ['example'] }] }),
    `${es['config.version'].replace('{0}', '2')}\n${es['config.provider'].replace('{0}', 'example')}`);
});
