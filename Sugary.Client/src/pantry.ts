import { useCallback, useEffect, useState } from "react";
import { api } from "./api";
import { errorMessage } from "./errors";
import type { Translate } from "./i18n";
import type { Food, Recipe } from "./types";

// Her pantry as a screen or sheet shows it: her foods and her recipes, fetched together because a
// recipe's carbs are counted from the foods, and fetched again after anything changes.
export function usePantry(t: Translate) {
  const [foods, setFoods] = useState<Food[] | null>(null);
  const [recipes, setRecipes] = useState<Recipe[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      const [foodList, recipeList] = await Promise.all([api.foods(), api.recipes()]);
      setFoods(foodList);
      setRecipes(recipeList);
      setError(null);
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return { foods, recipes, error, reload };
}
