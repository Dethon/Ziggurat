import { formatGrams, formatRations, isRange } from "../carbs";
import { useSettings } from "../settings";
import type { Interval } from "../types";

export function TotalCard({ total, thumbnail }: { total: Interval; thumbnail: string | null }) {
  const { t, rationSize, language } = useSettings();
  const ranged = isRange(total);
  // The bar's scale runs a little past the high end so the range never touches the edge.
  const scale = Math.max(total.high * 1.2, 1);
  const percent = (grams: number) => `${(grams / scale) * 100}%`;
  const ration = (grams: number) => formatRations(grams, rationSize, language);

  return (
    <section className="total card">
      {thumbnail && <img className="total-photo" src={thumbnail} alt="" />}
      <div className="total-figures">
        <h2>{t("carbs")}</h2>
        <p className="total-number">
          {ranged && <span className="total-approx">≈</span>}
          {formatGrams(total.best)}
          <span className="total-unit">g</span>
        </p>
        <p className="total-range">
          {ranged ? t("between", { low: formatGrams(total.low), high: formatGrams(total.high) }) : t("exactTotal")}
        </p>
        {ranged && (
          <div className="range-bar" aria-hidden="true">
            <span
              className="range-fill"
              style={{ left: percent(total.low), width: percent(total.high - total.low) }}
            />
            <span className="range-mark" style={{ left: percent(total.best) }} />
          </div>
        )}
        <p className="total-rations">
          <strong>{t("rations", { n: ration(total.best) })}</strong> {t("rationsOf", { size: rationSize })}
          {ranged && ration(total.low) !== ration(total.high) && (
            <span className="muted">
              {" "}
              · {ration(total.low)}–{ration(total.high)}
            </span>
          )}
        </p>
      </div>
    </section>
  );
}
