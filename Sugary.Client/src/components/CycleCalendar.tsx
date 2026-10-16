import { dayDate, monthGrid, shiftMonth, type CalendarDay } from "../cycle";
import { locale } from "../format";
import { sentence } from "../meal";
import { useSettings } from "../settings";
import type { Period } from "../types";
import { Icon } from "./Icon";

// Monday 5 January 2026, the first day of a week both her languages start on Monday.
const A_MONDAY = new Date(2026, 0, 5);

// A month of her cycle: recorded period days filled, noted days dotted, today ringed. A day still to
// come cannot be opened, since nothing can be recorded on it.
export function CycleCalendar({
  month,
  periods,
  noted,
  today,
  onMonth,
  onDay,
}: {
  month: string;
  periods: readonly Period[];
  noted: ReadonlySet<string>;
  today: string;
  onMonth: (month: string) => void;
  onDay: (day: string) => void;
}) {
  const { t, language } = useSettings();
  const weeks = monthGrid(month, periods, noted, today);
  const title = sentence(dayDate(`${month}-01`).toLocaleDateString(locale(language), { month: "long", year: "numeric" }));
  const weekdays = Array.from({ length: 7 }, (_, index) =>
    new Date(A_MONDAY.getFullYear(), A_MONDAY.getMonth(), A_MONDAY.getDate() + index).toLocaleDateString(locale(language), {
      weekday: "short",
    }),
  );

  function spoken(cell: CalendarDay): string {
    const date = dayDate(cell.day).toLocaleDateString(locale(language), { weekday: "long", day: "numeric", month: "long" });
    const marks = [cell.today && t("daySpokenToday"), cell.period && t("daySpokenPeriod"), cell.note && t("daySpokenNote")];
    return [date, ...marks.filter(Boolean)].join(", ");
  }

  return (
    <section className="card calendar" aria-labelledby="calendar-title">
      <header className="calendar-header">
        <button type="button" className="icon-button" aria-label={t("monthPrevious")} onClick={() => onMonth(shiftMonth(month, -1))}>
          <Icon name="back" />
        </button>
        <h2 id="calendar-title" className="calendar-title" aria-live="polite">
          {title}
        </h2>
        <button type="button" className="icon-button" aria-label={t("monthNext")} onClick={() => onMonth(shiftMonth(month, 1))}>
          <Icon name="chevronRight" />
        </button>
      </header>
      <table className="calendar-grid" aria-labelledby="calendar-title">
        <thead>
          <tr>
            {weekdays.map((name) => (
              <th key={name} scope="col" className="calendar-weekday">
                {name.replace(".", "")}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {weeks.map((week, index) => (
            <tr key={index}>
              {week.map((cell, weekday) =>
                cell === null ? (
                  <td key={weekday} />
                ) : (
                  <td key={cell.day}>
                    <button
                      type="button"
                      className={[
                        "calendar-day",
                        cell.period && "calendar-day-period",
                        cell.today && "calendar-day-today",
                        cell.future && "calendar-day-future",
                      ]
                        .filter(Boolean)
                        .join(" ")}
                      aria-label={spoken(cell)}
                      disabled={cell.future}
                      onClick={() => onDay(cell.day)}
                    >
                      {Number(cell.day.slice(8))}
                      {cell.note && <span className="calendar-dot" aria-hidden="true" />}
                    </button>
                  </td>
                ),
              )}
            </tr>
          ))}
        </tbody>
      </table>
      <ul className="calendar-legend" aria-hidden="true">
        <li>
          <span className="legend-swatch legend-period" />
          {t("legendPeriod")}
        </li>
        <li>
          <span className="legend-swatch legend-note" />
          {t("legendNote")}
        </li>
        <li>
          <span className="legend-swatch legend-today" />
          {t("legendToday")}
        </li>
      </ul>
    </section>
  );
}
