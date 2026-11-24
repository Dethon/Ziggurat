import { asDayNote, kept } from "./days";
import type {
  CyclePhases,
  DayEntry,
  DayNote,
  Dose,
  Exercise,
  Food,
  GlucoseCurve,
  GlucoseLink,
  GlucoseNow,
  GlucoseSettings,
  GlucoseStatistics,
  GlucoseView,
  LinkGlucose,
  Meal,
  MealEstimate,
  PackagedFood,
  Pen,
  Profile,
  Measurement,
  SaveMeasurement,
  Period,
  Recipe,
  SaveDose,
  SaveExercise,
  SaveFood,
  SaveMeal,
  SavePen,
  SavePeriod,
  SavePushSubscription,
  SaveRecipe,
  SessionState,
  WidgetMinted,
  WidgetState,
} from "./types";

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    // The problem's own code, where the route words its refusals by one (the glucose link's).
    readonly code: string | null = null,
  ) {
    super(message);
  }
}

export const UNAUTHORIZED_EVENT = "sugary:unauthorized";
export const SUSPENDED_EVENT = "sugary:suspended";

// The proxy's session check refuses a suspended household's every call 403 and says so in a header
// of its own, which the app's own 403s (Sugary turned off, a refused key) never carry.
export function isSuspension(response: Response): boolean {
  return response.status === 403 && response.headers.get("X-Ziggurat-Refusal") === "suspended";
}

// Whose log the app is showing, when it is somebody else's (a reader an owner let read everyone's):
// every address the app asks names that person, so the server reads their log, read only. Null is
// her own, named by nothing. Set before the screens of that log mount, so none asks for the wrong one.
let viewing: string | null = null;

export function setViewing(userId: string | null): void {
  viewing = userId;
}

// The pantry and what fills it are the household's, whoever's log is open: asked, and changed, as nobody's.
const HOUSEHOLD = ["/api/foods", "/api/recipes", "/api/products", "/api/labels"];

function household(address: string): boolean {
  const path = address.split("?")[0];
  return HOUSEHOLD.some((root) => path === root || path.startsWith(`${root}/`));
}

