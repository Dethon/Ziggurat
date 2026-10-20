import { describe, expect, it } from "vitest";
import { bindUnits, footerAction, grouped, sentence, usuals, type FooterState } from "./meal";
import type { DraftItem, Food } from "./types";

const base: FooterState = {
  items: 3,
  canEstimate: true,
  estimated: true,
  stale: false,
  unsentAnswers: false,
  unsentCorrection: false,
};

describe("footerAction", () => {
  it("offers nothing on an empty meal with nothing to estimate", () => {
    expect(footerAction({ ...base, items: 0, canEstimate: false, estimated: false })).toBeNull();
  });

  it("offers the estimate once there is a photo, a clip or words", () => {
    expect(footerAction({ ...base, items: 0, estimated: false })).toBe("estimate");
  });

  it("saves an estimate she has nothing pending on", () => {
    expect(footerAction(base)).toBe("save");
  });

  it("puts her unsent answers in the total before saving", () => {
    expect(footerAction({ ...base, unsentAnswers: true })).toBe("update");
  });

  it("puts her unsent correction in the total before saving", () => {
    expect(footerAction({ ...base, unsentCorrection: true })).toBe("sendCorrection");
  });

  it("answers come before a correction, both go in the same request", () => {
    expect(footerAction({ ...base, unsentAnswers: true, unsentCorrection: true })).toBe("update");
  });

  it("tries again first when the last change failed and the numbers are old", () => {
    expect(footerAction({ ...base, stale: true, unsentAnswers: true })).toBe("retry");
  });

  it("estimates a photo before saving foods added ahead of it", () => {
    expect(footerAction({ ...base, items: 2, estimated: false })).toBe("estimate");
  });

  it("saves foods she added when there is nothing to estimate", () => {
    expect(footerAction({ ...base, items: 2, estimated: false, canEstimate: false })).toBe("save");
  });
});

const food = (id: string, uses: number, lastUsedAt: string | null): Food => ({
  id,
  name: id,
  carbsPer100g: null,
  unit: "g",
  portionGrams: null,
  portionCarbs: 10,
  barcode: null,
  source: "manual",
  uses,
  lastUsedAt,
});

describe("usuals", () => {
  it("orders by uses, then by the latest use, and leaves out foods never used", () => {
    const list = [
      food("toast", 22, "2026-10-14T08:00:00Z"),
      food("never", 0, null),
      food("coffee", 30, "2026-10-15T08:00:00Z"),
      food("juice", 22, "2026-10-15T09:00:00Z"),
    ];
    expect(usuals(list).map((f) => f.id)).toEqual(["coffee", "juice", "toast"]);
  });

  it("keeps the first few", () => {
    const list = Array.from({ length: 9 }, (_, i) => food(`f${i}`, 9 - i, null));
    expect(usuals(list, 6)).toHaveLength(6);
  });
});

describe("grouped", () => {
  it("sets what she counted apart from what was estimated", () => {
    const item = (source: DraftItem["source"]) => ({ source }) as DraftItem;
    const { estimated, counted } = grouped([item("estimate"), item("barcode"), item("manual"), item("estimate")]);
    expect(estimated).toHaveLength(2);
    expect(counted.map((i) => i.source)).toEqual(["barcode", "manual"]);
  });
});

describe("bindUnits", () => {
  it("keeps numbers with their units", () => {
    expect(bindUnits("A standard square, about 4 cm (250 g)")).toBe("A standard square, about 4\u00a0cm (250\u00a0g)");
    expect(bindUnits("2 slices")).toBe("2 slices");
  });

  it("makes a range one unbreakable piece with an en dash", () => {
    expect(bindUnits("Pan estándar de hamburguesa (unos 60-70 g)")).toBe("Pan estándar de hamburguesa (unos 60\u2060–\u206070\u00a0g)");
    expect(bindUnits("1 - 2 cucharadas")).toBe("1\u2060–\u20602 cucharadas");
    expect(bindUnits("90–110 g")).toBe("90\u2060–\u2060110\u00a0g");
    expect(bindUnits("0,5-1,5 tazas")).toBe("0,5\u2060–\u20601,5 tazas");
  });
});

describe("sentence", () => {
  it("capitalises the first letter only", () => {
    expect(sentence("lasagna with meat")).toBe("Lasagna with meat");
    expect(sentence("ñoquis")).toBe("Ñoquis");
  });
});
