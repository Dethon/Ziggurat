import type { SessionState, SugaryPerson } from "./types";

// The session as the server answered it, with what an older server leaves out filled in: somebody
// using Sugary, with nobody else's log to look at.
export function sessionOf(answer: SessionState): SessionState & { usesSugary: boolean; people: SugaryPerson[] } {
  return { ...answer, usesSugary: answer.usesSugary ?? true, people: answer.people ?? [] };
}

// The logs the picker offers: everyone she may look at, herself only where she keeps a log.
export function offered(session: SessionState): SugaryPerson[] {
  const { usesSugary, people } = sessionOf(session);
  return people.filter((person) => usesSugary || person.userId !== session.person);
}

// Shown only when there is somebody else to look at: anyone else sees the app exactly as before.
export function showsPicker(session: SessionState): boolean {
  return offered(session).some((person) => person.userId !== session.person);
}

// Whose log the app opens on: the one she last picked in this tab (a reload keeps it) while it is still
// offered; else her own (null), or, for a reader keeping none, the first person offered.
export function firstViewed(session: SessionState, picked: string | null = null): string | null {
  const people = offered(session);
  if (picked !== null && people.some((person) => person.userId === picked)) {
    return picked === session.person ? null : picked;
  }
  return sessionOf(session).usesSugary ? null : (people[0]?.userId ?? null);
}

// Her pick, kept for this tab alone (sessionStorage: a reload, a pull-down to refresh, never the next
// time she opens the app) and under her own id, so somebody else signing in on the phone starts fresh.
const PICKED = "sugary.viewing";

export function rememberViewed(signer: string | null, viewed: string | null): void {
  try {
    if (signer === null || viewed === null) {
      sessionStorage.removeItem(PICKED);
    } else {
      sessionStorage.setItem(PICKED, JSON.stringify({ signer, viewed }));
    }
  } catch {
    // Storage refused (a private window): a reload opens the usual log.
  }
}

export function rememberedViewed(signer: string | null): string | null {
  try {
    const kept = JSON.parse(sessionStorage.getItem(PICKED) ?? "null") as { signer?: string; viewed?: string } | null;
    return kept && kept.signer === signer && typeof kept.viewed === "string" ? kept.viewed : null;
  } catch {
    return null;
  }
}

// Neither a log of her own nor anybody's to look at: Sugary is not on for her.
export function hasNoLog(session: SessionState): boolean {
  return !sessionOf(session).usesSugary && offered(session).length === 0;
}
