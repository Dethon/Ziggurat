import { formatGrams, fromFood, newId, parseAmount } from "./carbs";
import { countedByPortion } from "./foods";
import type { Translate } from "./i18n";
import type { DraftItem, Food, Recipe, RecipeIngredient, SaveRecipe } from "./types";

const round1 = (value: number) => Math.round(value * 10) / 10;

const byId = (foods: readonly Food[]) => new Map(foods.map((food) => [food.id, food]));

// The pot's carbs, from her foods as they are now; none while an ingredient is not in her pantry.
export function carbsOf(ingredients: readonly RecipeIngredient[], foods: readonly Food[]): number | null {
  const kept = byId(foods);
  const each = ingredients.map((ingredient) => {
    const food = kept.get(ingredient.foodId);
    return food ? fromFood(food, ingredient.amount).carbs : null;
  });
  return each.some((carbs) => carbs === null) ? null : round1(each.reduce<number>((sum, carbs) => sum + (carbs ?? 0), 0));
}

export function missing(recipe: Pick<Recipe, "ingredients">, foods: readonly Food[]): string[] {
  const kept = byId(foods);
  return recipe.ingredients.filter((ingredient) => !kept.has(ingredient.foodId)).map((ingredient) => ingredient.foodId);
}

// A recipe as one of her foods: by weight when she gave the cooked weight, by portion when she gave
// the servings. That is the shape a helping is measured in, so the amount sheet takes it as it is.
// One not saved yet has no id, and nothing reads the projection's own uses.
export function asFood(recipe: SaveRecipe & { id?: string }, foods: readonly Food[]): Food | null {
  const carbs = carbsOf(recipe.ingredients, foods);
  if (carbs === null) {
    return null;
  }
  const byWeight = recipe.cookedGrams !== null;
  return {
    id: recipe.id ?? "",
    name: recipe.name,
    carbsPer100g: byWeight ? round1((carbs * 100) / recipe.cookedGrams!) : null,
    unit: "g",
    portionGrams: null,
    portionCarbs: byWeight ? null : round1(carbs / recipe.servings!),
    barcode: null,
    source: "manual",
    uses: 0,
    lastUsedAt: null,
  };
}

// A helping of a recipe, as one meal item: grams of the pot or a count of servings. Its numbers are
// hers, not a label's, and it remembers the recipe rather than a food.
export function helping(recipe: Recipe, foods: readonly Food[], amount: number): DraftItem | null {
  const food = asFood(recipe, foods);
  return food === null ? null : { ...fromFood(food, amount), source: "manual", foodId: null, recipeId: recipe.id };
}

// What a helping carries: per 100 g of a weighed pot, or per serving.
export function perHelping(food: Food, t: Translate, language: string): string {
  return countedByPortion(food)
    ? t("recipePerServing", { n: formatGrams(food.portionCarbs ?? 0) })
    : t("per100", { n: food.carbsPer100g!.toLocaleString(language), unit: food.unit });
}

// A recipe in the list: what a helping of it carries, or that it cannot be counted.
export function describeRecipe(recipe: Recipe, foods: readonly Food[], t: Translate, language: string): string {
  const food = asFood(recipe, foods);
  return food === null ? t("recipeIncomplete") : t("recipeMeta", { detail: perHelping(food, t, language) });
}

export function usedIn(recipes: readonly Recipe[], foodId: string): Recipe[] {
  return recipes.filter((recipe) => recipe.ingredients.some((ingredient) => ingredient.foodId === foodId));
}

export type ShelfEntry = { kind: "food"; name: string; food: Food } | { kind: "recipe"; name: string; recipe: Recipe };

// Her foods and her recipes as one list, the most recently eaten first and the rest by name, as the
// server orders each of them.
export function shelf(foods: readonly Food[], recipes: readonly Recipe[]): ShelfEntry[] {
  const entries: (ShelfEntry & { lastUsedAt: string | null })[] = [
    ...foods.map((food) => ({ kind: "food" as const, name: food.name, food, lastUsedAt: food.lastUsedAt })),
    ...recipes.map((recipe) => ({ kind: "recipe" as const, name: recipe.name, recipe, lastUsedAt: recipe.lastUsedAt })),
  ];
  const when = (at: string | null) => (at === null ? -Infinity : Date.parse(at));
  return entries
    .sort((a, b) => when(b.lastUsedAt) - when(a.lastUsedAt) || a.name.localeCompare(b.name, undefined, { sensitivity: "base" }))
    .map(({ lastUsedAt: _, ...entry }) => entry);
}

// An ingredient as she is typing it: the amount is text until it parses.
export interface DraftIngredient {
  key: string;
  foodId: string;
  amount: string;
}

// The amount an ingredient starts at: her usual amount of a food by weight, one portion of one by portion.
export const startingAmount = (food: Food): string =>
  countedByPortion(food) ? "1" : food.portionGrams === null ? "" : String(food.portionGrams);

export const ingredientUnit = (food: Food): string => (countedByPortion(food) ? "×" : food.unit);

// A food goes in once; picking one already in leaves the recipe as it is, for her to change its amount.
export function withIngredient(rows: readonly DraftIngredient[], food: Food, key = newId()): readonly DraftIngredient[] {
  return rows.some((row) => row.foodId === food.id) ? rows : [...rows, { key, foodId: food.id, amount: startingAmount(food) }];
}

// The recipe she would save as the sheet stands, whether every amount is in (so the pot can be
// counted) and whether it can be saved at all.
export function readDraft(draft: {
  name: string;
  rows: readonly DraftIngredient[];
  byWeight: boolean;
  yieldText: string;
}): { recipe: SaveRecipe; counted: boolean; valid: boolean } {
  const positive = (value: number | null): value is number => value !== null && value > 0;
  const amounts = draft.rows.map((row) => parseAmount(row.amount));
  const made = parseAmount(draft.yieldText);
  const counted = amounts.length > 0 && amounts.every(positive);
  return {
    recipe: {
      name: draft.name.trim(),
      cookedGrams: draft.byWeight ? made : null,
      servings: draft.byWeight ? null : made,
      ingredients: draft.rows.map((row, index) => ({ foodId: row.foodId, amount: amounts[index] ?? 0 })),
    },
    counted,
    valid: draft.name.trim() !== "" && counted && positive(made),
  };
}
