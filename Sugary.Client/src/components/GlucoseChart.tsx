import { useRef, useState, type PointerEvent } from "react";
import { chartBox, useWidth } from "../chart";
import { useWide } from "../wide";
import { chartTop, curveSegments, formatGlucose, LOWEST, UNIT_LABEL } from "../glucose";
import { formatShortDay, formatTime, locale } from "../format";
import { useSettings } from "../settings";
import type { GlucoseCurve, GlucoseUnit } from "../types";

// Drawn by hand, as every picture in the app is: one line, her target band behind it, the band's edges
// and the ends of the time named. A touch or a pointer along it reads the nearest point. Drawn for a
// phone's card, and drawn wider rather than larger on a desktop's (chartBox).
const DRAWN = { width: 320, height: 180 };
const WIDE_TALLEST = 2.2;
const PLOT = { left: 34, right: 8, top: 8, bottom: 22 };

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
  const card = useRef<HTMLDivElement>(null);
  // A wide window gives the curve the room the glucose screen has under it, so its swings read.
  const { width, height } = chartBox(useWidth(card), DRAWN, useWide() ? WIDE_TALLEST : undefined);
  const plotWidth = width - PLOT.left - PLOT.right;
  const plotHeight = height - PLOT.top - PLOT.bottom;
  const from = Date.parse(curve.from);
  const to = Date.parse(curve.to);
  const frame = { from, to, width: plotWidth, height: plotHeight, low: LOWEST, high: chartTop(curve.points) };
  const y = (mgPerDl: number) => ((frame.high - mgPerDl) / (frame.high - frame.low)) * plotHeight;
  const x = (at: number) => ((at - from) / (to - from)) * plotWidth;
  const days = (to - from) / 86_400_000;
  const tick = (at: number) =>
    days <= 2
      ? formatTime(new Date(at).toISOString(), language)
      : new Date(at).toLocaleDateString(locale(language), { day: "numeric", month: "short" });
  // A wide chart names more of its times, about as far apart as a phone's three.
  const parts = width > 480 ? 6 : 4;
  const ticks = Array.from({ length: parts - 1 }, (_, part) => from + ((to - from) * (part + 1)) / parts);

  function point(event: PointerEvent<SVGSVGElement>) {
    const box = event.currentTarget.getBoundingClientRect();
    const at = from + (((event.clientX - box.left) / box.width) * width - PLOT.left) / plotWidth * (to - from);
    const nearest = curve.points.reduce(
      (best, p, index) => (Math.abs(Date.parse(p.at) - at) < Math.abs(Date.parse(curve.points[best].at) - at) ? index : best),
      0,
    );
    setHovered(nearest);
  }

  const shown = hovered === null ? null : curve.points[hovered];
  return (
    <div ref={card} className="glucose-chart">
      <p className="glucose-readout" aria-hidden="true">
        {shown && (
          <>
            <strong>{formatGlucose(shown.mgPerDl, unit, language)}</strong> {UNIT_LABEL[unit]} ·{" "}
            {days <= 2 ? formatTime(shown.at, language) : `${formatShortDay(shown.at, language)} ${formatTime(shown.at, language)}`}
          </>
        )}
      </p>
      <svg
        viewBox={`0 0 ${width} ${height}`}
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
            width={plotWidth}
            height={y(targetLow) - y(targetHigh)}
          />
          {[targetLow, targetHigh].map((edge) => (
            <g key={edge}>
              <line className="glucose-chart-edge" x1={0} x2={plotWidth} y1={y(edge)} y2={y(edge)} />
              <text className="glucose-chart-label" x={-6} y={y(edge)} dy="0.35em" textAnchor="end">
                {formatGlucose(edge, unit, language)}
              </text>
            </g>
          ))}
          <text className="glucose-chart-label" x={-6} y={0} dy="0.7em" textAnchor="end">
            {formatGlucose(frame.high, unit, language)}
          </text>
          <line className="glucose-chart-axis" x1={0} x2={plotWidth} y1={plotHeight} y2={plotHeight} />
          {ticks.map((at) => (
            <text key={at} className="glucose-chart-label" x={x(at)} y={plotHeight + 16} textAnchor="middle">
              {tick(at)}
            </text>
          ))}
          {curveSegments(curve.points, curve.bucketMinutes, frame).map((segment, index) => (
            <path key={index} className="glucose-chart-line" d={segment} />
          ))}
          {shown && (
            <g className="glucose-chart-hover">
              <line x1={x(Date.parse(shown.at))} x2={x(Date.parse(shown.at))} y1={0} y2={plotHeight} />
              <circle cx={x(Date.parse(shown.at))} cy={y(Math.min(frame.high, Math.max(frame.low, shown.mgPerDl)))} r={4.5} />
            </g>
          )}
        </g>
      </svg>
    </div>
  );
}
