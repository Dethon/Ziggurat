import { useCallback, useEffect, useId, useState } from "react";
import { api } from "../api";
import { formatGrams, fromFood, parseAmount } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon, type IconName } from "../components/Icon";
import { Jar } from "../components/Illustrations";
import { ProductFinder, type Found } from "../components/ProductFinder";
import { ShareChips } from "../components/ShareChips";
import { Sheet } from "../components/Sheet";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { fromPack, originOf, plain, sourceFor } from "../foods";
import { bindUnits } from "../meal";
import { asFood, helping, shelf } from "../recipes";
import { useSettings } from "../settings";
import type { DraftItem, Food, Recipe, SaveFood, Unit } from "../types";

type View =
  | { kind: "list" }
  | { kind: "amount"; food: Food }
  | { kind: "helping"; recipe: Recipe }
  | { kind: "edit"; food: Food | null; already?: boolean };

const SOURCE_ICON: Record<Food["source"], IconName> = { barcode: "barcode", label: "label", manual: "pencil" };

// Her own foods and recipes, opened from a meal to add one to it. Changing them lives on the Foods tab.
// `labels` says whether a new food can be read off a photo of its nutrition table, as on the meal screen.
export function FoodsSheet({
  labels,
  onClose,
  onAdd,
}: {
  labels: boolean;
  onClose: () => void;
  onAdd: (item: DraftItem) => void;
}) {
  const { t } = useSettings();
  const [foods, setFoods] = useState<Food[] | null>(null);
  const [recipes, setRecipes] = useState<Recipe[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>({ kind: "list" });

  const load = useCallback(async () => {
    try {
      const [kept, cooked] = await Promise.all([api.foods(), api.recipes()]);
      setFoods(kept);
      setRecipes(cooked);
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

  if (view.kind === "helping") {
    return <HelpingSheet recipe={view.recipe} foods={foods ?? []} onAdd={onAdd} onClose={onClose} onBack={backToList} />;
  }

  if (view.kind === "edit") {
    return (
      <FoodSheet
        key={view.food?.id ?? "new"}
        food={view.food}
        already={view.already}
        labels={labels}
        onOpen={(food) => setView({ kind: "edit", food, already: true })}
        onDone={backToList}
        onClose={onClose}
        onBack={backToList}
      />
    );
  }

  return (
    <Sheet title={t("myFoods")} onClose={onClose}>
      <FoodList
        foods={foods}
        recipes={recipes}
        error={error}
        onPick={(food) => setView({ kind: "amount", food })}
        onPickRecipe={(recipe) => setView({ kind: "helping", recipe })}
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

// A recipe in the list: what a helping of it carries, or that it cannot be counted.
export function describeRecipe(
  recipe: Recipe,
  foods: readonly Food[],
  t: ReturnType<typeof useSettings>["t"],
  language: string,
): string {
  const food = asFood(recipe, foods);
  if (food === null) {
    return t("recipeIncomplete");
  }
  const detail =
    food.carbsPer100g === null
      ? t("recipePerServing", { n: formatGrams(food.portionCarbs ?? 0) })
      : t("per100", { n: food.carbsPer100g.toLocaleString(language), unit: food.unit });
  return t("recipeMeta", { detail });
}

export function FoodList({
  foods,
  recipes,
  error,
  onPick,
  onPickRecipe,
  onAdd,
  onAddRecipe,
  onNew,
  framed = false,
}: {
  foods: Food[] | null;
  // Her recipes, listed among her foods; left out where only a food will do.
  recipes?: Recipe[] | null;
  error: string | null;
  onPick: (food: Food) => void;
  onPickRecipe?: (recipe: Recipe) => void;
  // A separate "+" on each row, where tapping the row itself does something else.
  onAdd?: (food: Food) => void;
  onAddRecipe?: (recipe: Recipe) => void;
  // Without it the list offers no button of its own (the screen pins one instead).
  onNew?: () => void;
  // On a screen the list sits on a card; in a sheet it sits on the sheet.
  framed?: boolean;
}) {
  const { t, language } = useSettings();
  const [query, setQuery] = useState("");
  const loaded = foods !== null && recipes !== null;
  const entries = loaded ? (recipes === undefined ? shelf(foods, []) : shelf(foods, recipes)) : [];
  const wanted = plain(query);
  const shown = entries.filter((entry) => plain(entry.name).includes(wanted));

  return (
    <div className="sheet-form">
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      {loaded && entries.length === 0 && (
        <div className="empty compact">
          <Jar />
          <p className="empty-title">{t("foodsEmpty")}</p>
          <p className="small">{t("foodsEmptyHint")}</p>
        </div>
      )}
      {loaded && entries.length > 0 && (
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
            {shown.map((entry) => {
              const food = entry.kind === "food" ? entry.food : null;
              const recipe = entry.kind === "recipe" ? entry.recipe : null;
              const add = food && onAdd ? () => onAdd(food) : recipe && onAddRecipe ? () => onAddRecipe(recipe) : null;
              return (
                <li key={`${entry.kind}-${food?.id ?? recipe!.id}`} className="food-item">
                  <button
                    type="button"
                    className="food-row"
                    onClick={() => (food ? onPick(food) : onPickRecipe?.(recipe!))}
                  >
                    <span className="food-source" aria-hidden="true">
                      <Icon name={food ? SOURCE_ICON[food.source] : "pot"} size={20} />
                    </span>
                    <span className="item-text">
                      <span className="item-name">{entry.name}</span>
                      <span className="item-meta">
                        {food ? describe(food, t, language) : describeRecipe(recipe!, foods ?? [], t, language)}
                      </span>
                    </span>
                    {!add && <Icon name="chevronRight" size={20} />}
                  </button>
                  {add && (
                    <button
                      type="button"
                      className="food-add"
                      aria-label={t("addFoodToMeal", { name: entry.name })}
                      onClick={add}
                    >
                      <Icon name="plus" size={22} />
                    </button>
                  )}
                </li>
              );
            })}
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
  toItem = (amount) => fromFood(food, amount),
  servings = false,
}: {
  food: Food;
  onAdd: (item: DraftItem) => void;
  onClose: () => void;
  onBack?: () => void;
  // What the amount becomes in the meal; a helping of a recipe remembers the recipe instead.
  toItem?: (amount: number) => DraftItem;
  // A count of a recipe's servings rather than of a food's portions.
  servings?: boolean;
}) {
  const { t, language } = useSettings();
  const formId = useId();
  const byWeight = food.carbsPer100g !== null;
  // A weighed food starts at her usual amount; a fixed portion starts at one of them.
  const usual = byWeight ? food.portionGrams : 1;
  const [amount, setAmount] = useState(usual === null ? "" : String(usual));
  const eaten = parseAmount(amount);
  const carbs = eaten === null ? null : toItem(eaten).carbs;
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
            onAdd(toItem(eaten));
          }
        }}
      >
        <p className="per100">{describe(food, t, language)}</p>
        <label className="field">
          <span className="field-label">
            {t(!byWeight ? (servings ? "recipeHowMany" : "foodHowMany") : food.unit === "ml" ? "amountDrunk" : "amountEaten")}
          </span>
          <span className="amount-input">
            <input inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} />
            {byWeight && <span className="amount-unit">{food.unit}</span>}
          </span>
        </label>
        {usual !== null && (
          <ShareChips
            base={usual}
            unit={byWeight ? food.unit : undefined}
            current={eaten}
            label={t(byWeight ? "amountEaten" : servings ? "recipeHowMany" : "foodHowMany")}
            onPick={(value) => setAmount(String(value))}
          />
        )}
        <p className="packaged-result" aria-live="polite">
          {carbs !== null ? t("carbsFromAmount", { carbs: formatGrams(carbs) }) : " "}
        </p>
      </form>
    </Sheet>
  );
}

// How much of one of her recipes: grams of the pot, or how many of its servings. A recipe missing an
// ingredient has no carbs to count, so it says so instead.
export function HelpingSheet({
  recipe,
  foods,
  onAdd,
  onClose,
  onBack,
}: {
  recipe: Recipe;
  foods: readonly Food[];
  onAdd: (item: DraftItem) => void;
  onClose: () => void;
  onBack?: () => void;
}) {
  const { t } = useSettings();
  const food = asFood(recipe, foods);
  if (food === null) {
    return (
      <Sheet title={recipe.name} onClose={onClose} onBack={onBack}>
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{t("recipeIncomplete")}</span>
        </p>
      </Sheet>
    );
  }
  return (
    <AmountSheet
      food={food}
      servings={recipe.servings !== null}
      toItem={(amount) => helping(recipe, foods, amount)!}
      onAdd={onAdd}
      onClose={onClose}
      onBack={onBack}
    />
  );
}

// A new food or one of hers, to change or delete. A new one can be filled in from a pack, by its
// barcode or a photo of its nutrition table; a pack she already keeps opens that food instead
// (`onOpen`), which then says so (`already`).
export function FoodSheet({
  food,
  already = false,
  labels,
  usedBy = [],
  onOpen,
  onDone,
  onClose,
  onBack,
}: {
  food: Food | null;
  already?: boolean;
  labels: boolean;
  // The names of the recipes that use this food, which keep it from being deleted.
  usedBy?: readonly string[];
  onOpen: (food: Food) => void;
  // With the food as saved, or nothing once it is deleted.
  onDone: (saved: Food | null) => void;
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
  // Where the numbers came from, which a pack read into a new food replaces.
  const [origin, setOrigin] = useState(() => originOf(food));
  const [finding, setFinding] = useState(false);
  // What the label reading had to say about the numbers, beside them.
  const [packNote, setPackNote] = useState<string | null>(null);

  const per100 = parseAmount(density);
  const usual = parseAmount(portion);
  const fixed = parseAmount(portionCarbs);
  const valid =
    name.trim() !== "" &&
    (byWeight ? per100 !== null && per100 <= 100 && (portion.trim() === "" || (usual !== null && usual > 0)) : fixed !== null);

  async function run(work: () => Promise<Food | void>) {
    setBusy(true);
    setError(null);
    try {
      onDone((await work()) ?? null);
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  const next: SaveFood = {
    name: name.trim(),
    carbsPer100g: byWeight ? per100 : null,
    unit: byWeight ? unit : "g",
    portionGrams: byWeight ? usual : null,
    portionCarbs: byWeight ? null : fixed,
    barcode: origin.barcode,
    source: sourceFor(origin, byWeight, per100),
  };
  // One of hers is saved again only once something about it changed.
  const edited = useEdited(next);
  const save = () => run(() => (food ? api.updateFood(food.id, next) : api.createFood(next)));

  async function fromFound(found: Found) {
    setError(null);
    if (found.food.foodId) {
      try {
        const kept = (await api.foods()).find((f) => f.id === found.food.foodId);
        if (kept) {
          onOpen(kept);
          return;
        }
      } catch (failure) {
        setFinding(false);
        setError(errorMessage(failure, t));
        return;
      }
    }
    const pack = fromPack(found.food, found.source, found.barcode ?? null);
    setName(pack.name);
    setByWeight(true);
    setUnit(pack.unit);
    setDensity(pack.density);
    setPortion(pack.portion);
    setOrigin(pack.origin);
    setPackNote(found.food.note);
    setFinding(false);
  }

  if (finding) {
    return (
      <Sheet title={t("foodFromPack")} onClose={onClose} onBack={() => setFinding(false)}>
        <ProductFinder labels={labels} onFound={(found) => void fromFound(found)} />
      </Sheet>
    );
  }

  return (
    <Sheet
      title={t(food ? "foodEdit" : "foodNew")}
      onClose={onClose}
      onBack={onBack}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !valid || (food !== null && !edited)}>
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
        {already && (
          <p className="notice">
            <Icon name="jar" size={18} /> <span>{t("foodAlready")}</span>
          </p>
        )}
        {food === null && (
          <button type="button" className="button button-secondary" onClick={() => setFinding(true)}>
            <Icon name="barcode" />
            {t(origin.source === "manual" ? "foodFromPack" : "foodFromPackAgain")}
          </button>
        )}
        {origin.source !== "manual" && food === null && (
          <p className="muted small provenance-line">
            <Icon name={origin.source === "barcode" ? "barcode" : "label"} size={18} />
            <span>{origin.source === "barcode" ? t("sourceProduct", { code: origin.barcode ?? "" }) : t("sourceLabel")}</span>
          </p>
        )}
        {packNote && (
          <p className="notice">
            <Icon name="info" size={18} /> <span>{bindUnits(packNote)}</span>
          </p>
        )}
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
        {food && usedBy.length > 0 && (
          <p className="muted small provenance-line">
            <Icon name="pot" size={18} />
            <span>{t("foodInRecipes", { names: usedBy.join(", ") })}</span>
          </p>
        )}
        {food && (
          <button
            type="button"
            className="button button-quiet danger"
            disabled={busy || usedBy.length > 0}
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
