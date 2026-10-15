import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { useSettings } from "../settings";
import type { Food } from "../types";
import { FoodForm, FoodList } from "./FoodsSheet";

// Her pantry: everything she keeps, to find, change or add to.
export function FoodsScreen() {
  const { t } = useSettings();
  const [foods, setFoods] = useState<Food[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ food: Food | null } | null>(null);

  const load = useCallback(async () => {
    try {
      setFoods(await api.foods());
      setError(null);
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="screen">
      <h1>{t("foodsTitle")}</h1>
      <FoodList
        framed
        foods={foods}
        error={error}
        onPick={(food) => setEditing({ food })}
        onNew={() => setEditing({ food: null })}
      />
      {editing && (
        <Sheet title={t(editing.food ? "foodEdit" : "foodNew")} onClose={() => setEditing(null)}>
          <FoodForm
            food={editing.food}
            onDone={() => {
              setEditing(null);
              void load();
            }}
          />
        </Sheet>
      )}
    </div>
  );
}
