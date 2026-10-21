import { describe, expect, it } from "vitest";
import { toMealItem } from "./carbs";
import { asFood, carbsOf, helping, missing, shelf, usedIn } from "./recipes";
import type { Food, Recipe } from "./types";

const food = (id: string, more: Partial<Food> = {}): Food => ({
  id,
  name: id,
  carbsPer100g: null,
  unit: "g",
  portionGrams: null,
  portionCarbs: null,
  barcode: null,
  source: "manual",
  uses: 0,
  lastUsedAt: null,
  ...more,
});

const lentils = food("Lentejas", { carbsPer100g: 48 });
const milk = food("Leche", { carbsPer100g: 4.8, unit: "ml" });
const stock = food("Pastilla de caldo", { portionCarbs: 2 });
const foods = [lentils, milk, stock];

const recipe = (more: Partial<Recipe> = {}): Recipe => ({
  id: "stew",
  name: "Lentejas de la abuela",
  cookedGrams: 1800,
  servings: null,
  ingredients: [
    { foodId: "Lentejas", amount: 300 },
    { foodId: "Leche", amount: 250 },
    { foodId: "Pastilla de caldo", amount: 2 },
  ],
  uses: 0,
  lastUsedAt: null,
  ...more,
});

describe("a recipe's carbs", () => {
  it("are its ingredients' carbs: a weight or volume by its density, a count by its portion", () => {
    // 144 from the lentils, 12 from the milk, 4 from two stock cubes.
    expect(carbsOf(recipe().ingredients, foods)).toBe(160);
  });

  it("cannot be counted while an ingredient is not in her pantry", () => {
    expect(carbsOf([...recipe().ingredients, { foodId: "gone", amount: 10 }], foods)).toBeNull();
    expect(missing(recipe({ ingredients: [{ foodId: "gone", amount: 10 }] }), foods)).toEqual(["gone"]);
  });

  it("follow her foods as they are now", () => {
    const changed = [food("Lentejas", { carbsPer100g: 50 }), milk, stock];
    expect(carbsOf(recipe().ingredients, changed)).toBe(166);
  });
});

describe("a recipe measured by its cooked weight", () => {
  it("is a food by weight, its carbs per 100 g the pot's over what the pot weighed", () => {
    expect(asFood(recipe(), foods)).toMatchObject({
      id: "stew",
      name: "Lentejas de la abuela",
      carbsPer100g: 8.9,
      unit: "g",
      portionCarbs: null,
    });
  });

  it("goes into a meal as one weighed helping that remembers the recipe and no food", () => {
    const item = toMealItem(helping(recipe(), foods, 300)!);
    expect(item).toMatchObject({ name: "Lentejas de la abuela", grams: 300, carbs: 26.7, source: "manual", foodId: null, recipeId: "stew" });
  });
});

describe("a recipe measured in servings", () => {
  const byServing = recipe({ cookedGrams: null, servings: 4 });

  it("is a food by portion, a serving carrying the pot's carbs over the servings", () => {
    expect(asFood(byServing, foods)).toMatchObject({ carbsPer100g: null, portionCarbs: 40 });
  });

  it("goes into a meal as a count of servings", () => {
    expect(helping(byServing, foods, 1.5)).toMatchObject({ carbs: 60, portion: "× 1.5", recipeId: "stew", foodId: null });
  });

  it("has no helping while an ingredient is missing", () => {
    expect(helping(byServing, [lentils], 1)).toBeNull();
  });
});

describe("her pantry's shelf", () => {
  it("names the recipes that use a food, so it is not deleted from under them", () => {
    const other = recipe({ id: "soup", name: "Sopa", ingredients: [{ foodId: "Leche", amount: 500 }] });
    expect(usedIn([recipe(), other], "Leche").map((r) => r.name)).toEqual(["Lentejas de la abuela", "Sopa"]);
    expect(usedIn([recipe(), other], "Lentejas").map((r) => r.name)).toEqual(["Lentejas de la abuela"]);
    expect(usedIn([other], "Lentejas")).toEqual([]);
  });

  it("puts foods and recipes in one list, the most recently eaten first, then by name", () => {
    const eaten = [
      food("Arroz", { lastUsedAt: "2026-10-10T12:00:00Z" }),
      food("Zanahoria"),
      food("Avena"),
    ];
    const recipes = [recipe({ name: "Lentejas", lastUsedAt: "2026-10-11T12:00:00Z" }), recipe({ id: "b", name: "Bizcocho" })];
    expect(shelf(eaten, recipes).map((entry) => entry.name)).toEqual(["Lentejas", "Arroz", "Avena", "Bizcocho", "Zanahoria"]);
  });
});
