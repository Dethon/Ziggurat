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

// Whose log the app opens on: her own (null), or, for a reader keeping none, the first person offered.
export function firstViewed(session: SessionState): string | null {
  return sessionOf(session).usesSugary ? null : (offered(session)[0]?.userId ?? null);
}

// Neither a log of her own nor anybody's to look at: Sugary is not on for her.
export function hasNoLog(session: SessionState): boolean {
  return !sessionOf(session).usesSugary && offered(session).length === 0;
}
