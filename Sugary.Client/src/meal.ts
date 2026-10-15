import type { DraftItem, Food } from "./types";

// The one primary action the meal's footer offers. Whatever would leave the total on screen out of
// step with what she has told the app comes before saving, so a meal is never saved with her
// answers or her correction silently left out of its numbers.
export type FooterAction = "estimate" | "retry" | "update" | "sendCorrection" | "save";

export interface FooterState {
  items: number;
  // A photo, a clip or a description is there to estimate.
  canEstimate: boolean;
  estimated: boolean;
  // The last request to change the estimate failed, so the numbers on screen are from before it.
  stale: boolean;
  unsentAnswers: boolean;
  unsentCorrection: boolean;
}

export function footerAction(state: FooterState): FooterAction | null {
  if (state.items === 0) {
    return state.canEstimate ? "estimate" : null;
  }
  if (state.stale) {
    return "retry";
  }
  if (state.estimated && state.unsentAnswers) {
    return "update";
  }
  if (state.estimated && state.unsentCorrection) {
    return "sendCorrection";
  }
  // Foods added first and a photo of the rest: the photo is estimated before anything is saved.
  if (!state.estimated && state.canEstimate) {
    return "estimate";
  }
  return "save";
}

// Her most-used foods, offered first on an empty meal: most uses, then most recent.
export function usuals(foods: readonly Food[], count = 6): Food[] {
  return foods
    .filter((food) => food.uses > 0)
    .toSorted((a, b) => b.uses - a.uses || (b.lastUsedAt ?? "").localeCompare(a.lastUsedAt ?? ""))
    .slice(0, count);
}

// The model's items and what she counted herself, shown apart: only the first carry a range.
export function grouped(items: readonly DraftItem[]): { estimated: DraftItem[]; counted: DraftItem[] } {
  return {
    estimated: items.filter((item) => item.source === "estimate"),
    counted: items.filter((item) => item.source !== "estimate"),
  };
}

// The model writes "about 4 cm (250 g)" or "unos 60-70 g"; shown, a number never parts from its
// unit, and a range is one unit with an en dash, joined so a line never breaks inside it.
export function bindUnits(text: string): string {
  return text
    .replace(/(\d+(?:[.,]\d+)?)\s?[-–]\s?(\d+(?:[.,]\d+)?)/g, "$1\u2060–\u2060$2")
    .replace(/(\d) (g|ml|cm|kg|l)\b/g, "$1\u00a0$2");
}

// Names arrive in the model's lower case; shown, they start with a capital.
export function sentence(text: string): string {
  return text.charAt(0).toLocaleUpperCase() + text.slice(1);
}
