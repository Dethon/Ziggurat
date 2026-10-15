import { useEffect } from "react";

const NO_KEYBOARD = ["checkbox", "radio", "button", "submit", "reset", "range", "color", "file", "image", "hidden"];

// Fields that bring up the phone's keyboard; a checkbox or a button does not.
export function bringsKeyboard(element: unknown): boolean {
  if (typeof element !== "object" || element === null || !("tagName" in element)) {
    return false;
  }
  const field = element as { tagName: string; type?: string; readOnly?: boolean };
  if (field.readOnly) {
    return false;
  }
  return field.tagName === "TEXTAREA" || (field.tagName === "INPUT" && !NO_KEYBOARD.includes(field.type ?? "text"));
}

// How much of the bottom of the page the keyboard covers. Safari on the iPhone leaves the page (the
// layout viewport) as it is and shrinks only what is visible (the visual viewport), so the keyboard
// is the gap under it; Chrome on Android resizes the page itself (index.html asks it to), leaving
// no gap.
export function keyboardInset(innerHeight: number, visual: { height: number; offsetTop: number } | null): number {
  if (!visual) {
    return 0;
  }
  return Math.max(0, Math.round(innerHeight - (visual.offsetTop + visual.height)));
}

// The keyboard is up when a text field has focus and either the visible part of the page has lost a
// keyboard's height (iPhone), or the page itself is clearly shorter than before (Android). Android's
// back button closes the keyboard and keeps the focus, and the tab bar must come back then.
export function keyboardUp(focused: unknown, page: { height: number; before: number; inset: number }): boolean {
  return bringsKeyboard(focused) && (page.inset > 120 || page.height < page.before * 0.8);
}

// While the keyboard is up, the page is marked (data-typing on <html>) and told how tall the
// keyboard is (--keyboard), so the tab bar steps aside and what is pinned to the bottom (the screen's
// action, a sheet, the sign-in button, a toast) sits on top of the keyboard instead of behind it.
export function useTypingMark() {
  useEffect(() => {
    const root = document.documentElement;
    const visual = window.visualViewport;
    // The page's height before the keyboard: followed while no text field has focus, so a turn of
    // the phone (or any other resize) is never taken for a keyboard.
    let before = window.innerHeight;
    const update = () => {
      const focused = document.activeElement;
      if (!bringsKeyboard(focused)) {
        before = window.innerHeight;
      }
      const inset = keyboardInset(window.innerHeight, visual);
      const up = keyboardUp(focused, { height: window.innerHeight, before, inset });
      root.toggleAttribute("data-typing", up);
      root.style.setProperty("--keyboard", `${up ? inset : 0}px`);
    };
    // Focus moves in two steps (out, then in); reading the result once both are done avoids a flicker.
    const later = () => window.setTimeout(update, 0);
    document.addEventListener("focusin", update);
    document.addEventListener("focusout", later);
    window.addEventListener("resize", update);
    visual?.addEventListener("resize", update);
    visual?.addEventListener("scroll", update);
    return () => {
      document.removeEventListener("focusin", update);
      document.removeEventListener("focusout", later);
      window.removeEventListener("resize", update);
      visual?.removeEventListener("resize", update);
      visual?.removeEventListener("scroll", update);
      root.removeAttribute("data-typing");
      root.style.removeProperty("--keyboard");
    };
  }, []);
}
