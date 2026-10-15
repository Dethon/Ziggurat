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

// While the keyboard is up, the page is marked (data-typing on <html>) so the tab bar can step aside
// and leave the field and the screen's one action in view. Up means a text field has focus and the
// window is clearly shorter than it was before (the viewport resizes with the keyboard, see
// index.html): Android's back button closes the keyboard and leaves the focus where it was, and the
// tab bar must come back then.
export function keyboardUp(focused: unknown, height: number, before: number): boolean {
  return bringsKeyboard(focused) && height < before * 0.8;
}

export function useTypingMark() {
  useEffect(() => {
    const root = document.documentElement;
    // The window's height before the keyboard: followed while no text field has focus, so a turn of
    // the phone (or any other resize) is never taken for a keyboard.
    let before = window.innerHeight;
    const update = () => {
      const focused = document.activeElement;
      if (!bringsKeyboard(focused)) {
        before = window.innerHeight;
      }
      root.toggleAttribute("data-typing", keyboardUp(focused, window.innerHeight, before));
    };
    // Focus moves in two steps (out, then in); reading the result once both are done avoids a flicker.
    const later = () => window.setTimeout(update, 0);
    document.addEventListener("focusin", update);
    document.addEventListener("focusout", later);
    window.addEventListener("resize", update);
    return () => {
      document.removeEventListener("focusin", update);
      document.removeEventListener("focusout", later);
      window.removeEventListener("resize", update);
      root.removeAttribute("data-typing");
    };
  }, []);
}
