import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";
import { errorMessage } from "./errors";
import { translator } from "./i18n";
import { openedFood, openedRecipe } from "./stale";
import type { Food, Recipe } from "./types";

// A sheet saves the whole record, and someone else may have saved it since the sheet opened: Ana
// raised the flour, Dani's open sheet changed the servings, and Ana's flour went back. Every change
// says what its sheet opened with, so the server lays only what this sheet changed over the record.
describe("a change sends what its sheet opened with", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function sent(): () => unknown {
    let body: unknown = null;
    vi.stubGlobal("fetch", async (_url: string, init: RequestInit) => {
      body = JSON.parse(String(init.body));
      return new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } });
    });
    return () => body;
  }

  const recipe: Recipe = {
    id: "r1",
    name: "Bizcocho",
    cookedGrams: null,
    servings: 8,
    ingredients: [{ foodId: "f1", amount: 250 }],
    uses: 3,
    lastUsedAt: null,
  };

  const food: Food = {
    id: "f1",
    name: "Harina",
    carbsPer100g: 72,
    unit: "g",
    portionGrams: null,
    portionCarbs: null,
    barcode: null,
    source: "manual",
    uses: 0,
    lastUsedAt: null,
  };

  it("a recipe", async () => {
    const body = sent();
    await api.updateRecipe("r1", { ...openedRecipe(recipe), servings: 10 }, openedRecipe(recipe));

    expect(body()).toEqual({
      name: "Bizcocho",
      cookedGrams: null,
      servings: 10,
      ingredients: [{ foodId: "f1", amount: 250 }],
      loaded: { name: "Bizcocho", cookedGrams: null, servings: 8, ingredients: [{ foodId: "f1", amount: 250 }] },
    });
  });

  it("a food", async () => {
    const body = sent();
    await api.updateFood("f1", { ...openedFood(food), carbsPer100g: 70 }, openedFood(food));

    expect((body() as { loaded: unknown }).loaded).toEqual({
      name: "Harina",
      carbsPer100g: 72,
      unit: "g",
      portionGrams: null,
      portionCarbs: null,
      barcode: null,
      source: "manual",
    });
  });

  // The same field changed by both is refused, and she is told in her language to look again.
  it("a clash is said in her language", () => {
    expect(errorMessage(new ApiError(409, "Changed elsewhere"), translator("es"), "save")).toBe(
      "Alguien lo ha cambiado mientras lo tenías abierto. Ciérralo y ábrelo de nuevo para ver el cambio, y luego haz el tuyo.",
    );
  });
});
