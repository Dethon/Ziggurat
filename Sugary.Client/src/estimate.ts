import { ApiError, type EstimateInput } from "./api";
import { clarifications, counted, type Draft } from "./draft";
import type { MealEstimate } from "./types";

// Her answers go on the conversation the first estimate opened, so the agent estimates again with
// her photos and words still in front of it. A conversation the server no longer has open (it
// restarted, or the meal was left too long) is answered 410, and the meal is then estimated afresh
// with everything, as the standalone app always did.
export interface MealMedia {
  photos: Blob[];
  clip: Blob | null;
}

export async function estimateDraft(
  draft: Draft,
  { photos, clip }: MealMedia,
  language: string,
  send: (input: EstimateInput) => Promise<MealEstimate>,
): Promise<MealEstimate> {
  const fresh: EstimateInput = {
    photos,
    clip,
    description: draft.description,
    clarifications: clarifications(draft),
    counted: counted(draft),
    language,
    conversationId: null,
  };
  return continued(fresh, draft.conversation ?? null, send);
}

// The next turn of a conversation still open, or the estimate made afresh when it is not.
export async function continued(
  fresh: EstimateInput,
  conversation: string | null,
  send: (input: EstimateInput) => Promise<MealEstimate>,
): Promise<MealEstimate> {
  if (!conversation) {
    return send(fresh);
  }

  try {
    return await send({ ...fresh, photos: [], clip: null, description: "", conversationId: conversation });
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 410) {
      return send(fresh);
    }
    throw failure;
  }
}
