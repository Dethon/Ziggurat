import { useSyncExternalStore } from "react";

// A window wide enough to set the meal beside the log (tabs.ts, fuses). The same literal opens
// styles.css's "A wide window": a media query reads no custom property, and the rail's entries and the
// page's columns must change together.
export const WIDE = "(min-width: 90rem)";

function subscribe(changed: () => void): () => void {
  const query = matchMedia(WIDE);
  query.addEventListener("change", changed);
  return () => query.removeEventListener("change", changed);
}

export function useWide(): boolean {
  return useSyncExternalStore(subscribe, () => matchMedia(WIDE).matches);
}
