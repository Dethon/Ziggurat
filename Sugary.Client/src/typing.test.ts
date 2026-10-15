import { describe, expect, it } from "vitest";
import { bringsKeyboard, keyboardUp } from "./typing";

describe("bringsKeyboard", () => {
  it("is true for text fields and false for controls without a keyboard", () => {
    const input = (type: string) => ({ tagName: "INPUT", type, readOnly: false });
    expect(bringsKeyboard({ tagName: "TEXTAREA", readOnly: false })).toBe(true);
    expect(bringsKeyboard(input("text"))).toBe(true);
    expect(bringsKeyboard(input("search"))).toBe(true);
    expect(bringsKeyboard(input("checkbox"))).toBe(false);
    expect(bringsKeyboard({ tagName: "INPUT", type: "text", readOnly: true })).toBe(false);
    expect(bringsKeyboard({ tagName: "BUTTON", type: "button" })).toBe(false);
    expect(bringsKeyboard(null)).toBe(false);
  });
});

describe("keyboardUp", () => {
  const field = { tagName: "TEXTAREA", readOnly: false };

  it("is up while a text field has focus and the window has shrunk", () => {
    expect(keyboardUp(field, 500, 844)).toBe(true);
  });

  it("is down once the keyboard closes, though the field keeps its focus", () => {
    expect(keyboardUp(field, 844, 844)).toBe(false);
  });

  it("is down when the focus is on anything without a keyboard", () => {
    expect(keyboardUp({ tagName: "BUTTON" }, 500, 844)).toBe(false);
  });
});
