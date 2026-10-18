import { formatGrams } from "./carbs";
import { answerLines, countedLines, editFact } from "./draft";
import type { EstimateInput } from "./api";
import type { DraftItem } from "./types";

// A meal already in her log, asked about again: a food she forgot, one food told in her words, or
// the whole meal told in her words. The conversation its first estimate opened is long gone, so
// each starts a new one from what the log holds, the photo kept with the meal among it.
export type Revision = { kind: "add" } | { kind: "food"; id: string } | { kind: "meal" };

export interface ReviseRequest {
  // What she wrote: the food she forgot, or what was off.
  words: string;
  // Photos or a clip she took of a food she forgot. Only adding takes them; a correction sends the
  // meal's own photo.
  photos: Blob[];
  clip: Blob | null;
  answers: Record<string, string>;
}

// A food as the log has it, the way she would say it: its name, its portion and its weight.
function logged(item: DraftItem): string {
  const weight = item.grams === null ? null : `(${formatGrams(item.grams)} g)`;
  return [[item.name, item.portion].filter(Boolean).join(", "), weight].filter(Boolean).join(" ");
}

const facts = (items: readonly DraftItem[]) => items.filter((item) => item.source === "estimate" && item.edited).map(editFact);

// The request made afresh. Sent through `continued`, which puts her answers on the conversation the
// first answer opened while it is open.
export function revisionInput(
  revision: Revision,
  items: readonly DraftItem[],
  request: ReviseRequest,
  storedPhoto: Blob | null,
  language: string,
): EstimateInput {
  const correction = request.words.trim() === "" ? [] : [`Correction: ${request.words.trim()}`];
  const asked = (described: readonly DraftItem[], counted: readonly DraftItem[]) => ({
    photos: storedPhoto ? [storedPhoto] : [],
    clip: null,
    description: described.map(logged).join("; "),
    clarifications: [...answerLines(request.answers), ...facts(described), ...correction].join("\n"),
    counted: countedLines(counted),
  });
  const fresh =
    revision.kind === "add"
      ? {
          photos: request.photos,
          clip: request.clip,
          description: request.words.trim(),
          clarifications: answerLines(request.answers).join("\n"),
          counted: countedLines(items),
        }
      : revision.kind === "food"
        ? asked(
            items.filter((item) => item.id === revision.id),
            items.filter((item) => item.id !== revision.id),
          )
        : asked(
            items.filter((item) => item.source === "estimate"),
            items.filter((item) => item.source !== "estimate"),
          );
  return { ...fresh, language, conversationId: null };
}

// The meal's foods with what the model found in: a forgotten food after the rest, a food replaced
// where it was, or the estimated foods replaced and what she counted herself kept.
export function applied(revision: Revision, items: readonly DraftItem[], found: readonly DraftItem[]): DraftItem[] {
  switch (revision.kind) {
    case "add":
      return [...items, ...found];
    case "food":
      return items.flatMap((item) => (item.id === revision.id ? found : [item]));
    case "meal":
      return [...found, ...items.filter((item) => item.source !== "estimate")];
  }
}
