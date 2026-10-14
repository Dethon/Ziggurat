import { useState } from "react";
import { formatGrams, parseAmount, withCarbs, withGrams } from "../carbs";
import { useSettings } from "../settings";
import type { DraftItem } from "../types";
import { Icon } from "./Icon";

const GRAM_STEP = 10;

interface Props {
  item: DraftItem;
  onChange: (item: DraftItem) => void;
  onRemove: () => void;
}

export function ItemRow({ item, onChange, onRemove }: Props) {
  const { t } = useSettings();
  const [open, setOpen] = useState(false);
  const ranged = Math.round(item.carbsHigh) - Math.round(item.carbsLow) >= 1;
  const origin = item.edited
    ? t("edited")
    : item.source === "estimate"
      ? t(`confidence_${item.confidence ?? "medium"}`)
      : item.foodId
        ? t("fromMyFoods")
        : t(`source_${item.source}`);
  const tone = item.edited || item.source !== "estimate" ? "exact" : (item.confidence ?? "medium");

  return (
    <li className="item">
      <button type="button" className="item-summary" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className={`dot dot-${tone}`} />
        <span className="item-text">
          <span className="item-name">{item.name}</span>
          <span className="item-meta">{[item.portion, origin].filter(Boolean).join(" · ")}</span>
        </span>
        <span className="item-carbs">
          <span className="item-grams">{formatGrams(item.carbs)} g</span>
          {ranged && (
            <span className="item-meta">
              {formatGrams(item.carbsLow)}–{formatGrams(item.carbsHigh)}
            </span>
          )}
        </span>
        <span className={open ? "item-chevron open" : "item-chevron"}>
          <Icon name="chevron" size={18} />
        </span>
      </button>

      {open && (
        <div className="item-editor">
          {item.note && (
            <p className="item-note">
              <Icon name="info" size={16} /> {item.note}
            </p>
          )}
          <div className="item-fields">
            {item.carbsPer100g !== null && (
              <AmountField
                label={t("weight")}
                value={item.grams}
                step={GRAM_STEP}
                onCommit={(grams) => onChange(withGrams(item, grams))}
              />
            )}
            <AmountField label={t("carbs")} value={item.carbs} onCommit={(carbs) => onChange(withCarbs(item, carbs))} />
          </div>
          {item.carbsPer100g !== null && (
            <p className="muted small">{t("per100", { n: formatGrams(item.carbsPer100g), unit: "g" })}</p>
          )}
          <div className="item-actions">
            <button type="button" className="button button-quiet danger" onClick={onRemove}>
              {t("remove")}
            </button>
            <button type="button" className="button button-quiet" onClick={() => setOpen(false)}>
              {t("done")}
            </button>
          </div>
        </div>
      )}
    </li>
  );
}

interface AmountFieldProps {
  label: string;
  value: number | null;
  step?: number;
  onCommit: (value: number) => void;
}

function AmountField({ label, value, step, onCommit }: AmountFieldProps) {
  const shown = value === null ? "" : formatGrams(value);
  const [text, setText] = useState(shown);
  const [syncedTo, setSyncedTo] = useState(shown);
  // The other field recomputes this one; follow it unless the person is mid-edit here.
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
    <label className="amount-field">
      <span className="field-label">{label}</span>
      <span className="amount-control">
        {step && (
          <button type="button" className="stepper" aria-label={`−${step}`} onClick={() => nudge(-step)}>
            <Icon name="minus" size={18} />
          </button>
        )}
        <input inputMode="decimal" value={text} onChange={(event) => commit(event.target.value)} />
        <span className="amount-unit">g</span>
        {step && (
          <button type="button" className="stepper" aria-label={`+${step}`} onClick={() => nudge(step)}>
            <Icon name="plus" size={18} />
          </button>
        )}
      </span>
    </label>
  );
}
