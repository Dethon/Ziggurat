import { useEffect, useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { endFrom, endsLater, EXERCISE_ICON, MAX_MINUTES, MAX_NAME, MAX_NOTE, readMinutes, sessionMinutes } from "../exercise";
import { toLocalInput } from "../format";
import { useSettings } from "../settings";
import type { Exercise, ExerciseKind, SaveExercise } from "../types";

const KINDS: ExerciseKind[] = ["walk", "gym", "other"];

// What the sheet was opened for: another kind to start now, a session she already did, or one she
// recorded, to change or delete (a running one included, which can be given its end here).
export type ExerciseOpening = { mode: "other" } | { mode: "past" } | { mode: "edit"; session: Exercise };

export function ExerciseSheet({
  opening,
  onClose,
  onSaved,
  onDelete,
}: {
  opening: ExerciseOpening;
  onClose: () => void;
  // Started says the session is running from now; saved, that it was added or changed.
  onSaved: (session: Exercise, how: "started" | "saved") => void;
  onDelete: (session: Exercise) => void;
}) {
  const { t } = useSettings();
  const session = opening.mode === "edit" ? opening.session : null;
  const starting = opening.mode === "other";
  const [kind, setKind] = useState<ExerciseKind>(session?.kind ?? (starting ? "other" : "walk"));
  const [name, setName] = useState(session?.name ?? "");
  // A session she already did most likely ended a little while ago; the time is hers to change.
  const [when, setWhen] = useState(() => toLocalInput(session?.startedAt ?? new Date(Date.now() - 3_600_000).toISOString()));
  // Her minutes are never guessed: a new session's start empty, a recorded one's are the ones it has.
  const [minutes, setMinutes] = useState(() => {
    const recorded = session ? sessionMinutes(session) : null;
    return recorded === null ? "" : String(recorded);
  });
  const [note, setNote] = useState(session?.note ?? "");
  const [names, setNames] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const minutesId = useId();

  useEffect(() => {
    api.exerciseNames().then(setNames, () => {});
  }, []);

  const named = kind !== "other" || name.trim() !== "";
  const startedAt = when ? new Date(when).toISOString() : null;
  // A running session keeps running while its minutes are left empty.
  const keepsRunning = session?.endedAt === null && minutes.trim() === "";
  const read = starting || keepsRunning ? null : readMinutes(minutes);
  const later = typeof read === "number" && startedAt !== null && endsLater(startedAt, read);
  const minutesProblem =
    typeof read === "string" && minutes.trim() !== "" ? t(read, { max: MAX_MINUTES }) : later ? t("exerciseEndsLater") : null;
  const ready = named && !busy && (starting || (startedAt !== null && (keepsRunning || (typeof read === "number" && !later))));

  async function save() {
    if (!ready) {
      return;
    }
    setBusy(true);
    setError(null);
    const values: SaveExercise = starting
      ? { kind: "other", name: name.trim(), startedAt: new Date().toISOString(), endedAt: null, note: null }
      : {
          kind,
          name: kind === "other" ? name.trim() : null,
          startedAt: startedAt!,
          endedAt: typeof read === "number" ? endFrom(startedAt!, read) : null,
          note: note.trim() || null,
        };
    try {
      const saved = await (session ? api.updateExercise(session.id, values) : api.createExercise(values));
      onSaved(saved, starting ? "started" : "saved");
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  const title = starting ? "exerciseOtherTitle" : session ? "exerciseEditTitle" : "exercisePastTitle";

  return (
    <Sheet
      title={t(title)}
      onClose={onClose}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={!ready}>
          {t(starting ? "exerciseStartNow" : "exerciseSave")}
        </button>
      }
    >
      <form
        id={formId}
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        {!starting && (
          <fieldset className="question">
            <legend>{t("exerciseKind")}</legend>
            <div className="segmented exercise-kinds">
              {KINDS.map((option) => (
                <button
                  key={option}
                  type="button"
                  className={`segment${kind === option ? " segment-on" : ""}`}
                  aria-pressed={kind === option}
                  onClick={() => setKind(option)}
                >
                  <Icon name={EXERCISE_ICON[option]} size={20} />
                  {t(`exercise_${option}`)}
                </button>
              ))}
            </div>
          </fieldset>
        )}
        {kind === "other" && (
          <div className="exercise-name">
            <label className="field">
              <span className="field-label">{t("exerciseName")}</span>
              <input
                value={name}
                maxLength={MAX_NAME}
                placeholder={t("exerciseNamePlaceholder")}
                autoFocus={starting}
                onChange={(event) => setName(event.target.value)}
              />
            </label>
            {names.length > 0 && (
              <div className="chips">
                {names.map((used) => (
                  <button
                    key={used}
                    type="button"
                    className={`chip${used === name.trim() ? " chip-on" : ""}`}
                    aria-pressed={used === name.trim()}
                    onClick={() => setName(used)}
                  >
                    {used}
                  </button>
                ))}
              </div>
            )}
          </div>
        )}
        {!starting && (
          <>
            <label className="field">
              <span className="field-label">{t("exerciseStart")}</span>
              <input type="datetime-local" required value={when} onChange={(event) => setWhen(event.target.value)} />
            </label>
            <div className="field">
              <label className="field-label" htmlFor={minutesId}>
                {t("exerciseMinutes")}
              </label>
              <input
                id={minutesId}
                inputMode="numeric"
                value={minutes}
                aria-invalid={minutesProblem !== null}
                onChange={(event) => setMinutes(event.target.value)}
              />
              {session?.endedAt === null && <span className="muted small">{t("exerciseMinutesRunningHint")}</span>}
              {minutesProblem && (
                <span className="field-problem" role="alert">
                  {minutesProblem}
                </span>
              )}
            </div>
            <label className="field">
              <span className="field-label">{t("exerciseNote")}</span>
              <input
                value={note}
                maxLength={MAX_NOTE}
                placeholder={t("exerciseNotePlaceholder")}
                onChange={(event) => setNote(event.target.value)}
              />
            </label>
          </>
        )}
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {session && (
          <button type="button" className="button button-quiet danger" disabled={busy} onClick={() => onDelete(session)}>
            <Icon name="trash" size={20} />
            {t("deleteExercise")}
          </button>
        )}
      </form>
    </Sheet>
  );
}
