import { api } from "../api";
import type { Meal } from "../types";
import { Icon } from "./Icon";

// A meal's photo, or a plate where it has none.
export function MealThumb({ meal }: { meal: Meal }) {
  return meal.hasPhoto ? (
    <img className="meal-thumb" src={api.photoUrl(meal.id)} alt="" loading="lazy" />
  ) : (
    <span className="meal-thumb meal-thumb-empty">
      <Icon name="plate" />
    </span>
  );
}
