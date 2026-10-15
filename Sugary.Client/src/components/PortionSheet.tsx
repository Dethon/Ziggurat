import { useRef, useState } from "react";
import { formatGrams, isRange, parseAmount, withCarbs, withGrams } from "../carbs";
import { bindUnits, sentence } from "../meal";
import { useSettings } from "../settings";
import type { DraftItem } from "../types";
import { Icon } from "./Icon";
import { ItemBadge } from "./ItemRow";
import { ShareChips } from "./ShareChips";
import { Sheet } from "./Sheet";

const GRAM_STEP = 10;

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
  // Read once, so typing the carbs (which drops the weight) does not take the weight field away.
  const byWeight = useRef(item.carbsPer100g !== null && item.grams !== null).current;
  const [low, best, high] = [item.carbsLow, item.carbs, item.carbsHigh].map(formatGrams);
  // The carbs field holds only carbs she typed herself, never the model's guess.
  const typedCarbs = item.edited && item.grams === null ? item.carbs : null;
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
        <div className="portion-figure">
          <span className="item-grams">
            {isRange({ low: item.carbsLow, best: item.carbs, high: item.carbsHigh }) ? `${low}–${high}\u00a0g` : `${best}\u00a0g`}
          </span>
          {isRange({ low: item.carbsLow, best: item.carbs, high: item.carbsHigh }) && <span className="item-best">≈&nbsp;{best}</span>}
          <ItemBadge item={item} />
        </div>
        {item.note && (
          <p className="notice">
            <Icon name="info" size={18} /> <span>{bindUnits(item.note)}</span>
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
              <ShareChips
                base={opened.current}
                unit={unit}
                current={item.grams}
                label={t("portionAmount")}
                onPick={(grams) => onChange(withGrams(item, grams))}
              />
            )}
            <p className="muted small">{t("per100", { n: formatGrams(item.carbsPer100g ?? 0), unit })}</p>
          </>
        )}
        <AmountField
          label={byWeight ? t("portionCarbs") : t("carbs")}
          value={byWeight ? typedCarbs : item.carbs}
          placeholder={t("portionCarbsPlaceholder")}
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
  placeholder,
  onCommit,
}: {
  label: string;
  value: number | null;
  unit: string;
  step?: number;
  big?: boolean;
  placeholder?: string;
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
          <input
            inputMode="decimal"
            value={text}
            placeholder={placeholder}
            onChange={(event) => commit(event.target.value)}
          />
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
