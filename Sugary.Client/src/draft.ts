import { useEffect, useReducer } from "react";
import { formatGrams, fromEstimate } from "./carbs";
import { load, store } from "./settings";
import type { DraftItem, Interval, MealEstimate, Question } from "./types";

export interface Draft {
  items: DraftItem[];
  questions: Question[];
  answers: Record<string, string>;
  corrections: string[];
  removed: string[];
  notes: string[];
  description: string;
  thumbnail: string | null;
  // The model's total before any answer or correction, logged with the meal so its accuracy
  // can be judged later.
  firstEstimate: Interval | null;
  // Whether an estimate has been made, so an empty result can be told from a fresh start.
  estimated: boolean;
}

export const emptyDraft: Draft = {
  items: [],
  questions: [],
  answers: {},
  corrections: [],
  removed: [],
  notes: [],
  description: "",
  thumbnail: null,
  firstEstimate: null,
  estimated: false,
};

export type DraftAction =
  | { type: "describe"; description: string }
  | { type: "thumbnail"; thumbnail: string | null }
  | { type: "estimated"; estimate: MealEstimate }
  | { type: "answer"; question: string; answer: string }
  | { type: "correct"; text: string }
  | { type: "add"; item: DraftItem }
  | { type: "replace"; item: DraftItem }
  | { type: "remove"; id: string }
  | { type: "reset" };

export function reduce(draft: Draft, action: DraftAction): Draft {
  switch (action.type) {
    case "describe":
      return { ...draft, description: action.description };
    case "thumbnail":
      return { ...draft, thumbnail: action.thumbnail };
    case "estimated":
      return {
        ...draft,
        // A new estimate replaces the model's items only; what came from a label, a barcode
        // or the person's own entry is not the model's to change.
        items: [...action.estimate.items.map(fromEstimate), ...draft.items.filter((i) => i.source !== "estimate")],
        questions: action.estimate.questions.filter((q) => !(q.text in draft.answers)),
        notes: action.estimate.notes,
        firstEstimate: draft.firstEstimate ?? action.estimate.total,
        estimated: true,
      };
    case "answer":
      return { ...draft, answers: { ...draft.answers, [action.question]: action.answer } };
    case "correct":
      return { ...draft, corrections: [...draft.corrections, action.text] };
    case "add":
      return { ...draft, items: [...draft.items, action.item] };
    case "replace":
      return { ...draft, items: draft.items.map((i) => (i.id === action.item.id ? action.item : i)) };
    case "remove": {
      const item = draft.items.find((i) => i.id === action.id);
      return {
        ...draft,
        items: draft.items.filter((i) => i.id !== action.id),
        removed: item?.source === "estimate" ? [...draft.removed, item.name] : draft.removed,
      };
    }
    case "reset":
      return emptyDraft;
  }
}

// Everything the person has told us since the first estimate, phrased as facts for the model
// so that asking again builds on their corrections instead of undoing them.
export function clarifications(draft: Draft): string {
  const edits = draft.items
    .filter((item) => item.source === "estimate" && item.edited)
    .map((item) =>
      item.grams !== null
        ? `"${item.name}" weighs ${formatGrams(item.grams)} g.`
        : `"${item.name}" has ${formatGrams(item.carbs)} g of carbs.`,
    );
  return [
    ...Object.entries(draft.answers).map(([question, answer]) => `Q: ${question}\nA: ${answer}`),
    ...edits.map((edit) => `Correction: ${edit}`),
    ...draft.removed.map((name) => `Correction: the person is not eating "${name}".`),
    ...draft.corrections.map((text) => `Correction: ${text}`),
  ].join("\n");
}

// What the person added themselves, from a label, a barcode, their own foods or by hand. The
// model is told so that a photo showing the same food does not count it a second time.
export function counted(draft: Draft): string {
  return draft.items
    .filter((item) => item.source !== "estimate")
    .map((item) => `- ${[item.name, item.portion].filter(Boolean).join(", ")}: ${formatGrams(item.carbs)} g of carbs`)
    .join("\n");
}

const STORAGE_KEY = "sugary.draft";

export function useDraft() {
  const [draft, dispatch] = useReducer(reduce, emptyDraft, (empty) => load(STORAGE_KEY, empty));
  useEffect(() => store(STORAGE_KEY, draft), [draft]);
  return [draft, dispatch] as const;
}
