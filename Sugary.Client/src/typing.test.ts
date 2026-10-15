import { describe, expect, it } from "vitest";
import { bringsKeyboard, keyboardInset, keyboardUp } from "./typing";

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

describe("keyboardInset", () => {
  it("is the gap under the visible part of the page: the iPhone's keyboard", () => {
    expect(keyboardInset(844, { height: 508, offsetTop: 0 })).toBe(336);
  });

  it("follows Safari scrolling the visible part up to the field", () => {
    expect(keyboardInset(844, { height: 508, offsetTop: 120 })).toBe(216);
  });

  it("is nothing when the page itself was resized (Android) or there is no visual viewport", () => {
    expect(keyboardInset(500, { height: 500, offsetTop: 0 })).toBe(0);
    expect(keyboardInset(844, null)).toBe(0);
  });
});

describe("keyboardUp", () => {
  const field = { tagName: "TEXTAREA", readOnly: false };

  it("is up on an iPhone: the page keeps its height and the keyboard covers its bottom", () => {
    expect(keyboardUp(field, { height: 844, before: 844, inset: 336 })).toBe(true);
  });

  it("is up on Android: the page itself shrinks", () => {
    expect(keyboardUp(field, { height: 500, before: 844, inset: 0 })).toBe(true);
  });

  it("is down once the keyboard closes, though the field keeps its focus", () => {
    expect(keyboardUp(field, { height: 844, before: 844, inset: 0 })).toBe(false);
  });

  it("is down when the focus is on anything without a keyboard", () => {
    expect(keyboardUp({ tagName: "BUTTON" }, { height: 500, before: 844, inset: 336 })).toBe(false);
  });
});
