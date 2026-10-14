import { describe, expect, it } from "vitest";
import { manualItem, withGrams } from "./carbs";
import { clarifications, counted, emptyDraft, reduce, type Draft } from "./draft";
import type { MealEstimate } from "./types";

const estimate = (names: string[], questions: string[] = []): MealEstimate => ({
  items: names.map((name) => ({
    name,
    portion: "some",
    grams: { low: 100, best: 150, high: 200 },
    carbsPer100g: 20,
    carbs: { low: 20, best: 30, high: 40 },
    confidence: "medium",
    note: null,
  })),
  total: { low: 20 * names.length, best: 30 * names.length, high: 40 * names.length },
  questions: questions.map((text) => ({ text, options: ["Yes", "No"] })),
  notes: [],
  conversationId: "c1",
});

const estimated = (draft: Draft, names: string[], questions: string[] = []) =>
  reduce(draft, { type: "estimated", estimate: estimate(names, questions) });

describe("draft", () => {
  it("replaces the model's items on a new estimate but keeps the person's own", () => {
    let draft = estimated(emptyDraft, ["rice", "bread"]);
    draft = reduce(draft, { type: "add", item: manualItem("yoghurt", 12) });
    draft = estimated(draft, ["rice"]);

    expect(draft.items.map((i) => i.name)).toEqual(["rice", "yoghurt"]);
  });

  it("remembers the first estimate through later ones", () => {
    let draft = estimated(emptyDraft, ["rice", "bread"]);
    draft = estimated(draft, ["rice"]);

    expect(draft.firstEstimate).toEqual({ low: 40, best: 60, high: 80 });
    expect(reduce(draft, { type: "reset" }).firstEstimate).toBeNull();
  });

  it("does not ask again what was already answered", () => {
    let draft = estimated(emptyDraft, ["cola"], ["Is the cola sugar-free?"]);
    draft = reduce(draft, { type: "answer", question: "Is the cola sugar-free?", answer: "Yes" });
    draft = estimated(draft, ["cola"], ["Is the cola sugar-free?", "Did you share the dish?"]);

    expect(draft.questions.map((q) => q.text)).toEqual(["Did you share the dish?"]);
  });

  it("turns answers, edits, removals and free text into facts for the next estimate", () => {
    let draft = estimated(emptyDraft, ["rice", "bread"], ["Sugar-free?"]);
    draft = reduce(draft, { type: "answer", question: "Sugar-free?", answer: "Yes" });
    draft = reduce(draft, { type: "replace", item: withGrams(draft.items[0], 120) });
    draft = reduce(draft, { type: "remove", id: draft.items[1].id });
    draft = reduce(draft, { type: "correct", text: "the sauce was tomato" });

    expect(clarifications(draft)).toBe(
      [
        "Q: Sugar-free?\nA: Yes",
        'Correction: "rice" weighs 120 g.',
        'Correction: the person is not eating "bread".',
        "Correction: the sauce was tomato",
      ].join("\n"),
    );
  });

  it("tells the model what the person added themselves, so a photo of it is not counted twice", () => {
    let draft = reduce(emptyDraft, { type: "add", item: { ...manualItem("Pan de molde", 29.4), portion: "64 g" } });
    draft = reduce(draft, { type: "add", item: manualItem("yoghurt", 12) });
    draft = estimated(draft, ["rice"]);

    expect(counted(draft)).toBe("- Pan de molde, 64 g: 29 g of carbs\n- yoghurt: 12 g of carbs");
    expect(counted(estimated(emptyDraft, ["rice"]))).toBe("");
  });

  it("does not report removing a hand-added item to the model", () => {
    let draft = reduce(emptyDraft, { type: "add", item: manualItem("yoghurt", 12) });
    draft = reduce(draft, { type: "remove", id: draft.items[0].id });

    expect(clarifications(draft)).toBe("");
  });
});
