import { describe, expect, it } from "vitest";
import { axisEnd } from "./TotalCard";

describe("axisEnd", () => {
  it("keeps the range bar on a fixed 0–150 g axis, so a narrower range looks narrower", () => {
    expect(axisEnd(56)).toBe(150);
    expect(axisEnd(100)).toBe(150);
  });

  it("grows in steps of 50 g for a bigger meal", () => {
    expect(axisEnd(163)).toBe(200);
    expect(axisEnd(200)).toBe(200);
  });
});
