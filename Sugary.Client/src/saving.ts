import { useState } from "react";
import { errorMessage, type Asking } from "./errors";
import type { Translate } from "./i18n";

// A sheet's save or delete: busy while it runs, its failure said in her language, and the sheet
// handed what the server answered once it succeeds.
export function useSaving(t: Translate) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run<T>(work: () => Promise<T>, done: (result: T) => void, asking: Asking = "save") {
    setBusy(true);
    setError(null);
    try {
      done(await work());
    } catch (failure) {
      setError(errorMessage(failure, t, asking));
      setBusy(false);
    }
  }

  return { busy, error, setError, run };
}
