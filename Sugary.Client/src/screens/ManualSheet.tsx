import { useState } from "react";
import { manualItem, parseAmount } from "../carbs";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { keep } from "../foods";
import { useSettings } from "../settings";
import type { DraftItem } from "../types";

export function ManualSheet({ onClose, onAdd }: { onClose: () => void; onAdd: (item: DraftItem) => void }) {
  const { t } = useSettings();
  const [name, setName] = useState("");
  const [carbs, setCarbs] = useState("");
  const [saveFood, setSaveFood] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const amount = parseAmount(carbs);

  async function add(named: string, grams: number) {
    setBusy(true);
    setError(null);
    try {
      // Kept as a fixed portion: what she typed is the carbs of one of these.
      const food = saveFood
        ? await keep({
            name: named,
            carbsPer100g: null,
            unit: "g",
            portionGrams: null,
            portionCarbs: grams,
            barcode: null,
            source: "manual",
          })
        : null;
      onAdd(manualItem(named, grams, food?.id ?? null));
    } catch (failure) {
      setError(errorMessage(failure, t));
      setBusy(false);
    }
  }

  return (
    <Sheet title={t("manualTitle")} onClose={onClose}>
      <form
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (name.trim() && amount !== null) {
            void add(name.trim(), amount);
          }
        }}
      >
        <label className="field">
          <span className="field-label">{t("manualName")}</span>
          <input value={name} autoFocus onChange={(event) => setName(event.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">{t("manualCarbs")}</span>
          <input inputMode="decimal" value={carbs} onChange={(event) => setCarbs(event.target.value)} />
        </label>
        <label className="check">
          <input type="checkbox" checked={saveFood} onChange={(event) => setSaveFood(event.target.checked)} />
          <span>
            {t("saveToFoods")}
            <small>{t("saveToFoodsManualHint")}</small>
          </span>
        </label>
        {error && (
          <p className="notice notice-error" role="alert">
            {error}
          </p>
        )}
        <button
          type="submit"
          className="button button-primary button-big"
          disabled={busy || !name.trim() || amount === null}
        >
          {t("addToMeal")}
        </button>
      </form>
    </Sheet>
  );
}
