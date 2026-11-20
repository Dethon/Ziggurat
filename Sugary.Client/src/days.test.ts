import { describe, expect, it } from "vitest";
import { asDayNote, FLOWS, nothingSaid, SYMPTOMS, sameDay, toggled } from "./days";

describe("what a day says", () => {
  it("knows the server's flows and symptoms, in its order", () => {
    expect(FLOWS).toEqual(["spotting", "light", "medium", "heavy"]);
    expect(SYMPTOMS).toEqual(["cramps", "headache", "back_pain", "tender_breasts", "bloating", "nausea", "tiredness", "mood_swings", "cravings", "acne"]);
  });

  it("toggles a symptom on and off, kept in the list's order", () => {
    expect(toggled(["nausea"], "cramps")).toEqual(["cramps", "nausea"]);
    expect(toggled(["cramps", "nausea"], "cramps")).toEqual(["nausea"]);
  });

  it("says nothing with no words, no flow and no symptom", () => {
    expect(nothingSaid({ text: "  ", flow: null, symptoms: [] })).toBe(true);
    expect(nothingSaid({ text: "", flow: "spotting", symptoms: [] })).toBe(false);
    expect(nothingSaid({ text: "", flow: null, symptoms: ["acne"] })).toBe(false);
  });

  it("compares two days as kept: words trimmed, symptoms in order", () => {
    expect(sameDay({ text: " tired ", flow: null, symptoms: ["nausea", "cramps"] }, { text: "tired", flow: null, symptoms: ["cramps", "nausea"] })).toBe(true);
    expect(sameDay({ text: "tired", flow: "light", symptoms: [] }, { text: "tired", flow: null, symptoms: [] })).toBe(false);
  });

  // A server from before flow and symptoms answers words alone.
  it("reads a day a server answered without flow or symptoms as having none", () => {
    expect(asDayNote({ day: "2026-10-12", text: "cramps" })).toEqual({ day: "2026-10-12", text: "cramps", flow: null, symptoms: [] });
  });
});
