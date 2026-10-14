import { ApiError } from "./api";
import type { Translate } from "./i18n";

export function errorMessage(error: unknown, t: Translate): string {
  if (error instanceof ApiError) {
    if (error.status === 0) {
      return t("errorOffline");
    }
    if (error.status === 503) {
      return t("errorNotConfigured");
    }
    if (error.message) {
      return error.message;
    }
  }
  return t("errorGeneric");
}

// A label the model could not read is an answer, not a failure: its reason is shown as it came, and
// with none the app says so in her language. The server words no reason of its own for that, so an
// English sentence never reaches a Spanish screen.
export function labelFailureMessage(error: unknown, t: Translate): string {
  if (error instanceof ApiError && error.status === 422) {
    return error.message || t("labelUnreadable");
  }
  return errorMessage(error, t);
}
