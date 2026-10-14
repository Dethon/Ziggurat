import { ApiError, type EstimateInput } from "./api";
import { clarifications, counted, type Draft } from "./draft";
import type { MealEstimate } from "./types";

// Her answers go on the conversation the first estimate opened, so the agent estimates again with
// her photos and words still in front of it. A conversation the server no longer has open (it
// restarted, or the meal was left too long) is answered 410, and the meal is then estimated afresh
// with everything, as the standalone app always did.
export async function estimateDraft(
  draft: Draft,
  photos: Blob[],
  language: string,
  send: (input: EstimateInput) => Promise<MealEstimate>,
): Promise<MealEstimate> {
  const fresh: EstimateInput = {
    photos,
    description: draft.description,
    clarifications: clarifications(draft),
    counted: counted(draft),
    language,
    conversationId: null,
  };
  if (!draft.conversation) {
    return send(fresh);
  }

  try {
    return await send({ ...fresh, photos: [], description: "", conversationId: draft.conversation });
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 410) {
      return send(fresh);
    }
    throw failure;
  }
}
