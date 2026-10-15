import { useRef, useState } from "react";
import { formatGrams, parseAmount, withCarbs, withGrams } from "../carbs";
import { sentence } from "../meal";
import { useSettings } from "../settings";
import type { DraftItem } from "../types";
import { Icon } from "./Icon";
import { ItemBadge } from "./ItemRow";
import { Sheet } from "./Sheet";

const GRAM_STEP = 10;
const SHARES = [
  { share: 0.5, label: "½" },
  { share: 0.75, label: "¾" },
  { share: 1, label: "1" },
  { share: 1.5, label: "1½" },
  { share: 2, label: "2" },
];

// One food's amount, changed with the thumb: a big stepper, a few shares of the amount it opened
// with, or its carbs typed straight in. Every change applies at once; Done only closes.
export function PortionSheet({
  item,
  onChange,
  onRemove,
  onClose,
}: {
  item: DraftItem;
  onChange: (item: DraftItem) => void;
  onRemove: () => void;
  onClose: () => void;
}) {
  const { t } = useSettings();
  const opened = useRef(item.grams);
  const byWeight = item.carbsPer100g !== null && item.grams !== null;
  // Read once: a corrected amount loses its portion text, not its unit.
  const unit = useRef(item.portion?.endsWith(" ml") ? "ml" : "g").current;

  return (
    <Sheet
      title={sentence(item.name)}
      onClose={onClose}
      footer={
        <div className="footer-row">
          <button type="button" className="button button-quiet danger" onClick={onRemove}>
            <Icon name="trash" size={20} />
            {t("removeFromMeal")}
          </button>
          <button type="button" className="button button-primary button-grow" onClick={onClose}>
            {t("done")}
          </button>
        </div>
      }
    >
      <div className="sheet-form">
        <ItemBadge item={item} />
        {item.note && (
          <p className="notice">
            <Icon name="info" size={18} /> <span>{item.note}</span>
          </p>
        )}
        {byWeight && (
          <>
            <AmountField
              label={t("portionAmount")}
              value={item.grams}
              unit={unit}
              step={GRAM_STEP}
              big
              onCommit={(grams) => onChange(withGrams(item, grams))}
            />
            {opened.current !== null && opened.current > 0 && (
              <div className="chips shares" role="group" aria-label={t("portionAmount")}>
                {SHARES.map(({ share, label }) => {
                  const grams = Math.round(opened.current! * share);
                  const on = item.grams !== null && Math.round(item.grams) === grams;
                  return (
                    <button
                      key={share}
                      type="button"
                      className={on ? "chip chip-on" : "chip"}
                      aria-pressed={on}
                      onClick={() => onChange(withGrams(item, grams))}
                    >
                      <span className="share">×{label}</span> {grams}&nbsp;{unit}
                    </button>
                  );
                })}
              </div>
            )}
            <p className="muted small">{t("per100", { n: formatGrams(item.carbsPer100g!), unit })}</p>
          </>
        )}
        <AmountField
          label={byWeight ? t("portionCarbs") : t("carbs")}
          value={item.carbs}
          unit="g"
          onCommit={(carbs) => onChange(withCarbs(item, carbs))}
        />
      </div>
    </Sheet>
  );
}

function AmountField({
  label,
  value,
  unit,
  step,
  big = false,
  onCommit,
}: {
  label: string;
  value: number | null;
  unit: string;
  step?: number;
  big?: boolean;
  onCommit: (value: number) => void;
}) {
  const shown = value === null ? "" : formatGrams(value);
  const [text, setText] = useState(shown);
  const [syncedTo, setSyncedTo] = useState(shown);
  // The other field recomputes this one; follow it unless she is mid-edit here.
  if (shown !== syncedTo) {
    setSyncedTo(shown);
    setText(shown);
  }

  const commit = (next: string) => {
    setText(next);
    const amount = parseAmount(next);
    if (amount !== null) {
      setSyncedTo(formatGrams(amount));
      onCommit(amount);
    }
  };
  const nudge = (delta: number) => commit(String(Math.max(0, (parseAmount(text) ?? 0) + delta)));

  return (
    <label className={big ? "amount-field amount-big" : "amount-field"}>
      <span className="field-label">{label}</span>
      <span className="amount-control">
        {step && (
          <button type="button" className="stepper" aria-label={`−${step} ${unit}`} onClick={() => nudge(-step)}>
            <Icon name="minus" size={22} />
          </button>
        )}
        <span className="amount-input">
          <input inputMode="decimal" value={text} onChange={(event) => commit(event.target.value)} />
          <span className="amount-unit">{unit}</span>
        </span>
        {step && (
          <button type="button" className="stepper" aria-label={`+${step} ${unit}`} onClick={() => nudge(step)}>
            <Icon name="plus" size={22} />
          </button>
        )}
      </span>
    </label>
  );
}
