import { useState } from "react";

// A sheet opened on something she saved offers Save only once it would save something else: the
// values it would send now, against those it would have sent as it opened.
export const snapshot = (values: unknown): string => JSON.stringify(values);

export const changedFrom = (opened: string, values: unknown): boolean => snapshot(values) !== opened;

export function useEdited(values: unknown): boolean {
  const [opened] = useState(() => snapshot(values));
  return changedFrom(opened, values);
}
