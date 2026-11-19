import { Icon } from "../components/Icon";
import { MeasurementChart } from "../components/MeasurementChart";
import { Sheet } from "../components/Sheet";
import { formatDays, formatUnits } from "../format";
import { charted, HISTORY_RANGES, UNIT, within, type HistoryRange } from "../profile";
import { useSettings } from "../settings";
import type { Measurement, MeasurementKind } from "../types";

// One kind's whole history, away from Settings so years of entries never stretch that page: a year, five
// or all of them as a chart and a list, either opening an entry to correct or delete.
export function MeasurementHistorySheet({
  kind,
  past,
  range,
  today,
  onRange,
  onOpen,
  onAdd,
  onClose,
}: {
  kind: MeasurementKind;
  past: readonly Measurement[];
  range: HistoryRange;
  today: string;
  onRange: (range: HistoryRange) => void;
  // Neither, for somebody else's history: read, never corrected or added to.
  onOpen?: (measurement: Measurement) => void;
  onAdd?: () => void;
  onClose: () => void;
}) {
  const { t, language } = useSettings();
  const thisYear = today.slice(0, 4);
  const day = (on: string) => formatDays(on, on, language, thisYear);
  const shown = within(past, range, today);

  return (
    <Sheet
      title={t(`measurementHistoryTitle_${kind}`)}
      onClose={onClose}
      footer={
        onAdd && (
          <button type="button" className="button button-primary button-big" onClick={onAdd}>
            <Icon name="plus" size={20} /> {t(`measurementAdd_${kind}`)}
          </button>
        )
      }
    >
      <div className="segmented" role="group" aria-label={t("historyRanges")}>
        {HISTORY_RANGES.map((option) => (
          <button
            key={option}
            type="button"
            className={option === range ? "segment segment-on" : "segment"}
            aria-pressed={option === range}
            onClick={() => onRange(option)}
          >
            {t(`historyRange_${option}`)}
          </button>
        ))}
      </div>
      {charted(shown) && (
        <MeasurementChart kind={kind} measurements={shown} today={today} onOpen={onOpen} />
      )}
      {shown.length === 0 ? (
        <p className="muted small">{t("measurementNoneInRange")}</p>
      ) : (
        <ul className="rows">
          {shown.map((m) => (
            <li key={m.id}>
              {onOpen ? (
                <button
                  type="button"
                  className="row-link period-row measurement-row"
                  aria-haspopup="dialog"
                  aria-label={`${t(`measurementEdit_${m.kind}`)}, ${day(m.on)}`}
                  onClick={() => onOpen(m)}
                >
                  <span className="period-row-text">
                    <span className="item-name">{`${formatUnits(m.value, language)} ${UNIT[m.kind]}`}</span>
                    <span className="item-meta">{day(m.on)}</span>
                  </span>
                  <Icon name="pencil" size={18} />
                </button>
              ) : (
                <div className="row-link row-static period-row measurement-row">
                  <span className="period-row-text">
                    <span className="item-name">{`${formatUnits(m.value, language)} ${UNIT[m.kind]}`}</span>
                    <span className="item-meta">{day(m.on)}</span>
                  </span>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </Sheet>
  );
}
