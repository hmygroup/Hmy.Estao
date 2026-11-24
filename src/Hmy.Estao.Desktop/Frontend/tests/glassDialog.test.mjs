import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

const dialog = readFileSync(new URL('../src/GlassDialog.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/liquid-glass.css', import.meta.url), 'utf8');

test('the frontend never opens WebView2 native script dialogs', () => {
  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
      if (entry.isDirectory()) visit(file);
      else if (/\.tsx?$/.test(entry.name)) {
        const code = readFileSync(file, 'utf8').replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, '');
        assert.doesNotMatch(code, /\b(?:window\.)?(?:alert|confirm|prompt)\s*\(/, entry.name);
      }
    }
  }
  visit(new URL('../src/', import.meta.url));
});

test('the glass dialog is a modal, labelled, keyboard-dismissable <dialog>', () => {
  assert.match(dialog, /element\.showModal\(\)/);
  assert.match(dialog, /aria-labelledby=\{titleId\} aria-describedby=\{messageId\}/);
  assert.match(dialog, /onCancel=\{event => \{ event\.preventDefault\(\); close\(false\); \}\}/);
  assert.match(dialog, /returnFocus\?\.isConnected\) returnFocus\.focus/);
  // Destructive confirmations start on Cancel; others on the primary action.
  assert.match(dialog, /className="secondary" autoFocus=\{tone === "danger"\}/);
  assert.match(dialog, /autoFocus=\{tone !== "danger"\}/);
});

test('the glass dialog uses theme tokens and honours accessibility media queries', () => {
  assert.match(css, /\.glass-dialog-panel \{[^}]*var\(--glass-edge\)/);
  for (const query of ['prefers-reduced-motion: reduce', 'prefers-reduced-transparency: reduce', 'forced-colors: active'])
    assert.ok(css.lastIndexOf(`@media (${query})`) > css.indexOf('.glass-dialog {'), query);
});
