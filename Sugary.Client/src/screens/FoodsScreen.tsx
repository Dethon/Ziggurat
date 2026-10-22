import { useState } from "react";
import { ActionBar } from "../components/ActionBar";
import { Icon } from "../components/Icon";
import { usePantry } from "../pantry";
import { usedIn, type ShelfEntry } from "../recipes";
import { useSettings } from "../settings";
import type { DraftItem, Food, Recipe } from "../types";
import { AmountSheet, FoodList, FoodSheet, HelpingSheet } from "./FoodsSheet";
import { RecipeSheet } from "./RecipeSheet";

// Her pantry: everything she keeps and cooks, to find, change, or add to the meal in two taps.
// `labels` says whether a new food can be read off a photo of its nutrition table.
export function FoodsScreen({ labels, onAddToMeal }: { labels: boolean; onAddToMeal: (item: DraftItem) => void }) {
  const { t } = useSettings();
  const { foods, recipes, error, reload } = usePantry(t);
  const [editing, setEditing] = useState<{ food: Food | null; already?: boolean } | null>(null);
  const [cooking, setCooking] = useState<{ recipe: Recipe | null } | null>(null);
  const [adding, setAdding] = useState<ShelfEntry | null>(null);

  const addToMeal = (item: DraftItem) => {
    setAdding(null);
    onAddToMeal(item);
  };

  return (
    <div className="screen">
      <h1 className="marked">
        <Icon name="slice" size={28} />
        {t("foodsTitle")}
      </h1>
      <FoodList
        framed
        foods={foods}
        recipes={recipes}
        error={error}
        onPick={(entry) => (entry.kind === "food" ? setEditing({ food: entry.food }) : setCooking({ recipe: entry.recipe }))}
        onAdd={setAdding}
      />
      <ActionBar active>
        <div className="footer-row">
          <button type="button" className="button button-primary button-grow" onClick={() => setEditing({ food: null })}>
            <Icon name="plus" />
            {t("foodNew")}
          </button>
          <button
            type="button"
            className="button button-secondary button-grow"
            disabled={foods === null}
            onClick={() => setCooking({ recipe: null })}
          >
            <Icon name="pot" />
            {t("recipeNew")}
          </button>
        </div>
      </ActionBar>
      {editing && (
        <FoodSheet
          key={editing.food?.id ?? "new"}
          food={editing.food}
          already={editing.already}
          labels={labels}
          usedBy={editing.food ? usedIn(recipes ?? [], editing.food.id).map((recipe) => recipe.name) : []}
          onOpen={(food) => setEditing({ food, already: true })}
          onClose={() => setEditing(null)}
          onDone={() => {
            setEditing(null);
            void reload();
          }}
        />
      )}
      {cooking && foods && (
        <RecipeSheet
          key={cooking.recipe?.id ?? "new"}
          recipe={cooking.recipe}
          foods={foods}
          labels={labels}
          onClose={() => {
            setCooking(null);
            // A food made from the recipe stays in the pantry even if the recipe was not saved.
            void reload();
          }}
          onDone={() => {
            setCooking(null);
            void reload();
          }}
        />
      )}
      {adding?.kind === "food" && <AmountSheet food={adding.food} onClose={() => setAdding(null)} onAdd={addToMeal} />}
      {adding?.kind === "recipe" && (
        <HelpingSheet recipe={adding.recipe} foods={foods ?? []} onClose={() => setAdding(null)} onAdd={addToMeal} />
      )}
    </div>
  );
}
