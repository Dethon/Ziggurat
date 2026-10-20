import { describe, expect, it } from "vitest";
import { changedFrom, snapshot } from "./edited";

describe("a sheet opened on something saved", () => {
  const opened = snapshot({ units: 4, kind: "rapid", note: null });

  it("is unchanged while it would save what it opened with", () => {
    expect(changedFrom(opened, { units: 4, kind: "rapid", note: null })).toBe(false);
  });

  it("is changed once any value it would save differs", () => {
    expect(changedFrom(opened, { units: 4.5, kind: "rapid", note: null })).toBe(true);
    expect(changedFrom(opened, { units: 4, kind: "rapid", note: "late" })).toBe(true);
  });

  it("is unchanged again when a value is put back", () => {
    expect(changedFrom(opened, { units: 4, kind: "rapid", note: null })).toBe(false);
  });
});
