import { formatDays, formatUnits } from "../format";
import { plotted, UNIT } from "../profile";
import { useSettings } from "../settings";
import type { Measurement, MeasurementKind } from "../types";

// Drawn by hand, as every picture in the app is: one kind's history as a line through its days, the
// lowest and highest value named on the left, the first and last day below. Each point is a button:
// tapping one opens it to correct or delete, with a target wider than the dot.
const WIDTH = 320;
const HEIGHT = 120;
const PLOT = { left: 40, right: 14, top: 12, bottom: 22 };
const PLOT_WIDTH = WIDTH - PLOT.left - PLOT.right;
const PLOT_HEIGHT = HEIGHT - PLOT.top - PLOT.bottom;

export function MeasurementChart({
  kind,
  measurements,
  label,
  onOpen,
}: {
  kind: MeasurementKind;
  measurements: readonly Measurement[];
  label: string;
  onOpen: (measurement: Measurement) => void;
}) {
  const { t, language } = useSettings();
  const { points } = plotted(measurements, { width: PLOT_WIDTH, height: PLOT_HEIGHT });
  const values = measurements.map((m) => m.value);
  const [least, most] = [Math.min(...values), Math.max(...values)];
  const spot = (value: number) => points.find((p) => p.measurement.value === value)!.y;
  const thisYear = new Date().getFullYear().toString();
  const day = (on: string) => formatDays(on, on, language, thisYear);
  const shown = (m: Measurement) => `${formatUnits(m.value, language)} ${UNIT[kind]}`;
  const [first, last] = [points[0], points[points.length - 1]];

  return (
    <svg className="measurement-chart" viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="group" aria-label={label}>
      <g transform={`translate(${PLOT.left} ${PLOT.top})`}>
        {[least, most]
          .filter((value, index) => index === 0 || value !== least)
          .map((value) => (
            <g key={value}>
              <line className="measurement-chart-guide" x1={0} x2={PLOT_WIDTH} y1={spot(value)} y2={spot(value)} />
              <text className="glucose-chart-label" x={-8} y={spot(value)} dy="0.35em" textAnchor="end">
                {formatUnits(value, language)}
              </text>
            </g>
          ))}
        <line className="glucose-chart-axis" x1={0} x2={PLOT_WIDTH} y1={PLOT_HEIGHT} y2={PLOT_HEIGHT} />
        <text className="glucose-chart-label" x={first.x} y={PLOT_HEIGHT + 16} textAnchor={points.length > 1 ? "start" : "middle"}>
          {day(first.measurement.on)}
        </text>
        {points.length > 1 && (
          <text className="glucose-chart-label" x={last.x} y={PLOT_HEIGHT + 16} textAnchor="end">
            {day(last.measurement.on)}
          </text>
        )}
        {points.length > 1 && (
          <path className="glucose-chart-line" d={points.map((p, i) => `${i === 0 ? "M" : "L"}${p.x} ${p.y}`).join(" ")} />
        )}
        {points.map((p) => (
          <g
            key={p.measurement.id}
            className="measurement-point"
            role="button"
            tabIndex={0}
            aria-label={`${t(`measurementEdit_${kind}`)}: ${shown(p.measurement)}, ${day(p.measurement.on)}`}
            onClick={() => onOpen(p.measurement)}
            onKeyDown={(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                onOpen(p.measurement);
              }
            }}
          >
            <circle className="measurement-point-target" cx={p.x} cy={p.y} r={16} />
            <circle className="measurement-point-dot" cx={p.x} cy={p.y} r={4.5} />
          </g>
        ))}
      </g>
    </svg>
  );
}
