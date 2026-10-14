import { api } from "./api";
import type { Food, SaveFood } from "./types";

const plain = (text: string) =>
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