export function withPerson(address: string, person: string | null = viewing): string {
  return person === null || household(address)
    ? address
    : `${address}${address.includes("?") ? "&" : "?"}person=${encodeURIComponent(person)}`;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(withPerson(`/api${path}`), { credentials: "same-origin", ...init });
  } catch {
    throw new ApiError(0, "");
  }

  // The proxy answers a call whose session is gone 401, never a redirect, so the page can send the
  // person to sign in (App).
  if (response.status === 401) {
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  }
  if (isSuspension(response)) {
    window.dispatchEvent(new Event(SUSPENDED_EVENT));
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new ApiError(response.status, problem?.detail ?? problem?.title ?? "", typeof problem?.code === "string" ? problem.code : null);
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

// The most the server answers in one page of doses or sessions.
const RECORD_PAGE = 200;

export const api = {
  session: () => request<SessionState>("/session"),

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
  // `loaded` is what the sheet opened with: the server keeps whatever another phone changed since.
  updateMeal: (id: string, meal: SaveMeal, loaded: SaveMeal) =>
    request<Meal>(`/meals/${id}`, json("PUT", { ...meal, loaded })),
  deleteMeal: (id: string) => request<void>(`/meals/${id}`, { method: "DELETE" }),
  photoUrl: (id: string) => withPerson(`/api/meals/${id}/photo`),
  // The photo kept with a meal, to send with a correction of it.
  async mealPhoto(id: string): Promise<Blob> {
    const response = await fetch(api.photoUrl(id), { credentials: "same-origin" });
    if (!response.ok) {
      throw new ApiError(response.status, "");
    }
    return response.blob();
  },

  doses: (from: string | null, skip = 0, take = RECORD_PAGE) =>
    request<Dose[]>(`/doses?skip=${skip}&take=${take}${from ? `&from=${encodeURIComponent(from)}` : ""}`),
  // Every dose since from (all of them, with none), newest first, a page at a time.
  async allDoses(from: string | null): Promise<Dose[]> {
    const doses: Dose[] = [];
    for (;;) {
      const page = await api.doses(from, doses.length);
      doses.push(...page);
      if (page.length < RECORD_PAGE) {
        return doses;
      }
    }
  },
  createDose: (dose: SaveDose) => request<Dose>("/doses", json("POST", dose)),
  updateDose: (id: string, dose: SaveDose, loaded: SaveDose) =>
    request<Dose>(`/doses/${id}`, json("PUT", { ...dose, loaded })),
  deleteDose: (id: string) => request<void>(`/doses/${id}`, { method: "DELETE" }),

  exercise: (from: string | null, skip = 0, take = RECORD_PAGE) =>
    request<Exercise[]>(`/exercise?skip=${skip}&take=${take}${from ? `&from=${encodeURIComponent(from)}` : ""}`),
  // Every session started since from (all of them, with none), newest first, a page at a time.
  async allExercise(from: string | null): Promise<Exercise[]> {
    const sessions: Exercise[] = [];
    for (;;) {
      const page = await api.exercise(from, sessions.length);
      sessions.push(...page);
      if (page.length < RECORD_PAGE) {
        return sessions;
      }
    }
  },
  createExercise: (session: SaveExercise) => request<Exercise>("/exercise", json("POST", session)),
  updateExercise: (id: string, session: SaveExercise, loaded: SaveExercise) =>
    request<Exercise>(`/exercise/${id}`, json("PUT", { ...session, loaded })),
  deleteExercise: (id: string) => request<void>(`/exercise/${id}`, { method: "DELETE" }),
  // What she called her other sessions, the latest first, for Otro to offer back.
  exerciseNames: () => request<string[]>("/exercise/names"),

  // All of them, newest first: a few a year, and the forecast reads every one.
  pens: () => request<Pen[]>("/pens"),
  createPen: (pen: SavePen) => request<Pen>("/pens", json("POST", pen)),
  updatePen: (id: string, pen: SavePen, loaded: SavePen) =>
    request<Pen>(`/pens/${id}`, json("PUT", { ...pen, loaded })),
  deletePen: (id: string) => request<void>(`/pens/${id}`, { method: "DELETE" }),
  push: () => request<{ publicKey: string | null }>("/push"),
  savePushSubscription: (subscription: SavePushSubscription) =>
    request<void>("/push/subscription", json("PUT", subscription)),
  deletePushSubscription: (endpoint: string) =>
    request<void>(`/push/subscription?endpoint=${encodeURIComponent(endpoint)}`, { method: "DELETE" }),
  profile: () => request<Profile>("/profile"),
  saveProfile: (profile: Profile, loaded: Profile) => request<Profile>("/profile", json("PUT", { ...profile, loaded })),
  measurements: () => request<Measurement[]>("/measurements"),
  createMeasurement: (measurement: SaveMeasurement) => request<Measurement>("/measurements", json("POST", measurement)),
  updateMeasurement: (id: string, measurement: SaveMeasurement, loaded: SaveMeasurement) =>
    request<Measurement>(`/measurements/${id}`, json("PUT", { ...measurement, loaded })),
  deleteMeasurement: (id: string) => request<void>(`/measurements/${id}`, { method: "DELETE" }),
  periods: () => request<Period[]>("/periods"),
  createPeriod: (period: SavePeriod) => request<Period>("/periods", json("POST", period)),
  updatePeriod: (id: string, period: SavePeriod, loaded: SavePeriod) =>
    request<Period>(`/periods/${id}`, json("PUT", { ...period, loaded })),
  deletePeriod: (id: string) => request<void>(`/periods/${id}`, { method: "DELETE" }),
  // Her newest reading with her link's state; then her curve over a window and a clinic's figures.
  widget: () => request<WidgetState>("/widget"),
  mintWidget: () => request<WidgetMinted>("/widget", { method: "PUT" }),
  removeWidget: () => request<void>("/widget", { method: "DELETE" }),
  glucoseNow: () => request<GlucoseNow>("/glucose/current"),
  glucoseCurve: (from: string, to: string, view: GlucoseView) =>
    request<GlucoseCurve>(`/glucose/readings?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&view=${view}`),
  glucoseStatistics: (days: number, view: GlucoseView) =>
    request<GlucoseStatistics>(`/glucose/statistics?days=${days}&view=${view}`),
  cyclePhases: () => request<CyclePhases>("/glucose/cycle"),
  cycleReminder: () => request<{ on: boolean }>("/cycle/reminder"),
  setCycleReminder: (on: boolean) => request<{ on: boolean }>("/cycle/reminder", json("PUT", { on })),
  // Logs in at LibreLinkUp before anything is kept, so it may take a while and is refused by code.
  linkGlucose: (account: LinkGlucose) => request<GlucoseLink>("/glucose/link", json("PUT", account)),
  chooseGlucoseConnection: (patientId: string) =>
    request<GlucoseLink>("/glucose/link/connection", json("PUT", { patientId })),
  saveGlucoseSettings: (settings: GlucoseSettings, loaded: GlucoseSettings) =>
    request<GlucoseLink>("/glucose/settings", json("PUT", { ...settings, loaded })),
  unlinkGlucose: () => request<void>("/glucose/link", { method: "DELETE" }),
  dayNotes: () => request<DayNote[]>("/day-notes").then((days) => days.map(asDayNote)),
  // A day with nothing said is deleted; the server then answers with nothing. Always sends its
  // symptoms, if none an empty list: a save without them changes only the words (an older app's).
  setDayNote: (day: string, entry: DayEntry, loaded: DayEntry) =>
    request<DayNote | undefined>(`/day-notes/${day}`, json("PUT", { ...kept(entry), loaded: kept(loaded) })),

  foods: () => request<Food[]>("/foods"),
  createFood: (food: SaveFood) => request<Food>("/foods", json("POST", food)),
  updateFood: (id: string, food: SaveFood, loaded: SaveFood) =>
    request<Food>(`/foods/${id}`, json("PUT", { ...food, loaded })),
  deleteFood: (id: string) => request<void>(`/foods/${id}`, { method: "DELETE" }),
  recipes: () => request<Recipe[]>("/recipes"),
  createRecipe: (recipe: SaveRecipe) => request<Recipe>("/recipes", json("POST", recipe)),
  updateRecipe: (id: string, recipe: SaveRecipe, loaded: SaveRecipe) =>
    request<Recipe>(`/recipes/${id}`, json("PUT", { ...recipe, loaded })),
  deleteRecipe: (id: string) => request<void>(`/recipes/${id}`, { method: "DELETE" }),

  // Each time carries its offset in the phone's own zone, so the file reads like the clock on the wall.
  exportUrl: (
    table: "meals" | "items" | "doses" | "exercise" | "periods" | "day-notes" | "foods" | "profile" | "heights" | "weights" | "pens",
  ) =>
    withPerson(`/api/export/${table}.csv?tz=${encodeURIComponent(Intl.DateTimeFormat().resolvedOptions().timeZone)}`),
};
