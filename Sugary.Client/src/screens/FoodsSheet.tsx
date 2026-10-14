import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { formatGrams, fromFood, parseAmount } from "../carbs";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { search } from "../foods";
import { useSettings } from "../settings";
import type { DraftItem, Food, SaveFood, Unit } from "../types";

type View = { kind: "list" } | { kind: "amount"; food: Food } | { kind: "edit"; food: Food | null };

// Her own foods. Opened from a meal it adds one to the meal; opened from the settings it only
// manages the list.
export function FoodsSheet({ onClose, onAdd }: { onClose: () => void; onAdd?: (item: DraftItem) => void }) {
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

  if (view.kind === "amount" && onAdd) {
    return (
      <Sheet title={view.food.name} onClose={onClose} onBack={backToList}>
        <AmountForm food={view.food} onAdd={onAdd} onEdit={() => setView({ kind: "edit", food: view.food })} />
      </Sheet>
    );
  }

  if (view.kind === "edit") {
    return (
      <Sheet title={t(view.food ? "foodEdit" : "foodNew")} onClose={onClose} onBack={backToList}>
        <FoodForm food={view.food} onDone={backToList} />
      </Sheet>
    );
  }

  return (
    <Sheet title={t("myFoods")} onClose={onClose}>
      <FoodList
        foods={foods}
        error={error}
        onPick={(food) => setView(onAdd ? { kind: "amount", food } : { kind: "edit", food })}
        onNew={() => setView({ kind: "edit", food: null })}
      />
    </Sheet>
  );
}

function describe(food: Food, t: ReturnType<typeof useSettings>["t"], language: string): string {
  if (food.carbsPer100g === null) {
    return t("foodPerPortion", { n: formatGrams(food.portionCarbs ?? 0) });
  }
  const density = t("per100", { n: food.carbsPer100g.toLocaleString(language), unit: food.unit });
  return food.portionGrams === null
    ? density
    : `${density} · ${t("foodUsual", { n: formatGrams(food.portionGrams), unit: food.unit })}`;
}

function FoodList({
  foods,
  error,
  onPick,
  onNew,
}: {
  foods: Food[] | null;
  error: string | null;
  onPick: (food: Food) => void;
  onNew: () => void;
}) {
  const { t, language } = useSettings();
  const [query, setQuery] = useState("");
  const shown = search(foods ?? [], query);

  return (
    <div className="sheet-form">
      {error && (
        <p className="notice notice-error" role="alert">
          {error}
        </p>
      )}
      {foods !== null && foods.length === 0 && (
        <div className="empty compact">
          <p>{t("foodsEmpty")}</p>
          <p className="small">{t("foodsEmptyHint")}</p>
        </div>
      )}
      {foods !== null && foods.length > 0 && (
        <>
          <input
            type="search"
            value={query}
            placeholder={t("foodSearch")}
            aria-label={t("foodSearch")}
            onChange={(event) => setQuery(event.target.value)}
          />
          {shown.length === 0 && <p className="muted">{t("foodNone")}</p>}
          <ul className="foods">
            {shown.map((food) => (
              <li key={food.id}>
                <button type="button" className="food-row" onClick={() => onPick(food)}>
                  <span className="item-text">
                    <span className="item-name">{food.name}</span>
                    <span className="item-meta">{describe(food, t, language)}</span>
                  </span>
                  <Icon name="chevron" size={18} />
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
      <button type="button" className="button button-secondary" onClick={onNew}>
        <Icon name="plus" />
        {t("foodNew")}
      </button>
    </div>
  );
}

function AmountForm({ food, onAdd, onEdit }: { food: Food; onAdd: (item: DraftItem) => void; onEdit: () => void }) {
  const { t, language } = useSettings();
  const byWeight = food.carbsPer100g !== null;
  // A weighed food starts at her usual amount; a fixed portion starts at one of them.
  const usual = byWeight ? food.portionGrams : 1;
  const [amount, setAmount] = useState(usual === null ? "" : String(usual));
  const eaten = parseAmount(amount);
  const carbs = eaten === null ? null : fromFood(food, eaten).carbs;

  return (
    <form
      className="sheet-form"
      onSubmit={(event) => {
        event.preventDefault();
        if (eaten !== null && eaten > 0) {
          onAdd(fromFood(food, eaten));
        }
      }}
    >
      <p className="per100">{describe(food, t, language)}</p>
      <label className="field">
        <span className="field-label">{t(byWeight ? "amountEaten" : "foodHowMany")}</span>
        <span className="amount-control wide">
          <input inputMode="decimal" value={amount} autoFocus onChange={(event) => setAmount(event.target.value)} />
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
            <button key={choice.label} type="button" className="chip" onClick={() => setAmount(String(choice.value))}>
              {choice.label}
            </button>
          ))}
        </div>
      )}
      <p className="packaged-result">{carbs !== null ? t("carbsFromAmount", { carbs: formatGrams(carbs) }) : " "}</p>
      <button type="submit" className="button button-primary button-big" disabled={eaten === null || eaten <= 0}>
        {t("addToMeal")}
      </button>
      <button type="button" className="button button-quiet" onClick={onEdit}>
        {t("foodEdit")}
      </button>
    </form>
  );
}

function FoodForm({ food, onDone }: { food: Food | null; onDone: () => void }) {
  const { t } = useSettings();
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
      setError(errorMessage(failure, t));
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
    <form
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
        <span className="field-label">{t("foodKind")}</span>
        <div className="segmented" role="group" aria-label={t("foodKind")}>
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
            <span className="field-label">{t("foodUnit")}</span>
            <div className="segmented" role="group" aria-label={t("foodUnit")}>
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
          {error}
        </p>
      )}
      <button type="submit" className="button button-primary button-big" disabled={busy || !valid}>
        {t("foodSave")}
      </button>
      {food && (
        <button
          type="button"
          className="button button-quiet danger"
          disabled={busy}
          onClick={() => window.confirm(t("foodDeleteConfirm")) && run(() => api.deleteFood(food.id))}
        >
          {t("foodDelete")}
        </button>
      )}
    </form>
  );
}
