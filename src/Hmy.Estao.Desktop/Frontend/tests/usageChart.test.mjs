import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

const source = readFileSync(new URL('../src/usageChartGeometry.ts', import.meta.url), 'utf8');
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { chartCoordinates } = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
const start = Date.parse('2026-11-10T08:00Z'), end = start + 5 * 3600_000;
const point = (hours, value) => ({ timestamp: new Date(start + hours * 3600_000).toISOString(), value });

test('popover actions are inside the scrollable content, after the charts', () => {
  const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
  const content = app.match(/<main className="popover-content">([\s\S]*?)<\/main>/)?.[1];
  assert.ok(content?.includes('<nav className="popover-actions"'));
  assert.ok(content.indexOf('popover-actions') > content.indexOf('<UsageChart'));
  for (const action of ['usage-dashboard', 'status-page', 'settings', 'quit']) assert.ok(content.includes(`run("${action}"`));
});

test('samples use the whole cycle scale instead of stretching first and last readings across the chart', () => {
  const coords = chartCoordinates([point(2, .2), point(3, .4)], start, end, end);
  assert.equal(coords[0].x, 12 + 2 / 5 * 332);
  assert.equal(coords[1].x, 12 + 3 / 5 * 332);
  assert.equal(coords[1].y, 150 - .4 * 132);
});
test('history stops at the observation and future projections can continue to reset', () => {
  const points = [point(1, .1), point(3, .3), point(5, .5)];
  assert.equal(chartCoordinates(points, start, end, start + 3 * 3600_000).length, 2);
  assert.equal(chartCoordinates(points, start, end, end).length, 3);
});
test('invalid and out-of-cycle samples are ignored, real zero retained, percentages clamped', () => {
  const points = [point(-1, .2), point(1, 0), point(2, Infinity), point(3, 2), point(6, .5)];
  const coords = chartCoordinates(points, start, end, end);
  assert.deepEqual(coords.map(p => p.value), [0, 1]);
  assert.deepEqual(chartCoordinates(points, end, start, end), []);
});
test('dense history is bounded to chart pixel columns without mutating the input', () => {
  const points = Array.from({ length: 10_000 }, (_, i) => point(i / 2000, i / 10_000));
  const coords = chartCoordinates(points, start, end, end);
  assert.ok(coords.length <= 333);
  assert.equal(coords.at(-1).value, points.at(-1).value);
  assert.equal(points.length, 10_000);
});
