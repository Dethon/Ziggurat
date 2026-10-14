import type { Food, Meal, MealEstimate, PackagedFood, SaveFood, SaveMeal, SessionState } from "./types";

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export const UNAUTHORIZED_EVENT = "sugary:unauthorized";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api${path}`, { credentials: "same-origin", ...init });
  } catch {
    throw new ApiError(0, "");
  }

  if (response.status === 401 && !path.startsWith("/session")) {
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new ApiError(response.status, problem?.detail ?? problem?.title ?? "");
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

export interface EstimateInput {
  photos: Blob[];
  // A short film around the plate, as the phone recorded it. The server makes it small, drops its
  // sound and takes stills from it, so it goes as it is.
  clip: Blob | null;
  description: string;
  clarifications: string;
  // What the person has already added themselves, so the model leaves it out.
  counted: string;
  language: string;
  // The conversation the meal's first estimate opened. Set, the request is her answers to it.
  conversationId: string | null;
}

// Her answers continue the conversation, whose first message already holds the photos, the clip
// and her words, so a follow-up sends none of them again.
export function estimateForm(input: EstimateInput): FormData {
  const form = new FormData();
  if (input.conversationId) {
    form.append("conversationId", input.conversationId);
  } else {
    input.photos.forEach((photo, index) => form.append("photos", photo, `meal-${index}.jpg`));
    if (input.clip) {
      form.append("clip", input.clip, input.clip instanceof File ? input.clip.name : "meal.mp4");
    }
    form.append("description", input.description);
  }
  form.append("clarifications", input.clarifications);
  form.append("counted", input.counted);
  form.append("language", input.language);
  return form;
}

export const api = {
  session: () => request<SessionState>("/session"),
  signIn: (code: string) => request<void>("/session", json("POST", { code })),
  signOut: () => request<void>("/session", { method: "DELETE" }),

  estimate: (input: EstimateInput, signal?: AbortSignal) =>
    request<MealEstimate>("/estimates", { method: "POST", body: estimateForm(input), signal }),

  readLabel(photo: Blob, language: string) {
    const form = new FormData();
    form.append("photo", photo, "label.jpg");
    form.append("language", language);
    return request<PackagedFood>("/labels", { method: "POST", body: form });
  },

  product: (barcode: string) => request<PackagedFood>(`/products/${encodeURIComponent(barcode)}`),

  meals: (skip = 0, take = 50) => request<Meal[]>(`/meals?skip=${skip}&take=${take}`),
  createMeal: (meal: SaveMeal) => request<Meal>("/meals", json("POST", meal)),
  updateMeal: (id: string, meal: SaveMeal) => request<Meal>(`/meals/${id}`, json("PUT", meal)),
  deleteMeal: (id: string) => request<void>(`/meals/${id}`, { method: "DELETE" }),
  photoUrl: (id: string) => `/api/meals/${id}/photo`,

  foods: () => request<Food[]>("/foods"),
  createFood: (food: SaveFood) => request<Food>("/foods", json("POST", food)),
  updateFood: (id: string, food: SaveFood) => request<Food>(`/foods/${id}`, json("PUT", food)),
  deleteFood: (id: string) => request<void>(`/foods/${id}`, { method: "DELETE" }),

  // Each time carries its offset in the phone's own zone, so the file reads like the clock on the wall.
  exportUrl: (table: "meals" | "items" | "foods") =>
    `/api/export/${table}.csv?tz=${encodeURIComponent(Intl.DateTimeFormat().resolvedOptions().timeZone)}`,
};
