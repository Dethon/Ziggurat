import { useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import { api } from "../api";
import { fromEstimate, shownTotal } from "../carbs";
import { CameraButton, CameraFallbackNote } from "../components/Camera";
import { Icon } from "../components/Icon";
import { ItemLine } from "../components/ItemRow";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { continued } from "../estimate";
import { forUpload } from "../images";
import { bindUnits, sentence } from "../meal";
import { applied, revisionInput, type Revision } from "../revise";
import { useSettings } from "../settings";
import type { DraftItem, Meal, MealEstimate } from "../types";

const MAX_PHOTOS = 4;

interface Photo {
  blob: Blob;
  url: string;
}

// A logged meal asked about again: what she forgot, from a photo or her words, or what was off
// about one food or the whole meal, in her words with the meal's own photo. What the model answers
// is shown first, with its questions, and goes into the meal only when she takes it.
export function ReviseSheet({
  revision,
  meal,
  items,
  onApply,
  onClose,
}: {
  revision: Revision;
  meal: Meal;
  items: readonly DraftItem[];
  onApply: (items: DraftItem[]) => void;
  onClose: () => void;
}) {
  const { t, language } = useSettings();
  const [words, setWords] = useState("");
  const [photos, setPhotos] = useState<Photo[]>([]);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [unsentAnswers, setUnsentAnswers] = useState(false);
  const [estimate, setEstimate] = useState<MealEstimate | null>(null);
  const [running, setRunning] = useState<AbortController | null>(null);
  const [error, setError] = useState<string | null>(null);
  const livePhotos = useRef(photos);
  livePhotos.current = photos;

  useEffect(
    () => () => {
      livePhotos.current.forEach((photo) => URL.revokeObjectURL(photo.url));
    },
    [],
  );

  const target = revision.kind === "food" ? items.find((item) => item.id === revision.id) : undefined;
  const adding = revision.kind === "add";
  const title = adding ? t("reviseAddTitle") : target ? sentence(target.name) : t("reviseMealTitle");
  const canAsk = adding ? words.trim() !== "" || photos.length > 0 : words.trim() !== "";
  // Made once per answer, so the foods she takes are the ones she was shown.
  const found = useMemo(() => estimate?.items.map(fromEstimate) ?? null, [estimate]);
  const next = found ? applied(revision, items, found) : null;

  async function addPhotos(picked: Blob[]) {
    try {
      const blobs = await Promise.all(picked.slice(0, MAX_PHOTOS - photos.length).map(forUpload));
      setPhotos((current) => [...current, ...blobs.map((blob) => ({ blob, url: URL.createObjectURL(blob) }))]);
    } catch {
      setError(t("errorGeneric"));
    }
  }

  function pickPhotos(event: ChangeEvent<HTMLInputElement>) {
    const files = [...(event.target.files ?? [])];
    event.target.value = "";
    void addPhotos(files);
  }

  function removePhoto(photo: Photo) {
    URL.revokeObjectURL(photo.url);
    setPhotos((current) => current.filter((p) => p !== photo));
  }

  async function ask(answered: Record<string, string>, conversation: string | null) {
    const controller = new AbortController();
    setRunning(controller);
    setError(null);
    try {
      // A correction goes with the meal's own photo, when it has one.
      // Without it, if it cannot be fetched: her words lead a correction.
      const stored = !adding && meal.hasPhoto ? await api.mealPhoto(meal.id).catch(() => null) : null;
      const result = await continued(
        revisionInput(revision, items, { words, photos: photos.map((photo) => photo.blob), answers: answered }, stored, language),
        conversation,
        (input) => api.estimate(input, controller.signal),
      );
      if (result.items.length === 0) {
        setError([t("noFood"), ...result.notes].join(" "));
      } else {
        setEstimate({ ...result, questions: result.questions.filter((q) => !(q.text in answered)) });
        setUnsentAnswers(false);
      }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(errorMessage(failure, t, "estimate"));
      }
    } finally {
      setRunning(null);
    }
  }

  function close() {
    running?.abort();
    onClose();
  }

  const footer = running ? (
    <button type="button" className="button button-secondary button-big" onClick={() => running.abort()}>
      {t("cancel")}
    </button>
  ) : estimate && next ? (
    unsentAnswers ? (
      <button type="button" className="button button-primary button-big" onClick={() => void ask(answers, estimate.conversationId)}>
        {t("updateWithAnswers")}
      </button>
    ) : (
      <button type="button" className="button button-primary button-big" onClick={() => onApply(next)}>
        {t(adding ? "reviseAddUse" : "reviseUse")}
      </button>
    )
  ) : (
    <button type="button" className="button button-primary button-big" disabled={!canAsk} onClick={() => void ask({}, null)}>
      {t(adding ? "estimate" : "sendCorrection")}
    </button>
  );

  return (
    <Sheet title={title} onClose={close} footer={footer}>
      <div className="sheet-form">
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}

        {running ? (
          <div className="revise-running" role="status">
            <div className="spinner" aria-hidden="true" />
            <p className="estimating-title">{t("estimating")}</p>
            <p className="muted small">{t("estimatingHint")}</p>
          </div>
        ) : estimate && next ? (
          <>
            <section className="revise-found" aria-labelledby="revise-found-title">
              <h3 id="revise-found-title" className="field-label">
                {t("reviseFound")}
              </h3>
              <ul className="items">
                {found?.map((item) => <ItemLine key={item.id} item={item} />)}
              </ul>
              {estimate.notes.map((note) => (
                <p key={note} className="notice">
                  <Icon name="info" size={18} /> <span>{bindUnits(note)}</span>
                </p>
              ))}
              {/* What taking it does to the meal she doses from. */}
              <p className="revise-total">
                {t("reviseMealTotal", { before: shownTotal(items).best, after: shownTotal(next).best })}
              </p>
            </section>

            {estimate.questions.length > 0 && (
              <section className="questions revise-questions" aria-labelledby="revise-questions-title">
                <div>
                  <h3 id="revise-questions-title" className="field-label">
                    {t("questionsTitle")}
                  </h3>
                  <p className="muted small">{t("questionsHint")}</p>
                </div>
                {estimate.questions.map((question) => (
                  <fieldset key={question.text} className="question">
                    <legend>{bindUnits(question.text)}</legend>
                    <div className="chips">
                      {question.options.map((option) => (
                        <button
                          key={option}
                          type="button"
                          className={answers[question.text] === option ? "chip chip-on" : "chip"}
                          aria-pressed={answers[question.text] === option}
                          onClick={() => {
                            setAnswers((current) => ({ ...current, [question.text]: option }));
                            setUnsentAnswers(true);
                          }}
                        >
                          {bindUnits(option)}
                        </button>
                      ))}
                    </div>
                  </fieldset>
                ))}
              </section>
            )}

            <button
              type="button"
              className="button button-quiet button-small"
              onClick={() => {
                setEstimate(null);
                setAnswers({});
                setUnsentAnswers(false);
              }}
            >
              <Icon name="pencil" size={18} />
              {t("reviseRetell")}
            </button>
          </>
        ) : (
          <>
            {adding && (
              <>
                {photos.length > 0 && (
                  <ul className="photos">
                    {photos.map((photo) => (
                      <li key={photo.url}>
                        <img src={photo.url} alt="" />
                        <button type="button" className="photo-remove" aria-label={t("removePhoto")} onClick={() => removePhoto(photo)}>
                          <Icon name="close" size={16} />
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
                {photos.length < MAX_PHOTOS && (
                  <div className="capture-row">
                    <CameraButton
                      mode="photo"
                      icon="camera"
                      className="button button-secondary tile-button"
                      onPhoto={(photo) => void addPhotos([photo])}
                    >
                      {t(photos.length === 0 ? "takePhoto" : "takeAnother")}
                    </CameraButton>
                    <label className="button button-secondary tile-button">
                      <Icon name="image" />
                      {t("fromGallery")}
                      <input type="file" accept="image/*" multiple hidden onChange={pickPhotos} />
                    </label>
                  </div>
                )}
                <CameraFallbackNote modes={["photo"]} />
              </>
            )}
            <label className="field">
              <span className="field-label">{t(adding ? "reviseAddLabel" : "reviseWordsLabel")}</span>
              <textarea
                rows={3}
                autoFocus={!adding}
                value={words}
                placeholder={t(adding ? "reviseAddPlaceholder" : target ? "reviseFoodPlaceholder" : "correctionPlaceholder")}
                onChange={(event) => setWords(event.target.value)}
              />
            </label>
            {!adding && meal.hasPhoto && <p className="muted small">{t("reviseStoredPhoto")}</p>}
          </>
        )}
      </div>
    </Sheet>
  );
}
