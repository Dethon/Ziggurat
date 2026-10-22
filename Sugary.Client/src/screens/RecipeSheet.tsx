import { useId, useState } from "react";
import { api } from "../api";
import { formatGrams, fromFood, newId, parseAmount } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { asFood, carbsOf } from "../recipes";
import { useSettings } from "../settings";
import type { Food, Recipe, SaveRecipe } from "../types";
import { FoodList, FoodSheet } from "./FoodsSheet";

// An ingredient as she is typing it: the amount is text until it parses.
interface Row {
  key: string;
  foodId: string;
  amount: string;
}

const row = (foodId: string, amount: string): Row => ({ key: newId(), foodId, amount });

// The amount an ingredient starts at: her usual amount of a food by weight, one portion of one by portion.
const startingAmount = (food: Food) =>
  food.carbsPer100g === null ? "1" : food.portionGrams === null ? "" : String(food.portionGrams);

type View = { kind: "edit" } | { kind: "pick" } | { kind: "new"; food: Food | null; already?: boolean };

// A new recipe or one of hers, to change or delete. Its ingredients come from her pantry; one that is
// not there yet is made with the ordinary new-food sheet, which keeps it in the pantry there and then.
export function RecipeSheet({
  recipe,
  foods: given,
  labels,
  onDone,
  onClose,
}: {
  recipe: Recipe | null;
  foods: Food[];
  labels: boolean;
  onDone: () => void;
  onClose: () => void;
}) {
  const { t, language } = useSettings();
  const formId = useId();
  const [confirm, confirming] = useConfirm();
  const [foods, setFoods] = useState(given);
  const [view, setView] = useState<View>({ kind: "edit" });
  const [name, setName] = useState(recipe?.name ?? "");
  const [ingredients, setIngredients] = useState<Row[]>(
    () => recipe?.ingredients.map((ingredient) => row(ingredient.foodId, String(ingredient.amount))) ?? [],
  );
  const [byWeight, setByWeight] = useState(recipe === null || recipe.cookedGrams !== null);
  const [cooked, setCooked] = useState(recipe?.cookedGrams?.toString() ?? "");
  const [servings, setServings] = useState(recipe?.servings?.toString() ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const kept = new Map(foods.map((food) => [food.id, food]));
  const amounts = ingredients.map((ingredient) => parseAmount(ingredient.amount));
  const yieldAmount = parseAmount(byWeight ? cooked : servings);
  const valid =
    name.trim() !== "" &&
    ingredients.length > 0 &&
    amounts.every((amount) => amount !== null && amount > 0) &&
    yieldAmount !== null &&
    yieldAmount > 0;

  const next: SaveRecipe = {
    name: name.trim(),
    cookedGrams: byWeight ? yieldAmount : null,
    servings: byWeight ? null : yieldAmount,
    ingredients: ingredients.map((ingredient, index) => ({ foodId: ingredient.foodId, amount: amounts[index] ?? 0 })),
  };
  const edited = useEdited(next);
  // Only once every amount is in: a pot half typed in is not a total.
  const counted = ingredients.length > 0 && amounts.every((amount) => amount !== null && amount > 0);
  const total = counted ? carbsOf(next.ingredients, foods) : null;
  // What a helping carries, worked out as the pantry list will once it is saved.
  const share =
    counted && yieldAmount !== null && yieldAmount > 0 ? asFood({ ...next, id: "", uses: 0, lastUsedAt: null }, foods) : null;

  function include(food: Food) {
    setFoods((current) => (current.some((f) => f.id === food.id) ? current : [...current, food]));
    setIngredients((current) => [...current, row(food.id, startingAmount(food))]);
    setView({ kind: "edit" });
  }

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

  const back = () => setView({ kind: "edit" });

  if (view.kind === "pick") {
    return (
      <Sheet title={t("recipeAddIngredient")} onClose={onClose} onBack={back}>
        <FoodList
          foods={foods}
          error={null}
          onPick={include}
          onNew={() => setView({ kind: "new", food: null })}
        />
      </Sheet>
    );
  }

  if (view.kind === "new") {
    // A pack she already keeps opens that food, which she can take as it is.
    return (
      <FoodSheet
        key={view.food?.id ?? "new"}
        food={view.food}
        already={view.already}
        labels={labels}
        onOpen={(food) => setView({ kind: "new", food, already: true })}
        onDone={(saved) => (saved ? include(saved) : back())}
        onClose={onClose}
        onBack={back}
      />
    );
  }

  return (
    <Sheet
      title={t(recipe ? "recipeEdit" : "recipeNew")}
      onClose={onClose}
      footer={
        <button
          type="submit"
          form={formId}
          className="button button-primary button-big"
          disabled={busy || !valid || (recipe !== null && !edited)}
        >
          {t("recipeSave")}
        </button>
      }
    >
      <form
        id={formId}
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (valid) {
            void run(() => (recipe ? api.updateRecipe(recipe.id, next) : api.createRecipe(next)));
          }
        }}
      >
        <label className="field">
          <span className="field-label">{t("name")}</span>
          <input value={name} autoFocus={recipe === null} onChange={(event) => setName(event.target.value)} />
        </label>

        <div className="field">
          <span className="field-label">{t("recipeIngredients")}</span>
          {ingredients.length === 0 && <p className="muted small">{t("recipeNoIngredients")}</p>}
          {ingredients.length > 0 && (
            <ul className="foods">
              {ingredients.map((ingredient, index) => {
                const food = kept.get(ingredient.foodId);
                const amount = amounts[index];
                const carbs = food && amount !== null ? fromFood(food, amount).carbs : null;
                const label = food?.name ?? t("recipeIncomplete");
                return (
                  <li key={ingredient.key} className="food-item recipe-ingredient">
                    <span className="item-text">
                      <span className="item-name">{label}</span>
                      <span className="item-meta">
                        {carbs === null ? " " : t("recipeIngredientCarbs", { n: formatGrams(carbs) })}
                      </span>
                    </span>
                    <span className="amount-input recipe-amount">
                      <input
                        inputMode="decimal"
                        aria-label={label}
                        value={ingredient.amount}
                        onChange={(event) =>
                          setIngredients((current) =>
                            current.map((r) => (r.key === ingredient.key ? { ...r, amount: event.target.value } : r)),
                          )
                        }
                      />
                      <span className="amount-unit">
                        {food && food.carbsPer100g === null ? "×" : (food?.unit ?? "g")}
                      </span>
                    </span>
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("recipeRemoveIngredient", { name: label })}
                      onClick={() => setIngredients((current) => current.filter((r) => r.key !== ingredient.key))}
                    >
                      <Icon name="close" size={20} />
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
          <button type="button" className="button button-secondary" onClick={() => setView({ kind: "pick" })}>
            <Icon name="plus" />
            {t("recipeAddIngredient")}
          </button>
        </div>

        <div className="field">
          <span className="field-label" id={`${formId}-yield`}>
            {t("recipeYield")}
          </span>
          <div className="segmented" role="group" aria-labelledby={`${formId}-yield`}>
            {[true, false].map((weight) => (
              <button
                key={String(weight)}
                type="button"
                className={byWeight === weight ? "segment segment-on" : "segment"}
                aria-pressed={byWeight === weight}
                onClick={() => setByWeight(weight)}
              >
                {t(weight ? "recipeByWeight" : "recipeByServings")}
              </button>
            ))}
          </div>
          <p className="muted small">{t(byWeight ? "recipeByWeightHint" : "recipeByServingsHint")}</p>
        </div>
        <label className="field">
          <span className="field-label">{t(byWeight ? "recipeCookedGrams" : "recipeServings")}</span>
          {byWeight ? (
            <input inputMode="decimal" value={cooked} onChange={(event) => setCooked(event.target.value)} />
          ) : (
            <input inputMode="decimal" value={servings} onChange={(event) => setServings(event.target.value)} />
          )}
        </label>

        <p className="packaged-result recipe-total" aria-live="polite">
          {total !== null ? t("recipeTotal", { n: formatGrams(total) }) : " "}
          {share && (
            <span className="muted small recipe-share">
              {share.carbsPer100g === null
                ? t("recipePerServing", { n: formatGrams(share.portionCarbs ?? 0) })
                : t("per100", { n: share.carbsPer100g.toLocaleString(language), unit: share.unit })}
            </span>
          )}
        </p>

        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {recipe && (
          <button
            type="button"
            className="button button-quiet danger"
            disabled={busy}
            onClick={async () => {
              if (await confirm({ title: t("recipeDeleteTitle"), body: t("recipeDeleteBody"), confirm: t("recipeDelete") })) {
                void run(() => api.deleteRecipe(recipe.id));
              }
            }}
          >
            <Icon name="trash" size={20} />
            {t("recipeDelete")}
          </button>
        )}
      </form>
      {confirming}
    </Sheet>
  );
}
