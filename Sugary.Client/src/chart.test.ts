import { describe, expect, it } from "vitest";
import { chartBox } from "./chart";

const glucose = { width: 320, height: 180 };

describe("a chart's drawing box", () => {
  it("is drawn at its own size until its card has been measured", () => {
    expect(chartBox(0, glucose)).toEqual(glucose);
  });

  it("is only scaled on a phone, so a phone sees it as it always has", () => {
    expect(chartBox(326, glucose)).toEqual(glucose);
    expect(chartBox(368, glucose)).toEqual(glucose);
  });

  it("stops growing past a phone's scale on a wider card and draws more of itself, so its words stay a reading size", () => {
    const box = chartBox(690, glucose);
    expect(box.width).toBe(600);
    expect(690 / box.width).toBeCloseTo(1.15);
  });

  it("grows taller with its width, keeping its shape, up to half again its height", () => {
    expect(chartBox(460, glucose).height).toBe(225);
    expect(chartBox(1200, glucose).height).toBe(270);
  });

  it("grows taller still where its screen has the room, still keeping its shape until then", () => {
    expect(chartBox(1200, glucose, 2.2).height).toBe(396);
    expect(chartBox(460, glucose, 2.2).height).toBe(225);
  });
});
