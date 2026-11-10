import { useId } from "react";
import type { ChartSeries, UsageChart as ChartData } from "./types";
import { date, dateTime, message, percent, t, time } from "./i18n";
import { chartCoordinates } from "./usageChartGeometry";
import "./usage-chart.css";

const palette = ["var(--accent)", "var(--muted)", "var(--text)", "color-mix(in srgb, var(--accent), var(--text))"];
function seriesColor(series: ChartSeries) {
  return series.kind === "target" ? "var(--warning-text)" : series.kind === "projection" ? "var(--text)" : palette[series.colorIndex % palette.length];
}

export default function UsageChart({ chart }: { chart: ChartData }) {
  const id = useId();
  const start = Date.parse(chart.startAt), end = Date.parse(chart.endAt);
  const observed = Date.parse(chart.observedAt);
  const now = Date.parse(chart.nowAt);
  const nowX = 12 + (now - start) / (end - start) * 332;
  const hasData = chart.series.some(s => s.kind === "usage" && s.points.length > 0);
  const forecast = chart.forecast.unavailable ? t("chart.forecast_unavailable") : chart.forecast.at
    ? t(chart.forecast.resetOccursFirst ? "chart.until_reset" : "chart.runs_out", message(chart.forecast.label), dateTime(chart.forecast.at))
    : t("chart.no_trend", message(chart.forecast.label));
  const axisTime = (timestamp: number) => end - start <= 86_400_000
    ? time(new Date(timestamp).toISOString()) : date(timestamp, false);
  return <section className="usage-history" aria-labelledby={`${id}-heading`}>
    <header><h3 id={`${id}-heading`}>{t("chart.title")}</h3><small>{chart.range}</small></header>
    <p className="chart-subtitle">{t("chart.local")}</p>
    <p className="chart-forecast">{forecast}</p>
    <ul className="chart-legend">{chart.series.filter(s => s.points.length > 0).map((series, index) =>
      <li key={index}><svg width="18" height="10" aria-hidden="true"><line x1="0" y1="5" x2="18" y2="5" stroke={seriesColor(series)} strokeWidth="2"
        strokeDasharray={series.kind === "target" ? "2 3" : series.kind === "projection" ? "6 3" : undefined} /></svg>{message(series.label)}</li>)}</ul>
    <svg className="usage-history-plot" viewBox="0 0 400 188" role="img" aria-labelledby={`${id}-title ${id}-description`}>
      <title id={`${id}-title`}>{t("chart.title")} · {chart.range}</title>
      <desc id={`${id}-description`}>{forecast}. {t("chart.range", dateTime(chart.startAt), dateTime(chart.endAt))}</desc>
      <defs><clipPath id={`${id}-clip`}><rect x="10" y="15" width="336" height="137" /></clipPath></defs>
      {[0, .5, 1].map(level => <g key={level}>
        <line className="chart-grid" x1="12" x2="344" y1={150 - level * 132} y2={150 - level * 132} />
        <text x="389" y={154 - level * 132} textAnchor="end">{percent(level)}</text>
      </g>)}
      {now > start && now < end && <g className="chart-now">
        <line x1={nowX} x2={nowX} y1="18" y2="150" />
        <text x={Math.min(326, Math.max(30, nowX))} y="11" textAnchor="middle">{t("chart.now")}</text>
      </g>}
      <g clipPath={`url(#${id}-clip)`}>{chart.series.map((series, index) => {
        const coords = chartCoordinates(series.points, start, end, series.kind === "usage" ? observed : end);
        if (!coords.length) return null;
        const path = coords.map(p => `${p.x},${p.y}`).join(" ");
        const color = seriesColor(series);
        return <g key={index}>
          {series.kind === "usage" && coords.length > 1 && <polygon points={`${coords[0].x},150 ${path} ${coords.at(-1)!.x},150`} fill={color} opacity=".1" />}
          <polyline points={path} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round"
            strokeDasharray={series.kind === "target" ? "2 4" : series.kind === "projection" ? "6 4" : undefined} />
          {series.kind !== "target" && coords.map((p, pointIndex) => <circle key={pointIndex} cx={p.x} cy={p.y} r="3"
            className={pointIndex === coords.length - 1 ? "chart-endpoint" : "chart-sample"}
            fill={color}><title>{message(series.label)} · {dateTime(p.timestamp)} · {percent(p.value)}</title></circle>)}
        </g>;
      })}</g>
      {!hasData && <text x="178" y="84" textAnchor="middle">{t("chart.collecting")}</text>}
      {[start, start + (end - start) / 2, end].map((stamp, index) =>
        <text key={index} x={12 + index * 166} y="177" textAnchor={index === 0 ? "start" : index === 2 ? "end" : "middle"}>{axisTime(stamp)}</text>)}
    </svg>
  </section>;
}
