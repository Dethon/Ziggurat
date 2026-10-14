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
}

export interface SaveMeal {
  eatenAt: string;
  notes: string | null;
  outcome: Outcome;
  photo: string | null;
  firstEstimate: Interval | null;
  items: MealItem[];
}

export interface DraftItem extends MealItem {
  id: string;
  note: string | null;
}

export interface SessionState {
  required: boolean;
  authenticated: boolean;
}
