import { describe, expect, it } from "vitest";
import { existing, fromPack, originOf, search, sourceFor } from "./foods";
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

describe("a pack read into her pantry", () => {
  const pack = { name: "Pan de molde", carbsPer100g: 46.5, servingGrams: 32, unit: "g" as const, note: null };

  it("fills the food with the pack's name, numbers and serving, and keeps its barcode", () => {
    expect(fromPack(pack, "barcode", "8480000823298")).toEqual({
      name: "Pan de molde",
      unit: "g",
      density: "46.5",
      portion: "32",
      origin: { source: "barcode", barcode: "8480000823298", carbsPer100g: 46.5 },
    });
  });

  it("leaves what the pack does not say empty, for her to type", () => {
    expect(fromPack({ ...pack, name: null, servingGrams: null, unit: "ml" }, "label", null)).toMatchObject({
      name: "",
      unit: "ml",
      portion: "",
      origin: { source: "label", barcode: null },
    });
  });

  // The pantry's icon says where a number came from: a pack's, untouched, keeps it; one she changed is hers.
  it("keeps the pack as the source until she changes its number", () => {
    const origin = fromPack(pack, "barcode", "8480000823298").origin;
    expect(sourceFor(origin, true, 46.5)).toBe("barcode");
    expect(sourceFor(origin, true, 50)).toBe("manual");
    expect(sourceFor(origin, false, null)).toBe("manual");
    expect(sourceFor(originOf(null), true, 46.5)).toBe("manual");
    expect(sourceFor(originOf(food("Pan", { source: "label" })), true, 46)).toBe("label");
  });
});
