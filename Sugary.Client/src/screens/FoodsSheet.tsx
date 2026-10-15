import { useCallback, useEffect, useId, useState } from "react";
import { api } from "../api";
import { formatGrams, fromFood, parseAmount } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon, type IconName } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { search } from "../foods";
import { useSettings } from "../settings";
import type { DraftItem, Food, SaveFood, Unit } from "../types";

type View = { kind: "list" } | { kind: "amount"; food: Food } | { kind: "edit"; food: Food | null };

const SOURCE_ICON: Record<Food["source"], IconName> = { barcode: "barcode", label: "label", manual: "pencil" };

// Her own foods, opened from a meal to add one to it. Changing them lives on the Foods tab.
export function FoodsSheet({ onClose, onAdd }: { onClose: () => void; onAdd: (item: DraftItem) => void }) {
  const { t } = useSettings();
  const [foods, setFoods] = useState<Food[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>({ kind: "list" });

  const load = useCallback(async () => {
    try {
      setFoods(await api.foods());
      setError(null);
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
  }, [load]);

  const backToList = () => {
    setView({ kind: "list" });
    void load();
  };

  if (view.kind === "amount") {
    return <AmountSheet food={view.food} onAdd={onAdd} onClose={onClose} onBack={backToList} />;
  }

  if (view.kind === "edit") {
    return <FoodSheet food={view.food} onDone={backToList} onClose={onClose} onBack={backToList} />;
  }

  return (
    <Sheet title={t("myFoods")} onClose={onClose}>
      <FoodList
        foods={foods}
        error={error}
        onPick={(food) => setView({ kind: "amount", food })}
        onNew={() => setView({ kind: "edit", food: null })}
      />
    </Sheet>
  );
}

export function describe(food: Food, t: ReturnType<typeof useSettings>["t"], language: string): string {
  if (food.carbsPer100g === null) {
    return t("foodPerPortion", { n: formatGrams(food.portionCarbs ?? 0) });
  }
  const density = t("per100", { n: food.carbsPer100g.toLocaleString(language), unit: food.unit });
  return food.portionGrams === null
    ? density
    : `${density} · ${t("foodUsual", { n: formatGrams(food.portionGrams), unit: food.unit })}`;
}

export function FoodList({
  foods,
  error,
  onPick,
  onAdd,
  onNew,
  framed = false,
}: {
  foods: Food[] | null;
  error: string | null;
  onPick: (food: Food) => void;
  // A separate "+" on each row, where tapping the row itself does something else.
  onAdd?: (food: Food) => void;
  // Without it the list offers no button of its own (the screen pins one instead).
  onNew?: () => void;
  // On a screen the list sits on a card; in a sheet it sits on the sheet.
  framed?: boolean;
}) {
  const { t, language } = useSettings();
  const [query, setQuery] = useState("");
  const shown = search(foods ?? [], query);

  return (
    <div className="sheet-form">
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      {foods !== null && foods.length === 0 && (
        <div className="empty compact">
          <Icon name="jar" size={40} />
          <p className="empty-title">{t("foodsEmpty")}</p>
          <p className="small">{t("foodsEmptyHint")}</p>
        </div>
      )}
      {foods !== null && foods.length > 0 && (
        <>
          <label className="search">
            <Icon name="search" size={20} />
            <input
              type="search"
              value={query}
              placeholder={t("foodSearch")}
              aria-label={t("foodSearch")}
              onChange={(event) => setQuery(event.target.value)}
            />
          </label>
          {shown.length === 0 && <p className="muted">{t("foodNone")}</p>}
          <ul className={framed ? "foods card foods-card" : "foods"}>
            {shown.map((food) => (
              <li key={food.id} className="food-item">
                <button type="button" className="food-row" onClick={() => onPick(food)}>
                  <span className="food-source" aria-hidden="true">
                    <Icon name={SOURCE_ICON[food.source]} size={20} />
                  </span>
                  <span className="item-text">
                    <span className="item-name">{food.name}</span>
                    <span className="item-meta">{describe(food, t, language)}</span>
                  </span>
                  {!onAdd && <Icon name="chevronRight" size={20} />}
                </button>
                {onAdd && (
                  <button
                    type="button"
                    className="food-add"
                    aria-label={t("addFoodToMeal", { name: food.name })}
                    onClick={() => onAdd(food)}
                  >
                    <Icon name="plus" size={22} />
                  </button>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
      {onNew && (
        <button type="button" className="button button-secondary" onClick={onNew}>
          <Icon name="plus" />
          {t("foodNew")}
        </button>
      )}
    </div>
  );
}

// How much of one of her foods, with her usual amount already in.
export function AmountSheet({
  food,
  onAdd,
  onClose,
  onBack,
}: {
  food: Food;
  onAdd: (item: DraftItem) => void;
  onClose: () => void;
  onBack?: () => void;
}) {
  const { t, language } = useSettings();
  const formId = useId();
  const byWeight = food.carbsPer100g !== null;
  // A weighed food starts at her usual amount; a fixed portion starts at one of them.
  const usual = byWeight ? food.portionGrams : 1;
  const [amount, setAmount] = useState(usual === null ? "" : String(usual));
  const eaten = parseAmount(amount);
  const carbs = eaten === null ? null : fromFood(food, eaten).carbs;
  const valid = eaten !== null && eaten > 0;

  return (
    <Sheet
      title={food.name}
      onClose={onClose}
      onBack={onBack}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={!valid}>
          {t("addToMeal")}
        </button>
      }
    >
      <form
        id={formId}
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (valid) {
            onAdd(fromFood(food, eaten));
          }
        }}
      >
        <p className="per100">{describe(food, t, language)}</p>
        <label className="field">
          <span className="field-label">{t(byWeight ? "amountEaten" : "foodHowMany")}</span>
          <span className="amount-input">
            <input inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} />
            {byWeight && <span className="amount-unit">{food.unit}</span>}
          </span>
        </label>
        {usual !== null && (
          <div className="chips">
            {[
              { label: t("foodHalf"), value: usual / 2 },
              { label: byWeight ? t("foodUsualChip", { n: formatGrams(usual), unit: food.unit }) : "1", value: usual },
              { label: t("foodDouble"), value: usual * 2 },
            ].map((choice) => (
              <button
                key={choice.label}
                type="button"
                className={eaten === choice.value ? "chip chip-on" : "chip"}
                aria-pressed={eaten === choice.value}
                onClick={() => setAmount(String(choice.value))}
              >
                {choice.label}
              </button>
            ))}
          </div>
        )}
        <p className="packaged-result" aria-live="polite">
          {carbs !== null ? t("carbsFromAmount", { carbs: formatGrams(carbs) }) : " "}
        </p>
      </form>
    </Sheet>
  );
}

// A new food or one of hers, to change or delete.
export function FoodSheet({
  food,
  onDone,
  onClose,
  onBack,
}: {
  food: Food | null;
  onDone: () => void;
  onClose: () => void;
  onBack?: () => void;
}) {
  const { t } = useSettings();
  const formId = useId();
  const [confirm, confirming] = useConfirm();
  const [name, setName] = useState(food?.name ?? "");
  const [byWeight, setByWeight] = useState(food === null || food.carbsPer100g !== null);
  const [unit, setUnit] = useState<Unit>(food?.unit ?? "g");
  const [density, setDensity] = useState(food?.carbsPer100g?.toString() ?? "");
  const [portion, setPortion] = useState(food?.portionGrams?.toString() ?? "");
  const [portionCarbs, setPortionCarbs] = useState(food?.portionCarbs?.toString() ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const per100 = parseAmount(density);
  const usual = parseAmount(portion);
  const fixed = parseAmount(portionCarbs);
  const valid =
    name.trim() !== "" &&
    (byWeight ? per100 !== null && per100 <= 100 && (portion.trim() === "" || (usual !== null && usual > 0)) : fixed !== null);

  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await work();
      onDone();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  const save = () => {
    const next: SaveFood = {
      name: name.trim(),
      carbsPer100g: byWeight ? per100 : null,
      unit: byWeight ? unit : "g",
      portionGrams: byWeight ? usual : null,
      portionCarbs: byWeight ? null : fixed,
      barcode: food?.barcode ?? null,
      // Numbers she typed or changed are hers; an untouched label or barcode entry keeps its origin.
      source: food !== null && byWeight && per100 === food.carbsPer100g ? food.source : "manual",
    };
    return run(() => (food ? api.updateFood(food.id, next) : api.createFood(next)));
  };

  return (
    <Sheet
      title={t(food ? "foodEdit" : "foodNew")}
      onClose={onClose}
      onBack={onBack}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !valid}>
          {t("foodSave")}
        </button>
      }
    >
      <form
        id={formId}
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (valid) {
            void save();
          }
        }}
      >
        <label className="field">
          <span className="field-label">{t("name")}</span>
          <input value={name} autoFocus={food === null} onChange={(event) => setName(event.target.value)} />
        </label>

        <div className="field">
          <span className="field-label" id={`${formId}-kind`}>
            {t("foodKind")}
          </span>
          <div className="segmented" role="group" aria-labelledby={`${formId}-kind`}>
            {[true, false].map((weight) => (
              <button
                key={String(weight)}
                type="button"
                className={byWeight === weight ? "segment segment-on" : "segment"}
                aria-pressed={byWeight === weight}
                onClick={() => setByWeight(weight)}
              >
                {t(weight ? "foodByWeight" : "foodByPortion")}
              </button>
            ))}
          </div>
          <p className="muted small">{t(byWeight ? "foodByWeightHint" : "foodByPortionHint")}</p>
        </div>

        {byWeight ? (
          <>
            <div className="field">
              <span className="field-label" id={`${formId}-unit`}>
                {t("foodUnit")}
              </span>
              <div className="segmented" role="group" aria-labelledby={`${formId}-unit`}>
                {(["g", "ml"] as const).map((option) => (
                  <button
                    key={option}
                    type="button"
                    className={unit === option ? "segment segment-on" : "segment"}
                    aria-pressed={unit === option}
                    onClick={() => setUnit(option)}
                  >
                    {option}
                  </button>
                ))}
              </div>
            </div>
            <label className="field">
              <span className="field-label">{t("foodCarbsPer100", { unit })}</span>
              <input inputMode="decimal" value={density} onChange={(event) => setDensity(event.target.value)} />
            </label>
            <label className="field">
              <span className="field-label">{t("foodUsualAmount", { unit })}</span>
              <input inputMode="decimal" value={portion} onChange={(event) => setPortion(event.target.value)} />
            </label>
          </>
        ) : (
          <label className="field">
            <span className="field-label">{t("foodPortionCarbs")}</span>
            <input inputMode="decimal" value={portionCarbs} onChange={(event) => setPortionCarbs(event.target.value)} />
          </label>
        )}

        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {food && (
          <button
            type="button"
            className="button button-quiet danger"
            disabled={busy}
            onClick={async () => {
              if (await confirm({ title: t("foodDeleteTitle"), body: t("foodDeleteBody"), confirm: t("foodDelete") })) {
                void run(() => api.deleteFood(food.id));
              }
            }}
          >
            <Icon name="trash" size={20} />
            {t("foodDelete")}
          </button>
        )}
      </form>
      {confirming}
    </Sheet>
  );
}
