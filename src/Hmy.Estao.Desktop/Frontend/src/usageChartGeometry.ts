import type { ChartPoint } from "./types";

// Keep one real sample per horizontal pixel; never interpolate extra history.
export function chartCoordinates(points: ChartPoint[], start: number, end: number, observed: number) {
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return [];
  const columns = new Map<number, ChartPoint & { x: number; y: number }>();
  for (const point of points.toSorted((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp))) {
    const timestamp = Date.parse(point.timestamp);
    if (!Number.isFinite(timestamp) || !Number.isFinite(point.value) || timestamp < start || timestamp > Math.min(end, observed)) continue;
    const fraction = (timestamp - start) / (end - start);
    const value = Math.max(0, Math.min(1, point.value));
    columns.set(Math.floor(fraction * 332), { ...point, value, x: 12 + fraction * 332, y: 150 - value * 132 });
  }
  return [...columns.values()];
}
