import type { Food, Recipe, SaveFood, SaveRecipe } from "./types";

// What a sheet opened with, sent beside its change so the server lays only what this sheet changed
// over the record as it is now: someone else may have saved it meanwhile (the server's StaleEdits).
export const openedRecipe = (recipe: Recipe): SaveRecipe => ({
  name: recipe.name,
  cookedGrams: recipe.cookedGrams,
  servings: recipe.servings,
  ingredients: recipe.ingredients.map(({ foodId, amount }) => ({ foodId, amount })),
});

export const openedFood = (food: Food): SaveFood => ({
  name: food.name,
  carbsPer100g: food.carbsPer100g,
  unit: food.unit,
  portionGrams: food.portionGrams,
  portionCarbs: food.portionCarbs,
  barcode: food.barcode,
  source: food.source,
});
