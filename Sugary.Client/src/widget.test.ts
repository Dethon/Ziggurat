import { describe, expect, it } from "vitest";
import { widgetImageAddress } from "./widget";

describe("widgetImageAddress", () => {
  const address = widgetImageAddress("https://sugary.example", "tok-en_1");

  it("asks for the card the server draws", () => {
    expect(address.startsWith("https://sugary.example/widget/tok-en_1/card.png?")).toBe(true);
    expect(address).toContain("hours=6&width=800&height=320&theme=dark");
  });

  it("changes every minute and never repeats, so KWGT cannot answer it from its cache", () => {
    expect(address.endsWith("&t=$df(yyMMddHHmm)$")).toBe(true);
  });
});
