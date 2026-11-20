// Whether a folding card (components/Fold.tsx) opens: as she last left it on this device, else folded
// on a phone, whose screen is short, and open on a desktop, which has the room. A card that starts
// "folded" does so on every screen and every visit: what it is read for shows folded, and what folds
// is a tap she takes when she means to change something.
export type FoldStart = "by-screen" | "folded";

export function foldOpen(stored: string | null, desktop: boolean, start: FoldStart = "by-screen"): boolean {
  if (start === "folded") {
    return false;
  }
  return stored === null ? desktop : stored === "open";
}
