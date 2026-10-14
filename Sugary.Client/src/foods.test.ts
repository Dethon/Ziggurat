import { describe, expect, it } from "vitest";
import { existing, search } from "./foods";
import type { Food } from "./types";

const food = (name: string, more: Partial<Food> = {}): Food => ({
  id: name,
  name,
  carbsPer100g: 46,
  unit: "g",
  portionGrams: 32,
  portionCarbs: null,
  barcode: null,
  source: "label",
  uses: 0,
  lastUsedAt: null,
  ...more,
});

describe("her own foods", () => {
  const foods = [food("Pan de molde", { barcode: "8480000823298" }), food("Macarrón"), food("Leche", { unit: "ml", carbsPer100g: 4.8 })];

  it("are found by any part of the name, ignoring case and accents", () => {
    expect(search(foods, "macarron").map((f) => f.name)).toEqual(["Macarrón"]);
    expect(search(foods, "  DE mol ").map((f) => f.name)).toEqual(["Pan de molde"]);
    expect(search(foods, "")).toHaveLength(3);
  });

  it("are not saved twice: the same barcode, or the same name and numbers, is the food she already has", () => {
    expect(existing(foods, { barcode: "8480000823298", name: "Otro nombre", carbsPer100g: 39, unit: "g" })?.name).toBe("Pan de molde");
    expect(existing(foods, { barcode: null, name: " macarrón ", carbsPer100g: 46, unit: "g" })?.name).toBe("Macarrón");
    expect(existing(foods, { barcode: null, name: "Macarrón", carbsPer100g: 70, unit: "g" })).toBeUndefined();
    expect(existing(foods, { barcode: "1234567890123", name: "Nuevo", carbsPer100g: 46, unit: "g" })).toBeUndefined();
  });
});
