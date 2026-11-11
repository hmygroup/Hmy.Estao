import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/HubSkillBrowser.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/harness-hub.css', import.meta.url), 'utf8');

test('installation controls sit outside the scrollable skill details', () => {
  assert.match(source, /className="hub-skill-details-content"/);
  assert.match(source, /<\/div>\s*<div className="hub-skill-details-footer">\s*\{isSkill && skill.inLibrary && skill.installable/);
  assert.match(css, /\.hub-skill-details-content\{[^}]*min-height:0;[^}]*overflow:auto/);
  assert.match(css, /\.hub-skill-details-footer\{[^}]*flex-shrink:0/);
  assert.doesNotMatch(css, /\.hub-skill-details\{[^}]*overflow:(auto|visible)/);
});

test('installed location stays inline and omits redundant labels', () => {
  assert.match(css, /\.hub-occurrence code\{[^}]*display:inline/);
  assert.match(source, /status !== "present" && <small>/);
  assert.doesNotMatch(source, /hub\.shared_folder/);
});

test('details put categories beside the name and omit repository metadata', () => {
  const details = source.slice(source.indexOf('function SkillDetails('));
  assert.match(details, /className="hub-skill-details-heading">\s*<h3>\{skill.name\}<\/h3>\s*<div className="hub-item-categories">/);
  assert.match(css, /\.hub-item-categories\{[^}]*margin-left:auto/);
  assert.doesNotMatch(details, /hub\.snapshot|skill\.source|hub\.origin_hmy/);
  assert.match(details, /!isCurated\(skill\) && skill.inLibrary && <small>/);
});

test('skill browser omits the shared-folder explanation and local skill label', () => {
  assert.doesNotMatch(source, /hub\.review_skill_hint|hub\.origin_local/);
});
