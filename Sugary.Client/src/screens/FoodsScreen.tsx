import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { ActionBar } from "../components/ActionBar";
import { Icon } from "../components/Icon";
import { errorMessage } from "../errors";
import { useSettings } from "../settings";
import type { DraftItem, Food } from "../types";
import { AmountSheet, FoodList, FoodSheet } from "./FoodsSheet";

// Her pantry: everything she keeps, to find, change, or add to the meal in two taps.
export function FoodsScreen({ onAddToMeal }: { onAddToMeal: (item: DraftItem) => void }) {
  const { t } = useSettings();
  const [foods, setFoods] = useState<Food[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ food: Food | null } | null>(null);
  const [adding, setAdding] = useState<Food | null>(null);

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
      <h1 className="marked">
        <Icon name="slice" size={28} />
        {t("foodsTitle")}
      </h1>
      <FoodList
        framed
        foods={foods}
        error={error}
        onPick={(food) => setEditing({ food })}
        onAdd={setAdding}
      />
      <ActionBar active>
        <button type="button" className="button button-primary button-big" onClick={() => setEditing({ food: null })}>
          <Icon name="plus" />
          {t("foodNew")}
        </button>
      </ActionBar>
      {editing && (
        <FoodSheet
          food={editing.food}
          onClose={() => setEditing(null)}
          onDone={() => {
            setEditing(null);
            void load();
          }}
        />
      )}
      {adding && (
        <AmountSheet
          food={adding}
          onClose={() => setAdding(null)}
          onAdd={(item) => {
            setAdding(null);
            onAddToMeal(item);
          }}
        />
      )}
    </div>
  );
}
