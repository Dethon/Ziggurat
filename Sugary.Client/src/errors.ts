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
