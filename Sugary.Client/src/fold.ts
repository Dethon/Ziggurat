// Whether a folding card (components/Fold.tsx) opens: as she last left it on this device, else folded
// on a phone, whose screen is short, and open on a desktop, which has the room.
export function foldOpen(stored: string | null, desktop: boolean): boolean {
  return stored === null ? desktop : stored === "open";
}
