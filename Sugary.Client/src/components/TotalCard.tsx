import { formatGrams, isRange } from "../carbs";
import { rationText } from "../format";
import { useSettings } from "../settings";
import type { Interval } from "../types";
import { Icon } from "./Icon";

// The meal's carbs, range first: the range is what she doses from, the best guess sits under it,
// and the reminder that this is an estimate stays right beside the number it qualifies.
export function TotalCard({
  total,
  stale = false,
  thumbnail,
  questions,
  onJumpToQuestions,
}: {
  total: Interval;
  // The last change failed: these numbers are from before it.
  stale?: boolean;
  thumbnail: string | null;
  questions: number;
  onJumpToQuestions: () => void;
}) {
  const { t, rationSize, language } = useSettings();
  const ranged = isRange(total);
  const [low, best, high] = [total.low, total.best, total.high].map(formatGrams);
  // The bar's scale runs a little past the high end so the range never touches the edge.
  const scale = Math.max(total.high * 1.2, 1);
  const percent = (grams: number) => `${(grams / scale) * 100}%`;

  return (
    <section className={stale ? "total card total-stale" : "total card"} aria-labelledby="total-label">
      <div className="total-top">
        <h2 id="total-label" className="total-label">
          {t("totalLabel")}
        </h2>
        {thumbnail && <img className="total-photo" src={thumbnail} alt="" />}
      </div>
      <p className="total-figure" aria-hidden="true">
        <span className="nowrap">{ranged ? `${low}–${high}` : best}</span>
        <span className="total-unit">g</span>
      </p>
      <p className="visually-hidden" aria-live="polite">
        {ranged ? t("totalSpoken", { low, high, best }) : t("totalSpokenExact", { n: best })}
      </p>
      <p className="total-best" aria-hidden={ranged}>
        {ranged ? t("totalBest", { best }) : t("exactTotal")}
      </p>
      {ranged && (
        <div className="range-bar" aria-hidden="true">
          <span className="range-fill" style={{ left: percent(total.low), width: percent(total.high - total.low) }} />
          <span className="range-mark" style={{ left: percent(total.best) }} />
        </div>
      )}
      <p className="total-rations">{rationText(total, rationSize, language, t)}</p>
      <p className="total-disclaimer">
        <Icon name="info" size={18} />
        <span>{t("estimateDisclaimer")}</span>
      </p>
      {questions > 0 && (
        <button type="button" className="link-button" onClick={onJumpToQuestions}>
          {t("questionsJump", { n: questions })}
          <Icon name="chevron" size={18} />
        </button>
      )}
    </section>
  );
}
