export type Confidence = "low" | "medium" | "high";
export type ItemSource = "estimate" | "label" | "barcode" | "manual";
export type Outcome = "unknown" | "low" | "inRange" | "high";

export interface Interval {
  low: number;
  best: number;
  high: number;
}

export interface EstimatedItem {
  name: string;
  portion: string;
  grams: Interval;
  carbsPer100g: number;
  carbs: Interval;
  confidence: Confidence;
  note: string | null;
}

export interface Question {
  text: string;
  options: string[];
}

export interface MealEstimate {
  items: EstimatedItem[];
  total: Interval;
  questions: Question[];
  notes: string[];
  // The conversation this estimate was made on, which her answers continue.
  conversationId: string;
}

export type Unit = "g" | "ml";

export interface PackagedFood {
  name: string | null;
  carbsPer100g: number;
  servingGrams: number | null;
  unit: Unit;
  note: string | null;
  // Set when the answer came from her own saved foods rather than the product database.
  foodId?: string | null;
}

// Something she keeps at home or eats often: a density with her usual amount, or a fixed
// portion she knows the carbs of.
export interface Food {
  id: string;
  name: string;
  carbsPer100g: number | null;
  unit: Unit;
  portionGrams: number | null;
  portionCarbs: number | null;
  barcode: string | null;
  source: Exclude<ItemSource, "estimate">;
  uses: number;
  lastUsedAt: string | null;
}

export type SaveFood = Omit<Food, "id" | "uses" | "lastUsedAt">;

export interface MealItem {
  name: string;
  portion: string | null;
  grams: number | null;
  carbsPer100g: number | null;
  carbsLow: number;
  carbs: number;
  carbsHigh: number;
  source: ItemSource;
  confidence: Confidence | null;
  // Set once the person has corrected the amount, so a re-estimate treats it as a fact.
  edited: boolean;
  // The food from her own list this item was made from, if any.
  foodId?: string | null;
}

export interface Meal {
  id: string;
  eatenAt: string;
  notes: string | null;
  outcome: Outcome;
  hasPhoto: boolean;
  firstEstimate: Interval | null;
  items: MealItem[];
  // The doses linked to it, as much of each as the Log shows.
  doses: LinkedDose[];
}

export interface LinkedDose {
  id: string;
  units: number;
  kind: DoseKind;
}

export interface SaveMeal {
  eatenAt: string;
  notes: string | null;
  outcome: Outcome;
  photo: string | null;
  firstEstimate: Interval | null;
  items: MealItem[];
  // The rapid insulin she took for it, saved with it on a new meal only; editing a meal never
  // touches its doses.
  dose?: { units: number; note: string | null } | null;
}

export type DoseKind = "rapid" | "basal";

// A dose of insulin she recorded taking: rapid (a meal or a correction) or basal (long-acting).
export interface Dose {
  id: string;
  takenAt: string;
  units: number;
  kind: DoseKind;
  note: string | null;
  // The meal it was taken for, when she linked one.
  mealId: string | null;
}

export type SaveDose = Omit<Dose, "id">;

export interface DraftItem extends MealItem {
  id: string;
  note: string | null;
}

export interface SessionState {
  required: boolean;
  authenticated: boolean;
  // Which of the model-backed actions the server answers. Off until the sugary agent answers them;
  // the app then shows them as not available rather than letting them fail.
  estimates: boolean;
  labels: boolean;
}
