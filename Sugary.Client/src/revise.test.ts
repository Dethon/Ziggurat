import { describe, expect, it } from "vitest";
import { fromEstimate, manualItem, withGrams } from "./carbs";
import type { EstimateInput } from "./api";
import { continued } from "./estimate";
import { applied, revisionInput, type ReviseRequest } from "./revise";
import type { DraftItem, EstimatedItem, MealEstimate } from "./types";

const estimated = (name: string, portion: string, grams: number, carbs: number): EstimatedItem => ({
  name,
  portion,
  grams: { low: grams * 0.6, best: grams, high: grams * 1.6 },
  carbsPer100g: (carbs * 100) / grams,
  carbs: { low: carbs * 0.6, best: carbs, high: carbs * 1.6 },
  confidence: "medium",
  note: null,
});

const salad = fromEstimate(estimated("Ensaladilla rusa", "una ración", 250, 30));
const bread = fromEstimate(estimated("Pan", "una rebanada", 40, 20));
const beer: DraftItem = manualItem("Caña", 6);
const items = [salad, bread, beer];

const request = (over: Partial<ReviseRequest> = {}): ReviseRequest => ({
  words: "",
  photos: [],
  clip: null,
  answers: {},
  ...over,
});

const answer = (names: string[]): MealEstimate => ({
  items: names.map((name) => estimated(name, "una ración", 100, 10)),
  total: { low: 6, best: 10, high: 16 },
  questions: [],
  notes: [],
  conversationId: "c-1",
});

describe("adding a food she forgot", () => {
  it("sends her words and photos, with the whole meal counted so only the new food is estimated", () => {
    const photo = new Blob(["x"]);
    const input = revisionInput({ kind: "add" }, items, request({ words: "una caña más", photos: [photo] }), null, "es");
    expect(input.description).toBe("una caña más");
    expect(input.photos).toEqual([photo]);
    expect(input.counted.split("\n")).toEqual([
      "- Ensaladilla rusa, una ración: 30 g of carbs",
      "- Pan, una rebanada: 20 g of carbs",
      "- Caña: 6 g of carbs",
    ]);
    expect(input.clarifications).toBe("");
  });

  it("sends a clip she filmed of it", () => {
    const clip = new Blob(["v"], { type: "video/mp4" });
    expect(revisionInput({ kind: "add" }, items, request({ clip }), null, "es").clip).toBe(clip);
  });

  it("puts what the model found after the meal's foods", () => {
    expect(applied({ kind: "add" }, items, answer(["Caña"]).items.map(fromEstimate)).map((i) => i.name)).toEqual([
      "Ensaladilla rusa",
      "Pan",
      "Caña",
      "Caña",
    ]);
  });
});

describe("correcting one food in words", () => {
  it("describes only that food, with the stored photo, and counts the others", () => {
    const photo = new Blob(["meal"]);
    const input = revisionInput(
      { kind: "food", id: salad.id },
      items,
      request({ words: "llevaba mucha más patata" }),
      photo,
      "es",
    );
    expect(input.description).toBe("Ensaladilla rusa, una ración (250 g)");
    expect(input.photos).toEqual([photo]);
    expect(input.clarifications).toBe("Correction: llevaba mucha más patata");
    expect(input.counted.split("\n")).toEqual(["- Pan, una rebanada: 20 g of carbs", "- Caña: 6 g of carbs"]);
    expect(input.clip).toBeNull();
  });

  it("tells the model a weight she set on it as a fact", () => {
    const halved = withGrams(salad, 125);
    const input = revisionInput({ kind: "food", id: halved.id }, [halved, bread], request({ words: "sin mayonesa" }), null, "es");
    expect(input.description).toBe("Ensaladilla rusa (125 g)");
    expect(input.clarifications).toBe('Correction: "Ensaladilla rusa" weighs 125 g.\nCorrection: sin mayonesa');
  });

  it("replaces the food where it was, with as many foods as the model found in it", () => {
    const next = applied({ kind: "food", id: salad.id }, items, answer(["Patatas", "Mayonesa"]).items.map(fromEstimate));
    expect(next.map((i) => i.name)).toEqual(["Patatas", "Mayonesa", "Pan", "Caña"]);
    expect(next[0].source).toBe("estimate");
  });
});

describe("correcting the whole meal in words", () => {
  it("describes the estimated foods, keeps her edits as facts and counts what she added herself", () => {
    const halved = withGrams(salad, 125);
    const input = revisionInput(
      { kind: "meal" },
      [halved, bread, beer],
      request({ words: "el pan era integral" }),
      null,
      "es",
    );
    expect(input.description).toBe("Ensaladilla rusa (125 g); Pan, una rebanada (40 g)");
    expect(input.clarifications).toBe('Correction: "Ensaladilla rusa" weighs 125 g.\nCorrection: el pan era integral');
    expect(input.counted).toBe("- Caña: 6 g of carbs");
  });

  it("replaces the estimated foods and keeps what she counted herself", () => {
    const next = applied({ kind: "meal" }, items, answer(["Ensaladilla rusa", "Pan integral"]).items.map(fromEstimate));
    expect(next.map((i) => i.name)).toEqual(["Ensaladilla rusa", "Pan integral", "Caña"]);
    expect(next[2]).toBe(beer);
  });
});

describe("answering the model's questions", () => {
  it("continues the conversation with her answers and nothing sent again", async () => {
    const sent: EstimateInput[] = [];
    await continued(
      revisionInput(
        { kind: "add" },
        items,
        request({ words: "una caña", photos: [new Blob(["x"])], answers: { "¿Con alcohol?": "Sí" } }),
        null,
        "es",
      ),
      "c-1",
      async (input) => (sent.push(input), answer([])),
    );
    const [input] = sent;
    expect(input.conversationId).toBe("c-1");
    expect(input.photos).toEqual([]);
    expect(input.description).toBe("");
    expect(input.clarifications).toBe("Q: ¿Con alcohol?\nA: Sí");
  });

  it("keeps them as fresh facts when the conversation has to start again", () => {
    const input = revisionInput(
      { kind: "meal" },
      items,
      request({ words: "el pan era integral", answers: { "¿Cuánto pan?": "Dos rebanadas" } }),
      null,
      "es",
    );
    expect(input.conversationId).toBeNull();
    expect(input.clarifications).toBe("Q: ¿Cuánto pan?\nA: Dos rebanadas\nCorrection: el pan era integral");
  });
});
