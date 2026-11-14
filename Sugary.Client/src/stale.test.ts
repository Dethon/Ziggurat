import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";
import { errorMessage } from "./errors";
import { translator } from "./i18n";
import { openedDose, openedExercise, openedFood, openedPen, openedPeriod, openedRecipe, overRecord } from "./stale";
import type { Dose, Exercise, Food, Pen, Period, Recipe } from "./types";

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

  const dose: Dose = { id: "d1", takenAt: "2026-11-05T09:00:37+00:00", units: 6, kind: "rapid", note: null, mealIds: ["m1"] };
  const pen: Pen = {
    id: "p1",
    kind: "basal",
    insulin: "Tresiba",
    openedOn: "2026-10-20",
    days: 56,
    replaceOn: "2026-12-15",
    replacedOn: null,
  };
  const session: Exercise = { id: "e1", kind: "walk", name: null, startedAt: "2026-11-05T09:00:00+00:00", endedAt: null, note: null };
  const period: Period = { id: "q1", start: "2026-11-01", end: null };

  it("a dose, a pen, a session and a period", async () => {
    const body = sent();
    await api.updateDose("d1", { ...openedDose(dose), units: 7 }, openedDose(dose));
    expect((body() as { loaded: unknown }).loaded).toEqual({
      takenAt: "2026-11-05T09:00:37+00:00",
      units: 6,
      kind: "rapid",
      note: null,
      mealIds: ["m1"],
    });
    await api.updatePen("p1", { ...openedPen(pen), days: 60 }, openedPen(pen));
    expect((body() as { loaded: unknown }).loaded).toEqual({ kind: "basal", insulin: "Tresiba", openedOn: "2026-10-20", days: 56 });
    await api.updateExercise("e1", { ...openedExercise(session), endedAt: "2026-11-05T09:40:00.000Z" }, openedExercise(session));
    expect((body() as { loaded: unknown }).loaded).toEqual({
      kind: "walk",
      name: null,
      startedAt: "2026-11-05T09:00:00+00:00",
      endedAt: null,
      note: null,
    });
    await api.updatePeriod("q1", { start: "2026-11-01", end: "2026-11-05" }, openedPeriod(period));
    expect((body() as { loaded: unknown }).loaded).toEqual({ start: "2026-11-01", end: null });
  });

  // A sheet shows a time to the minute, so the time it would send is not the recorded one even
  // untouched; a field she left as the sheet showed it goes back as it was recorded, or another
  // phone's change of that time would be taken for a clash.
  it("a field left as the sheet showed it is sent as recorded", () => {
    const shown = { ...openedDose(dose), takenAt: "2026-11-05T09:00:00.000Z" };
    expect(overRecord(openedDose(dose), shown, { ...shown, units: 7 })).toEqual({ ...openedDose(dose), units: 7 });
    expect(overRecord(openedDose(dose), shown, { ...shown, takenAt: "2026-11-05T09:05:00.000Z" }).takenAt).toBe(
      "2026-11-05T09:05:00.000Z",
    );
  });
});
