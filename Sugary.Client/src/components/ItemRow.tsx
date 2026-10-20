import { formatGrams, isRange } from "../carbs";
import { bindUnits, sentence } from "../meal";
import { useSettings } from "../settings";
import type { DraftItem } from "../types";
import { Icon, Meter } from "./Icon";

const LEVEL = { low: 1, medium: 2, high: 3 } as const;

// How sure the number is, or where it came from: always words, with a glyph, never colour alone.
// Confidence never borrows the error red.
export function ItemBadge({ item }: { item: DraftItem }) {
  const { t } = useSettings();
  if (item.edited) {
    return (
      <span className="badge badge-edited">
        <Icon name="pencil" size={16} />
        {t("edited")}
      </span>
    );
  }
  if (item.source === "estimate") {
    const confidence = item.confidence ?? "medium";
    return (
      <span className={`badge badge-${confidence}`}>
        <Meter level={LEVEL[confidence]} />
        {t(`confidence_${confidence}`)}
      </span>
    );
  }
  return (
    <span className="badge badge-counted">
      <Icon name="checkCircle" size={16} />
      {item.foodId ? t("fromMyFoods") : t(`source_${item.source}`)}
    </span>
  );
}

export function ItemRow({ item, onOpen }: { item: DraftItem; onOpen: () => void }) {
  return (
    <li className="item">
      <button type="button" className="item-summary" aria-haspopup="dialog" onClick={onOpen}>
        <ItemContent item={item} />
        <Icon name="chevronRight" size={20} />
      </button>
    </li>
  );
}

// The same row only to be read, as a food the model found before she takes it.
export function ItemLine({ item }: { item: DraftItem }) {
  return (
    <li className="item">
      <div className="item-summary">
        <ItemContent item={item} />
      </div>
    </li>
  );
}

function ItemContent({ item }: { item: DraftItem }) {
  const { t } = useSettings();
  const ranged = isRange({ low: item.carbsLow, best: item.carbs, high: item.carbsHigh });
  const [low, best, high] = [item.carbsLow, item.carbs, item.carbsHigh].map(formatGrams);

  return (
    <>
      <span className="item-text">
        <span className="item-name">{sentence(item.name)}</span>
        {item.portion && <span className="item-meta">{bindUnits(item.portion)}</span>}
        <ItemBadge item={item} />
      </span>
      <span className="item-carbs">
        {ranged ? (
          <>
            <span className="item-grams" aria-hidden="true">
              {low}–{high}&nbsp;g
            </span>
            <span className="visually-hidden">{t("rangeSpoken", { low, high })}</span>
            <span className="item-best">≈&nbsp;{best}</span>
          </>
        ) : (
          <span className="item-grams">{best}&nbsp;g</span>
        )}
      </span>
    </>
  );
}
