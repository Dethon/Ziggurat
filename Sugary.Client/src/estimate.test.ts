import { describe, expect, it } from "vitest";
import { ApiError, estimateForm, type EstimateInput } from "./api";
import { manualItem } from "./carbs";
import { emptyDraft, reduce, type Draft } from "./draft";
import { estimateDraft } from "./estimate";
import type { MealEstimate } from "./types";

const answer = (conversationId: string, names: string[] = ["rice"]): MealEstimate => ({
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
  questions: [{ text: "How much rice?", options: ["a cup (160 g)", "two cups (320 g)"] }],
  notes: [],
  conversationId,
});

const photo = new Blob(["jpeg"], { type: "image/jpeg" });

// A draft estimated once, with a food she added herself and an answer not yet sent.
function answered(): Draft {
  let draft = reduce(emptyDraft, { type: "describe", description: "rice and bread" });
  draft = reduce(draft, { type: "add", item: manualItem("yoghurt", 12) });
  draft = reduce(draft, { type: "estimated", estimate: answer("c1") });
  return reduce(draft, { type: "answer", question: "How much rice?", answer: "a cup (160 g)" });
}

function recording(...replies: (MealEstimate | Error)[]) {
  const sent: EstimateInput[] = [];
  const send = async (input: EstimateInput) => {
    sent.push(input);
    const reply = replies.shift()!;
    if (reply instanceof Error) {
      throw reply;
    }
    return reply;
  };
  return { sent, send };
}

describe("the estimate's form", () => {
  it("sends a first estimate's photos and words, and no conversation", () => {
    const form = estimateForm({
      photos: [photo, photo],
      description: "rice",
      clarifications: "",
      counted: "- yoghurt: 12 g of carbs",
      language: "es",
      conversationId: null,
    });

    expect(form.getAll("photos").map((part) => (part as File).name)).toEqual(["meal-0.jpg", "meal-1.jpg"]);
    expect(form.get("description")).toBe("rice");
    expect(form.get("counted")).toBe("- yoghurt: 12 g of carbs");
    expect(form.get("language")).toBe("es");
    expect(form.has("conversationId")).toBe(false);
  });

  it("sends her answers on the conversation, with what she counted and nothing of the first turn", () => {
    const form = estimateForm({
      photos: [photo],
      description: "rice",
      clarifications: "Q: How much rice?\nA: a cup (160 g)",
      counted: "- yoghurt: 12 g of carbs",
      language: "en",
      conversationId: "c1",
    });

    expect(form.get("conversationId")).toBe("c1");
    expect(form.get("clarifications")).toBe("Q: How much rice?\nA: a cup (160 g)");
    expect(form.get("counted")).toBe("- yoghurt: 12 g of carbs");
    expect(form.get("language")).toBe("en");
    expect(form.has("photos")).toBe(false);
    expect(form.has("description")).toBe(false);
  });
});

describe("estimating a draft", () => {
  it("starts a meal's conversation with its photos and words", async () => {
    const { sent, send } = recording(answer("c1"));
    const draft = reduce(emptyDraft, { type: "describe", description: "rice" });

    await estimateDraft(draft, [photo], "en", send);

    expect(sent).toEqual([
      { photos: [photo], description: "rice", clarifications: "", counted: "", language: "en", conversationId: null },
    ]);
  });

  it("sends her answers on the conversation the first estimate opened, without the photos", async () => {
    const { sent, send } = recording(answer("c1"));

    await estimateDraft(answered(), [photo], "es", send);

    expect(sent).toEqual([
      {
        photos: [],
        description: "",
        clarifications: "Q: How much rice?\nA: a cup (160 g)",
        counted: "- yoghurt: 12 g of carbs",
        language: "es",
        conversationId: "c1",
      },
    ]);
  });

  it("estimates the meal afresh, with everything, when the conversation is gone", async () => {
    const { sent, send } = recording(new ApiError(410, "gone"), answer("c2"));

    const result = await estimateDraft(answered(), [photo], "en", send);

    expect(result.conversationId).toBe("c2");
    expect(sent[1]).toEqual({
      photos: [photo],
      description: "rice and bread",
      clarifications: "Q: How much rice?\nA: a cup (160 g)",
      counted: "- yoghurt: 12 g of carbs",
      language: "en",
      conversationId: null,
    });
  });

  it("does not start again on any other failure", async () => {
    const { sent, send } = recording(new ApiError(502, "not connected"));

    await expect(estimateDraft(answered(), [photo], "en", send)).rejects.toThrow("not connected");
    expect(sent).toHaveLength(1);
  });
});

describe("the draft's conversation", () => {
  it("is the one the latest estimate answered on, and is forgotten on a new meal", () => {
    let draft = answered();
    expect(draft.conversation).toBe("c1");

    draft = reduce(draft, { type: "estimated", estimate: answer("c2") });
    expect(draft.conversation).toBe("c2");
    expect(reduce(draft, { type: "reset" }).conversation).toBeNull();
  });

  it("keeps the first estimate and her own numbers through an answer", () => {
    let draft = answered();
    // The model lists her yoghurt again: it shows beside hers, and hers keeps its own number.
    draft = reduce(draft, { type: "estimated", estimate: answer("c1", ["rice", "yoghurt"]) });

    expect(draft.firstEstimate).toEqual({ low: 20, best: 30, high: 40 });
    expect(draft.items.map((i) => [i.name, i.source, i.carbs])).toEqual([
      ["rice", "estimate", 30],
      ["yoghurt", "estimate", 30],
      ["yoghurt", "manual", 12],
    ]);
  });
});
