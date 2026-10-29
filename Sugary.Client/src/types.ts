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

// How much of one of her foods goes in: in its unit for a food by weight, how many of its portions
// for one by portion.
export interface RecipeIngredient {
  foodId: string;
  amount: number;
}

// Something she cooks from her foods, and what the pot makes: its cooked weight in grams, or how
// many servings — one of the two. Its carbs are worked out from her foods as they are now.
export interface Recipe {
  id: string;
  name: string;
  cookedGrams: number | null;
  servings: number | null;
  ingredients: RecipeIngredient[];
  uses: number;
  lastUsedAt: string | null;
}

export type SaveRecipe = Omit<Recipe, "id" | "uses" | "lastUsedAt">;

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
  // The recipe of hers this item was a helping of, if any.
  recipeId?: string | null;
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
  // Taken for other meals too, whose insulin it also is, whole.
  shared: boolean;
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
  // The meals it was taken for, none where she linked none: one bolus can cover several.
  mealIds: string[];
}

export type SaveDose = Omit<Dose, "id">;

// An insulin pen she opened: which insulin, the day she first used it and how many days it lasts
// open. The server says the day it is replaced (openedOn + days) and, once a newer pen of its kind
// took its place, the day that happened.
export interface Pen {
  id: string;
  kind: DoseKind;
  insulin: string;
  openedOn: string;
  days: number;
  replaceOn: string;
  replacedOn: string | null;
}

export type SavePen = Pick<Pen, "kind" | "insulin" | "openedOn" | "days">;

// A phone's push subscription as the server keeps it, with the language its reminders are written in.
export interface SavePushSubscription {
  endpoint: string;
  p256dh: string;
  auth: string;
  language: string;
}

export type ExerciseKind = "walk" | "gym" | "other";

// A session of exercise: a walk, the gym, or another kind she named, with no end while it runs.
export interface Exercise {
  id: string;
  kind: ExerciseKind;
  // Only another kind has one.
  name: string | null;
  startedAt: string;
  endedAt: string | null;
  note: string | null;
}

export type SaveExercise = Omit<Exercise, "id">;

// A period as she recorded it: the day it started and the day it ended, both yyyy-MM-dd on her
// calendar, with no end while it is going.
export interface Period {
  id: string;
  start: string;
  end: string | null;
}

export type SavePeriod = Omit<Period, "id">;

// What she wrote about a day of her calendar, period or not.
export interface DayNote {
  day: string;
  text: string;
}

export interface DraftItem extends MealItem {
  id: string;
  note: string | null;
}

export interface SessionState {
  // Which of the model-backed actions the server answers. Off until the sugary agent answers them;
  // the app then shows them as not available rather than letting them fail.
  estimates: boolean;
  labels: boolean;
}
