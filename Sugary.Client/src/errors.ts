import { ApiError } from "./api";
import type { StringKey, Translate } from "./i18n";

// What the request was for, which decides how a failure is said. The server words its problems in
// English, or not at all, so none of its sentences is shown: every failure is said here, in her
// language. The one exception is a label the model could not read, whose reason the model wrote
// in her language.
export type Asking = "estimate" | "label" | "product" | "save" | "deleteFood" | "load";

const SAID: Record<Asking, Partial<Record<number, StringKey>>> = {
  // 400: a photo or clip refused (type, size, length, unreadable); 500: clips cannot be read here.
  estimate: { 400: "errorMediaRefused", 500: "errorClipsDown", 502: "errorUnreadable", 504: "errorTimeout" },
  label: { 400: "labelRefused", 502: "labelNoAnswer", 504: "labelTimeout" },
  product: { 400: "productBadCode", 502: "productNoAnswer", 504: "productNoAnswer" },
  save: { 400: "errorNotSaved" },
  // 409: a food a recipe still uses, deleted from a pantry that had not heard of the recipe.
  deleteFood: { 409: "foodInRecipe" },
  load: {},
};

export function errorMessage(error: unknown, t: Translate, asking: Asking = "load"): string {
  if (error instanceof ApiError) {
    if (error.status === 0) {
      return t("errorOffline");
    }
    if (error.status === 503) {
      return t("errorNotConfigured");
    }
    const said = SAID[asking][error.status];
    if (said) {
      return t(said);
    }
  }
  return t("errorGeneric");
}

// A label the model could not read is an answer, not a failure: its reason is shown as it came (the
// model writes it in her language), and with none the app says so.
export function labelFailureMessage(error: unknown, t: Translate): string {
  if (error instanceof ApiError && error.status === 422) {
    return error.message || t("labelUnreadable");
  }
  return errorMessage(error, t, "label");
}
