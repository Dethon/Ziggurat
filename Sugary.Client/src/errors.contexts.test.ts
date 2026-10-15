import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { errorMessage, labelFailureMessage, type Asking } from "./errors";
import { dictionaries, translator } from "./i18n";

const es = translator("es");
const spanish = new Set(Object.values(dictionaries.es));

// Whatever the server says, in whatever status, her screen shows a sentence of the app's own.
describe("no server sentence reaches the screen", () => {
  const sentences = [
    [400, "That is not a product barcode."],
    [400, "At most 4 photos per meal."],
    [400, "That clip could not be read. Film it again, or take a photo instead."],
    [404, "Not Found"],
    [409, "Conflict"],
    [410, "This estimate is no longer open, so the meal has to be estimated again."],
    [500, "Clips cannot be read on this server right now. Take a photo instead, or describe the meal."],
    [502, "The product database could not be reached."],
    [502, "The model returned no answer."],
    [504, "Reading the label took too long. Try again, or add the food by hand."],
  ] as const;
  const askings: Asking[] = ["estimate", "label", "product", "save", "load", "signIn"];

  it.each(askings)("asking for %s", (asking) => {
    for (const [status, sentence] of sentences) {
      const said = errorMessage(new ApiError(status, sentence), es, asking);
      expect(said).not.toBe(sentence);
      expect(spanish.has(said)).toBe(true);
    }
  });

  it("says a failed product lookup as one, not as a label", () => {
    expect(errorMessage(new ApiError(502, "The product database could not be reached."), es, "product")).toBe(es("productNoAnswer"));
    expect(errorMessage(new ApiError(400, "That is not a product barcode."), es, "product")).toBe(es("productBadCode"));
  });

  it("keeps the model's own reason for a label it could not read, in her language", () => {
    expect(labelFailureMessage(new ApiError(422, "Esto es una paella, no una etiqueta."), es)).toBe("Esto es una paella, no una etiqueta.");
  });
});
