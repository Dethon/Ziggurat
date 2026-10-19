import { useId, useState } from "react";
import { amountForCarbs, formatGrams, fromPackaged, parseAmount } from "../carbs";
import { Icon } from "../components/Icon";
import { ProductFinder, type Found } from "../components/ProductFinder";
import { ShareChips } from "../components/ShareChips";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { keep } from "../foods";
import { bindUnits } from "../meal";
import { load, store, useSettings } from "../settings";
import type { DraftItem } from "../types";

// Whether she types the amount and is told its carbs, or the carbs she wants and is told what to
// weigh. Remembered, since whoever counts one way tends to keep counting that way.
type Entry = "amount" | "carbs";
const ENTRY_KEY = "sugary.packagedEntry";

// Without `labels` a photo is read for its barcode only, and says so when it has none.
export function PackagedSheet({
  labels,
  onClose,
  onAdd,
}: {
  labels: boolean;
  onClose: () => void;
  onAdd: (item: DraftItem) => void;
}) {
  const { t, language } = useSettings();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [found, setFound] = useState<Found | null>(null);
  const [name, setName] = useState("");
  const [amount, setAmount] = useState("");
  const [saveFood, setSaveFood] = useState(true);
  const [entry, setEntry] = useState<Entry>(() => load(ENTRY_KEY, { by: "amount" as Entry }).by);
  const [wanted, setWanted] = useState("");

  function show(next: Found) {
    setFound(next);
    setName(next.food.name ?? "");
    setAmount(next.food.servingGrams ? formatGrams(next.food.servingGrams) : "");
    setWanted(next.food.servingGrams ? formatGrams((next.food.servingGrams * next.food.carbsPer100g) / 100) : "");
  }

  // The figure typed in one way carries over to the other; only the field she is going to is
  // rewritten, so the one she typed keeps its decimals.
  function enterBy(next: Entry) {
    if (found && eaten !== null && next !== entry) {
      if (next === "carbs") {
        setWanted(formatGrams((eaten * found.food.carbsPer100g) / 100));
      } else {
        setAmount(formatGrams(eaten));
      }
    }
    setEntry(next);
    store(ENTRY_KEY, { by: next });
  }

  async function run(work: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    } finally {
      setBusy(false);
    }
  }

  // A food with no carbs has no amount that reaches any, so it is only ever weighed.
  const canAskCarbs = found !== null && found.food.carbsPer100g > 0;
  const byCarbs = entry === "carbs" && canAskCarbs;
  const carbsWanted = parseAmount(wanted);
  const eaten = byCarbs
    ? carbsWanted === null
      ? null
      : amountForCarbs(carbsWanted, found.food.carbsPer100g)
    : parseAmount(amount);
  const formId = useId();
  const valid = eaten !== null && eaten > 0;

  return (
    <Sheet
      title={t("packagedTitle")}
      onClose={onClose}
      onBack={found ? () => setFound(null) : undefined}
      footer={
        found && (
          <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !valid}>
            {t("addToMeal")}
          </button>
        )
      }
    >
      {found === null ? (
        <ProductFinder labels={labels} onFound={show} />
      ) : (
        <form
          id={formId}
          className="sheet-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (!valid || eaten === null) {
              return;
            }
            const named = name.trim() || t("unnamedProduct");
            void run(async () => {
              // Kept with this amount as her usual one, so next time it is one tap.
              const foodId =
                found.food.foodId ??
                (saveFood
                  ? (
                      await keep({
                        name: named,
                        carbsPer100g: found.food.carbsPer100g,
                        unit: found.food.unit,
                        portionGrams: eaten,
                        portionCarbs: null,
                        barcode: found.barcode ?? null,
                        source: found.source === "barcode" ? "barcode" : "label",
                      })
                    ).id
                  : null);
              onAdd(fromPackaged(found.food, eaten, named, found.source, foodId));
            });
          }}
        >
          <label className="field">
            <span className="field-label">{t("name")}</span>
            <input value={name} onChange={(event) => setName(event.target.value)} />
          </label>
          <div className="provenance">
            <p className="per100">
              {t("per100", { n: found.food.carbsPer100g.toLocaleString(language), unit: found.food.unit })}
            </p>
            {/* Where the number came from, before she adds it: a database, a model's reading, or her own list. */}
            <p className="muted small provenance-line">
              <Icon name={found.food.foodId ? "jar" : found.source === "barcode" ? "barcode" : "label"} size={18} />
              <span>
                {found.food.foodId
                  ? t("fromMyFoods")
                  : found.source === "barcode"
                    ? t("sourceProduct", { code: found.barcode ?? "" })
                    : t("sourceLabel")}
              </span>
            </p>
          </div>
          {found.food.note && (
            <p className="notice">
              <Icon name="info" size={18} /> <span>{bindUnits(found.food.note)}</span>
            </p>
          )}
          {canAskCarbs && (
            <div className="field">
              <span className="field-label" id={`${formId}-entry`}>
                {t("packagedStartFrom")}
              </span>
              <div className="segmented" role="group" aria-labelledby={`${formId}-entry`}>
                {(["amount", "carbs"] as const).map((option) => (
                  <button
                    key={option}
                    type="button"
                    className={entry === option ? "segment segment-on" : "segment"}
                    aria-pressed={entry === option}
                    onClick={() => enterBy(option)}
                  >
                    {t(option === "amount" ? "packagedByAmount" : "packagedByCarbs")}
                  </button>
                ))}
              </div>
            </div>
          )}
          {byCarbs ? (
            <>
              <label className="field">
                <span className="field-label">{t("carbsWanted")}</span>
                <span className="amount-input">
                  <input inputMode="decimal" value={wanted} onChange={(event) => setWanted(event.target.value)} />
                  <span className="amount-unit">g</span>
                </span>
              </label>
              <p className="packaged-result">
                {eaten !== null
                  ? t(found.food.unit === "ml" ? "pourOut" : "weighOut", { amount: formatGrams(eaten), unit: found.food.unit })
                  : " "}
              </p>
            </>
          ) : (
            <>
              <label className="field">
                <span className="field-label">{t(found.food.unit === "ml" ? "amountDrunk" : "amountEaten")}</span>
                <span className="amount-input">
                  <input inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} />
                  <span className="amount-unit">{found.food.unit}</span>
                </span>
              </label>
              {found.food.servingGrams && (
                <ShareChips
                  base={found.food.servingGrams}
                  unit={found.food.unit}
                  current={eaten}
                  label={t("amountEaten")}
                  onPick={(value) => setAmount(String(value))}
                />
              )}
              <p className="packaged-result">
                {eaten !== null
                  ? t("carbsFromAmount", { carbs: formatGrams((eaten * found.food.carbsPer100g) / 100) })
                  : " "}
              </p>
            </>
          )}
          {!found.food.foodId && (
            <label className="check">
              <input type="checkbox" checked={saveFood} onChange={(event) => setSaveFood(event.target.checked)} />
              <span>
                {t("saveToFoods")}
                <small>{t("saveToFoodsHint")}</small>
              </span>
            </label>
          )}
          {error && (
            <p className="notice notice-error" role="alert">
              <Icon name="alert" size={18} /> <span>{error}</span>
            </p>
          )}
        </form>
      )}
    </Sheet>
  );
}
