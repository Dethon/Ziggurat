import { fromFood } from "./carbs";
import { plain } from "./foods";
import type { DraftItem, Food, Recipe, RecipeIngredient } from "./types";

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
export function asFood(recipe: Recipe, foods: readonly Food[]): Food | null {
  const carbs = carbsOf(recipe.ingredients, foods);
  if (carbs === null) {
    return null;
  }
  const byWeight = recipe.cookedGrams !== null;
  return {
    id: recipe.id,
    name: recipe.name,
    carbsPer100g: byWeight ? round1((carbs * 100) / recipe.cookedGrams!) : null,
    unit: "g",
    portionGrams: null,
    portionCarbs: byWeight ? null : round1(carbs / recipe.servings!),
    barcode: null,
    source: "manual",
    uses: recipe.uses,
    lastUsedAt: recipe.lastUsedAt,
  };
}

// A helping of a recipe, as one meal item: grams of the pot or a count of servings. Its numbers are
// hers, not a label's, and it remembers the recipe rather than a food.
export function helping(recipe: Recipe, foods: readonly Food[], amount: number): DraftItem | null {
  const food = asFood(recipe, foods);
  return food === null ? null : { ...fromFood(food, amount), source: "manual", foodId: null, recipeId: recipe.id };
}

export function usedIn(recipes: readonly Recipe[], foodId: string): Recipe[] {
  return recipes.filter((recipe) => recipe.ingredients.some((ingredient) => ingredient.foodId === foodId));
}

export function searchRecipes(recipes: readonly Recipe[], text: string): Recipe[] {
  const wanted = plain(text);
  return wanted === "" ? [...recipes] : recipes.filter((recipe) => plain(recipe.name).includes(wanted));
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
