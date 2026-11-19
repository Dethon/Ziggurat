import { useState, type PointerEvent } from "react";
import { chartTop, curveSegments, formatGlucose, LOWEST, UNIT_LABEL } from "../glucose";
import { formatShortDay, formatTime, locale } from "../format";
import { useSettings } from "../settings";
import type { GlucoseCurve, GlucoseUnit } from "../types";

// Drawn by hand, as every picture in the app is: one line, her target band behind it, the band's edges
// and the ends of the time named. A touch or a pointer along it reads the nearest point.
const WIDTH = 320;
const HEIGHT = 180;
const PLOT = { left: 34, right: 8, top: 8, bottom: 22 };
const PLOT_WIDTH = WIDTH - PLOT.left - PLOT.right;
const PLOT_HEIGHT = HEIGHT - PLOT.top - PLOT.bottom;

export function GlucoseChart({
  curve,
  unit,
  targetLow,
  targetHigh,
  label,
}: {
  curve: GlucoseCurve;
  unit: GlucoseUnit;
  targetLow: number;
  targetHigh: number;
  label: string;
}) {
  const { language } = useSettings();
  const [hovered, setHovered] = useState<number | null>(null);
  const from = Date.parse(curve.from);
  const to = Date.parse(curve.to);
  const frame = { from, to, width: PLOT_WIDTH, height: PLOT_HEIGHT, low: LOWEST, high: chartTop(curve.points) };
  const y = (mgPerDl: number) => ((frame.high - mgPerDl) / (frame.high - frame.low)) * PLOT_HEIGHT;
  const x = (at: number) => ((at - from) / (to - from)) * PLOT_WIDTH;
  const days = (to - from) / 86_400_000;
  const tick = (at: number) =>
    days <= 2
      ? formatTime(new Date(at).toISOString(), language)
      : new Date(at).toLocaleDateString(locale(language), { day: "numeric", month: "short" });
  const ticks = [1, 2, 3].map((quarter) => from + ((to - from) * quarter) / 4);

  function point(event: PointerEvent<SVGSVGElement>) {
    const box = event.currentTarget.getBoundingClientRect();
    const at = from + (((event.clientX - box.left) / box.width) * WIDTH - PLOT.left) / PLOT_WIDTH * (to - from);
    const nearest = curve.points.reduce(
      (best, p, index) => (Math.abs(Date.parse(p.at) - at) < Math.abs(Date.parse(curve.points[best].at) - at) ? index : best),
      0,
    );
    setHovered(nearest);
  }

  const shown = hovered === null ? null : curve.points[hovered];
  return (
    <div className="glucose-chart">
      <p className="glucose-readout" aria-hidden="true">
        {shown && (
          <>
            <strong>{formatGlucose(shown.mgPerDl, unit, language)}</strong> {UNIT_LABEL[unit]} ·{" "}
            {days <= 2 ? formatTime(shown.at, language) : `${formatShortDay(shown.at, language)} ${formatTime(shown.at, language)}`}
          </>
        )}
      </p>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={label}
        onPointerMove={point}
        onPointerDown={point}
        onPointerLeave={() => setHovered(null)}
      >
        <g transform={`translate(${PLOT.left} ${PLOT.top})`}>
          <rect
            className="glucose-chart-band"
            x={0}
            y={y(targetHigh)}
            width={PLOT_WIDTH}
            height={y(targetLow) - y(targetHigh)}
          />
          {[targetLow, targetHigh].map((edge) => (
            <g key={edge}>
              <line className="glucose-chart-edge" x1={0} x2={PLOT_WIDTH} y1={y(edge)} y2={y(edge)} />
              <text className="glucose-chart-label" x={-6} y={y(edge)} dy="0.35em" textAnchor="end">
                {formatGlucose(edge, unit, language)}
              </text>
            </g>
          ))}
          <text className="glucose-chart-label" x={-6} y={0} dy="0.7em" textAnchor="end">
            {formatGlucose(frame.high, unit, language)}
          </text>
          <line className="glucose-chart-axis" x1={0} x2={PLOT_WIDTH} y1={PLOT_HEIGHT} y2={PLOT_HEIGHT} />
          {ticks.map((at) => (
            <text key={at} className="glucose-chart-label" x={x(at)} y={PLOT_HEIGHT + 16} textAnchor="middle">
              {tick(at)}
            </text>
          ))}
          {curveSegments(curve.points, curve.bucketMinutes, frame).map((segment, index) => (
            <path key={index} className="glucose-chart-line" d={segment} />
          ))}
          {shown && (
            <g className="glucose-chart-hover">
              <line x1={x(Date.parse(shown.at))} x2={x(Date.parse(shown.at))} y1={0} y2={PLOT_HEIGHT} />
              <circle cx={x(Date.parse(shown.at))} cy={y(Math.min(frame.high, Math.max(frame.low, shown.mgPerDl)))} r={4.5} />
            </g>
          )}
        </g>
      </svg>
    </div>
  );
}
