import { describe, expect, it } from "vitest";
import { foldOpen } from "./fold";

describe("a folding card", () => {
  it("opens as she last left it on this device", () => {
    expect(foldOpen("open", false)).toBe(true);
    expect(foldOpen("closed", true)).toBe(false);
  });

  // A phone's screen is short; a desktop has the room.
  it("starts folded on a phone and open on a desktop", () => {
    expect(foldOpen(null, false)).toBe(false);
    expect(foldOpen(null, true)).toBe(true);
  });

  // Her sensor is read for whether it works, which shows folded; the rest is seldom changed, and on a
  // desktop opens over her figures, so it is never left open for the next visit.
  it("starts folded on every screen and every visit when the card says so", () => {
    expect(foldOpen(null, true, "folded")).toBe(false);
    expect(foldOpen(null, false, "folded")).toBe(false);
    expect(foldOpen("open", true, "folded")).toBe(false);
  });
});
