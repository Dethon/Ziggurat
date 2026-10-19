import { api } from "./api";
import type { Food, PackagedFood, SaveFood, Unit } from "./types";

export const plain = (text: string) =>
  text
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase()
    .trim();

export function search(foods: readonly Food[], text: string): Food[] {
  const wanted = plain(text);
  return wanted === "" ? [...foods] : foods.filter((food) => plain(food.name).includes(wanted));
}

type Identity = Pick<SaveFood, "barcode" | "name" | "carbsPer100g" | "unit">;

// The food she already has for this product: the same barcode, or with no barcode to go by, the
// same name with the same numbers.
export function existing(foods: readonly Food[], candidate: Identity): Food | undefined {
  if (candidate.barcode) {
    const sameCode = foods.find((food) => food.barcode === candidate.barcode);
    if (sameCode) {
      return sameCode;
    }
  }
  return foods.find(
    (food) =>
      plain(food.name) === plain(candidate.name) &&
      food.carbsPer100g === candidate.carbsPer100g &&
      food.unit === candidate.unit &&
      (!candidate.barcode || !food.barcode),
  );
}

// Saving from a label or a barcode must not pile up copies of the same product.
export async function keep(food: SaveFood): Promise<Food> {
  return existing(await api.foods(), food) ?? (await api.createFood(food));
}

// Where a food's numbers came from, and the barcode that names its product.
export interface Origin {
  source: Food["source"];
  barcode: string | null;
  carbsPer100g: number | null;
}

export const originOf = (food: Food | null): Origin =>
  food
    ? { source: food.source, barcode: food.barcode, carbsPer100g: food.carbsPer100g }
    : { source: "manual", barcode: null, carbsPer100g: null };

// A pack read for her pantry, as the food's fields start: what the pack does not say is left for her.
export function fromPack(
  pack: PackagedFood,
  source: "barcode" | "label",
  barcode: string | null,
): { name: string; unit: Unit; density: string; portion: string; origin: Origin } {
  return {
    name: pack.name ?? "",
    unit: pack.unit,
    density: String(pack.carbsPer100g),
    portion: pack.servingGrams === null ? "" : String(pack.servingGrams),
    origin: { source, barcode, carbsPer100g: pack.carbsPer100g },
  };
}

// Numbers she typed or changed are hers; an untouched label or barcode entry keeps its origin.
export function sourceFor(origin: Origin, byWeight: boolean, per100: number | null): Food["source"] {
  return byWeight && per100 !== null && per100 === origin.carbsPer100g ? origin.source : "manual";
}
