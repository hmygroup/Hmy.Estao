import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/HubSkillBrowser.tsx', import.meta.url), 'utf8');
const css = readFileSync(new URL('../src/harness-hub.css', import.meta.url), 'utf8');

test('installation controls sit outside the scrollable skill details', () => {
  assert.match(source, /className="hub-skill-details-content"/);
  assert.match(source, /<\/div>\s*<div className="hub-skill-details-footer">\s*\{\(isSkill \|\| itemType\(skill\) === "mcp"\) && skill.inLibrary && skill.installable/);
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
  assert.doesNotMatch(details, /hub\.snapshot|hub\.origin_hmy/);
  assert.match(details, /isNetworkItem && skill\.source && <p className="hub-network-path">.*\{skill\.source\}/);
  assert.match(details, /: isNetworkItem \? <>/);
  assert.doesNotMatch(details, /hub\.browse_only_hint/);
  assert.match(details, /!isCurated\(skill\) && skill.inLibrary && <small>/);
});

test('skill browser omits the shared-folder explanation and local skill label', () => {
  assert.doesNotMatch(source, /hub\.review_skill_hint|hub\.origin_local/);
});

test('refresh uses the activity slot while idle and preserves busy cancellation', () => {
  const slot = source.slice(source.indexOf('className="hub-activity-slot"'), source.indexOf('{completedMessage &&'));
  assert.match(slot, /!busy && <button[^>]*hub-refresh[^>]*aria-label=\{t\("hub.refresh"\)\}/);
  assert.match(slot, /action: "check"/);
  assert.match(slot, /busy && <span className="hub-activity"/);
  assert.match(slot, /action: "cancel"/);
});

test('installed skills uninstall directly without confirmation or repository access', () => {
  assert.match(source, /isSkill && skill\.occurrences\.length > 0 && <UninstallSkill/);
  const uninstall = source.slice(source.indexOf('function UninstallSkill('));
  assert.doesNotMatch(uninstall, /confirm|setConfirming/);
  assert.match(uninstall, /action: "uninstall-skill", skillId: skill.id/);
  assert.match(uninstall, /disabled=\{busy \|\| !canUninstall\}/);
  assert.doesNotMatch(uninstall, /connected|harnessId|item\.path/);
});

test('install and uninstall share equal-width controls, with target for skills and MCPs but not CLIs', () => {
  assert.match(css, /\.hub-skill-actions\{[^}]*grid-template-columns:repeat\(2,minmax\(0,1fr\)\)/);
  assert.match(css, /\.hub-skill-actions>button:only-of-type\{grid-column:1\/-1\}/);
  assert.match(source, /\(isSkill \|\| itemType\(skill\) === "mcp"\) && skill.inLibrary && skill.installable && <div className="hub-skill-install">\s*<label>\{t\("hub.install_target"\)\}/);
  const actions = source.slice(source.indexOf('className="hub-skill-actions"'));
  assert.match(actions, /skill.inLibrary && skill.installable && <button type="button" className="primary"/);
  const uninstall = source.slice(source.indexOf('function UninstallSkill('));
  assert.match(uninstall, /<button type="button" className="secondary"[^>]*onClick=\{\(\) => void run\(\{ action: "uninstall-skill", skillId: skill.id \}\)\}/);
  assert.doesNotMatch(uninstall, /className="primary"/);
  assert.match(css, /\.hub-skill-actions \.secondary\{background:transparent;box-shadow:none\}/);
  assert.doesNotMatch(uninstall, /install_target/);
});

test('MCP and CLI packages have install/update and confirmed uninstall actions', () => {
  assert.match(source, /action: "install-package", skillId: skill.id/);
  assert.match(source, /t\(installed \? "hub.update_package" : "hub.install_package"\)/);
  assert.match(source, /window.confirm\(t\("hub.package_uninstall_confirm", skill.name\)\)/);
  assert.match(source, /action: "uninstall-package", skillId: skill.id/);
  assert.match(source, /!isCli && effectiveTarget \? \{ harnessId: effectiveTarget \}/);
  assert.match(source, /hub.cli_global_hint/);
});

test('installed skills use the update label without changing the install command', () => {
  assert.match(source, /t\(skill.occurrences.length > 0 \? "hub.update_skill" : "hub.install_skill"\)/);
  assert.match(source, /action: "install-skill", skillId: skill.id/);
});

test('instruction pill shares the category group and install sits beside export', () => {
  const details = source.slice(source.indexOf('function SkillDetails('));
  const heading = details.slice(details.indexOf('className="hub-item-categories"'), details.indexOf('<p>{description'));
  assert.match(heading, /hub\.type\.\$\{itemType\(skill\)\}/);
  assert.doesNotMatch(details, /hub-item-type/);
  assert.match(details, /className="hub-skill-actions">\s*<button[\s\S]*?action: "export-item"[\s\S]*?itemType\(skill\) === "instruction" && <button[\s\S]*?action: "install-instruction"/);
  assert.match(details, /disabled=\{busy \|\| !connected \|\| !skill.installable \|\| !tools.length\}/);
});
