import { describe, expect, it } from "vitest";
import { widgetFormulas } from "./widget";

const ORIGIN = "https://sugary.example";
const NOW = `${ORIGIN}/widget/tok-en_1/now.json`;

describe("widgetFormulas", () => {
  const formulas = widgetFormulas(ORIGIN, "tok-en_1");

  it("reads each field from the widget's address", () => {
    expect(formulas.reading).toBe(`$wg("${NOW}", json, .value)$`);
    expect(formulas.unit).toBe(`$wg("${NOW}", json, .unit)$`);
    expect(formulas.arrow).toBe(`$wg("${NOW}", json, .arrow)$`);
    expect(formulas.color).toBe(`$wg("${NOW}", json, .color)$`);
  });

  it("works the age out on the phone, from the reading's instant", () => {
    expect(formulas.age).toBe(`$tf(dp(wg("${NOW}", json, .at), auto))$`);
  });

  it("gives the curve as a plain image address", () => {
    expect(formulas.curve).toBe(`${ORIGIN}/widget/tok-en_1/curve.png?hours=6&width=800&height=320&theme=dark`);
  });
});
