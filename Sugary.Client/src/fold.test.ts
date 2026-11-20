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
});
