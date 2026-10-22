import { useId, useState } from "react";
import { api } from "../api";
import { formatGrams, fromFood, parseAmount } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { useEdited } from "../edited";
import {
  asFood,
  carbsOf,
  ingredientUnit,
  perHelping,
  readDraft,
  withIngredient,
  type DraftIngredient,
} from "../recipes";
import { useSaving } from "../saving";
import { useSettings } from "../settings";
import type { Food, Recipe } from "../types";
import { FoodList, FoodSheet } from "./FoodsSheet";

type View = { kind: "edit" } | { kind: "pick" } | { kind: "new" };

const rowsOf = (recipe: Recipe | null): DraftIngredient[] =>
  recipe?.ingredients.map((ingredient, index) => ({ key: String(index), foodId: ingredient.foodId, amount: String(ingredient.amount) })) ?? [];

// A new recipe or one of hers, to change or delete. Its ingredients come from her pantry; one that is
// not there yet is made with the ordinary new-food sheet, which keeps it in the pantry there and then.
// Closing the picker or that sheet comes back here, so what she wrote is only lost when she says so.
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
  const { busy, error, run } = useSaving(t);
  const [foods, setFoods] = useState(given);
  const [view, setView] = useState<View>({ kind: "edit" });
  const [name, setName] = useState(recipe?.name ?? "");
  const [rows, setRows] = useState<readonly DraftIngredient[]>(() => rowsOf(recipe));
  const [byWeight, setByWeight] = useState(recipe === null || recipe.cookedGrams !== null);
  const [cooked, setCooked] = useState(recipe?.cookedGrams?.toString() ?? "");
  const [servings, setServings] = useState(recipe?.servings?.toString() ?? "");

  const kept = new Map(foods.map((food) => [food.id, food]));
  const draft = readDraft({ name, rows, byWeight, yieldText: byWeight ? cooked : servings });
  const edited = useEdited(draft.recipe);
  const total = draft.counted ? carbsOf(draft.recipe.ingredients, foods) : null;
  // What a helping carries, worked out as the pantry list will once it is saved.
  const share = draft.valid ? asFood(draft.recipe, foods) : null;

  function include(food: Food) {
    setFoods((current) => (current.some((f) => f.id === food.id) ? current : [...current, food]));
    setRows((current) => withIngredient(current, food));
    setView({ kind: "edit" });
  }

  async function close() {
    if (!edited || (await confirm({ title: t("recipeDiscardTitle"), body: t("recipeDiscardBody"), confirm: t("discardYes"), cancel: t("discardNo") }))) {
      onClose();
    }
  }

  const back = () => setView({ kind: "edit" });

  if (view.kind === "pick") {
    return (
      <Sheet title={t("recipeAddIngredient")} onClose={back} onBack={back}>
        <FoodList
          foods={foods}
          error={null}
          onPick={(entry) => entry.kind === "food" && include(entry.food)}
          onNew={() => setView({ kind: "new" })}
        />
      </Sheet>
    );
  }

  if (view.kind === "new") {
    // A pack she already keeps is that food, so it goes in as it is rather than opening to be edited.
    return (
      <FoodSheet
        food={null}
        labels={labels}
        onOpen={include}
        onDone={(saved) => (saved ? include(saved) : back())}
        onClose={back}
        onBack={back}
      />
    );
  }

  return (
    <Sheet
      title={t(recipe ? "recipeEdit" : "recipeNew")}
      onClose={() => void close()}
      footer={
        <button
          type="submit"
          form={formId}
          className="button button-primary button-big"
          disabled={busy || !draft.valid || (recipe !== null && !edited)}
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
          if (draft.valid) {
            void run(() => (recipe ? api.updateRecipe(recipe.id, draft.recipe) : api.createRecipe(draft.recipe)), onDone);
          }
        }}
      >
        <label className="field">
          <span className="field-label">{t("name")}</span>
          <input value={name} autoFocus={recipe === null} onChange={(event) => setName(event.target.value)} />
        </label>

        <div className="field">
          <span className="field-label">{t("recipeIngredients")}</span>
          {rows.length === 0 && <p className="muted small">{t("recipeNoIngredients")}</p>}
          {rows.length > 0 && (
            <ul className="foods">
              {rows.map((row) => {
                const food = kept.get(row.foodId);
                const amount = parseAmount(row.amount);
                const carbs = food && amount !== null ? fromFood(food, amount).carbs : null;
                const label = food?.name ?? t("recipeIncomplete");
                return (
                  <li key={row.key} className="food-item recipe-ingredient">
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
                        value={row.amount}
                        onChange={(event) =>
                          setRows((current) => current.map((r) => (r.key === row.key ? { ...r, amount: event.target.value } : r)))
                        }
                      />
                      <span className="amount-unit">{food ? ingredientUnit(food) : "g"}</span>
                    </span>
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("recipeRemoveIngredient", { name: label })}
                      onClick={() => setRows((current) => current.filter((r) => r.key !== row.key))}
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
          {share && <span className="muted small recipe-share">{perHelping(share, t, language)}</span>}
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
                void run(() => api.deleteRecipe(recipe.id), onDone);
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
