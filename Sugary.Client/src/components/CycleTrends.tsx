import { dayDate, type Trends } from "../cycle";
import { formatDays, locale } from "../format";
import { useSettings } from "../settings";
import { Fold } from "./Fold";

// Folded (a phone's default), her average and range stay under the title.
// Her last year of cycles: her averages and range over a bar for each cycle, its length written on it.
// The bars stand on zero, so a two-day difference looks like one; the numbers carry it. A cycle too
// long to be one (a period she likely did not note) is drawn faint and counted in nothing; one running
// across a start read as spotting is drawn whole, and said so (countCycles).
export function CycleTrends({ trends, thisYear }: { trends: Trends; thisYear: string }) {
  const { t, language } = useSettings();
  // Scaled to the cycles counted, with room above the longest for its number: a skipped cycle twice as
  // long would flatten every other bar, so it reaches the top and its number says the rest.
  const tallest = trends.longest * 1.15;
  const height = (days: number) => `${Math.min(1, days / tallest) * 100}%`;
  const month = (day: string) => dayDate(day).toLocaleDateString(locale(language), { month: "short" }).replace(".", "");
  const days = (n: number) => (n === 1 ? t("periodDaysOne") : t("periodDays", { n }));

  return (
    <Fold
      id="cycle-trends"
      className="trends"
      title={t("trendsTitle")}
      gist={t("trendsGist", { average: trends.averageCycle, low: trends.shortest, high: trends.longest })}
    >
      <dl className="glucose-figures trends-figures">
        <div>
          <dt>{t("trendsAverageCycle")}</dt>
          <dd>{days(trends.averageCycle)}</dd>
        </div>
        <div>
          <dt>{t("trendsAveragePeriod")}</dt>
          <dd>{trends.averagePeriod === null ? "–" : days(trends.averagePeriod)}</dd>
        </div>
        <div>
          <dt>{t("trendsRange")}</dt>
          <dd>{trends.shortest === trends.longest ? days(trends.shortest) : t("trendsRangeDays", { low: trends.shortest, high: trends.longest })}</dd>
        </div>
      </dl>

      <ol className="trend-bars" aria-label={t("trendsChart")} style={{ ["--average-share" as string]: trends.averageCycle / tallest }}>
        {trends.cycles.map((cycle) => (
          <li
            key={cycle.start}
            className={cycle.skipped ? "trend-bar trend-bar-skipped" : "trend-bar"}
            aria-label={
              t("trendsCycleSpoken", { date: formatDays(cycle.start, cycle.start, language, thisYear), n: cycle.days }) +
              (cycle.skipped ? ` ${t("trendsSkipped")}` : "") +
              (cycle.joined ? ` ${t("trendsJoined")}` : "")
            }
          >
            <span className="trend-bar-column" aria-hidden="true">
              <span className="trend-bar-fill" style={{ height: height(cycle.days) }}>
                <span className="trend-bar-days">{cycle.days}</span>
                {cycle.periodDays !== null && (
                  <span className="trend-bar-period" style={{ height: `${(cycle.periodDays / cycle.days) * 100}%` }} />
                )}
              </span>
            </span>
            <span className="trend-bar-month" aria-hidden="true">
              {month(cycle.start)}
            </span>
          </li>
        ))}
      </ol>
      <p className="trends-legend muted small" aria-hidden="true">
        <span className="trends-key trends-key-period" />
        {t("trendsKeyPeriod")}
        <span className="trends-key trends-key-average" />
        {t("trendsKeyAverage")}
      </p>
      {trends.cycles.some((cycle) => cycle.skipped) && <p className="muted small">{t("trendsSkippedHint")}</p>}
      {trends.cycles.some((cycle) => cycle.joined) && <p className="muted small">{t("trendsJoinedHint")}</p>}
    </Fold>
  );
}
