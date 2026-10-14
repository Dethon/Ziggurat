import { useEffect, useRef, useState, type ChangeEvent } from "react";
import { api } from "../api";
import { toMealItem, total } from "../carbs";
import { Icon } from "../components/Icon";
import { ItemRow } from "../components/ItemRow";
import { TotalCard } from "../components/TotalCard";
import { clarifications, counted, reduce, useDraft, type DraftAction } from "../draft";
import { errorMessage } from "../errors";
import { forUpload, thumbnail } from "../images";
import { useSettings } from "../settings";
import { FoodsSheet } from "./FoodsSheet";
import { ManualSheet } from "./ManualSheet";
import { PackagedSheet } from "./PackagedSheet";

const MAX_PHOTOS = 4;

interface Photo {
  blob: Blob;
  url: string;
}

export function MealScreen({ onSaved }: { onSaved: () => void }) {
  const { t, language } = useSettings();
  const [draft, dispatch] = useDraft();
  const [photos, setPhotos] = useState<Photo[]>([]);
  const [running, setRunning] = useState<AbortController | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sheet, setSheet] = useState<"foods" | "packaged" | "manual" | null>(null);
  const [saving, setSaving] = useState(false);
  const [correction, setCorrection] = useState("");
  const [unsentAnswers, setUnsentAnswers] = useState(false);
  const livePhotos = useRef(photos);
  livePhotos.current = photos;

  useEffect(() => () => livePhotos.current.forEach((photo) => URL.revokeObjectURL(photo.url)), []);

  // After a reload the draft survives but the photos do not, and asking the model again
  // without them would quietly produce a worse estimate than the one on screen.
  const photosGone = draft.estimated && draft.thumbnail !== null && photos.length === 0;
  const canEstimate = photos.length > 0 || draft.description.trim() !== "";
  const sum = total(draft.items);

  async function addPhotos(event: ChangeEvent<HTMLInputElement>) {
    const files = [...(event.target.files ?? [])].slice(0, MAX_PHOTOS - photos.length);
    event.target.value = "";
    try {
      const blobs = await Promise.all(files.map(forUpload));
      if (photos.length === 0 && blobs.length > 0) {
        dispatch({ type: "thumbnail", thumbnail: await thumbnail(blobs[0]) });
      }
      setPhotos((current) => [...current, ...blobs.map((blob) => ({ blob, url: URL.createObjectURL(blob) }))]);
    } catch {
      setError(t("errorGeneric"));
    }
  }

  function removePhoto(photo: Photo) {
    URL.revokeObjectURL(photo.url);
    const rest = photos.filter((other) => other !== photo);
    setPhotos(rest);
    if (photo === photos[0]) {
      void (rest[0] ? thumbnail(rest[0].blob) : Promise.resolve(null)).then((next) =>
        dispatch({ type: "thumbnail", thumbnail: next }),
      );
    }
  }

  async function estimate(before?: DraftAction) {
    const asked = before ? reduce(draft, before) : draft;
    if (before) {
      dispatch(before);
    }
    const controller = new AbortController();
    setRunning(controller);
    setError(null);
    try {
      const result = await api.estimate(
        {
          photos: photos.map((photo) => photo.blob),
          description: asked.description,
          clarifications: clarifications(asked),
          counted: counted(asked),
          language,
        },
        controller.signal,
      );
      if (result.items.length === 0) {
        setError([t("noFood"), ...result.notes].join(" "));
      } else {
        dispatch({ type: "estimated", estimate: result });
        setUnsentAnswers(false);
        setCorrection("");
      }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(errorMessage(failure, t));
      }
    } finally {
      setRunning(null);
    }
  }

  async function save() {
    setSaving(true);
    setError(null);
    try {
      await api.createMeal({
        eatenAt: new Date().toISOString(),
        notes: null,
        outcome: "unknown",
        photo: draft.thumbnail,
        firstEstimate: draft.firstEstimate,
        items: draft.items.map(toMealItem),
      });
      startOver();
      onSaved();
    } catch (failure) {
      setError(errorMessage(failure, t));
    } finally {
      setSaving(false);
    }
  }

  function startOver() {
    photos.forEach((photo) => URL.revokeObjectURL(photo.url));
    setPhotos([]);
    setError(null);
    setCorrection("");
    setUnsentAnswers(false);
    dispatch({ type: "reset" });
  }

  if (running) {
    return (
      <div className="screen estimating">
        {draft.thumbnail && <img className="estimating-photo" src={draft.thumbnail} alt="" />}
        <div className="spinner" role="status" />
        <h1>{t("estimating")}</h1>
        <p className="muted">{t("estimatingHint")}</p>
        <button type="button" className="button button-quiet" onClick={() => running.abort()}>
          {t("cancel")}
        </button>
      </div>
    );
  }

  return (
    <div className="screen">
      {!draft.estimated && (
        <section className="capture">
          <h1>{t("captureTitle")}</h1>
          <p className="muted">{t("captureHint")}</p>

          {photos.length > 0 && (
            <ul className="photos">
              {photos.map((photo) => (
                <li key={photo.url}>
                  <img src={photo.url} alt="" />
                  <button
                    type="button"
                    className="photo-remove"
                    aria-label={t("removePhoto")}
                    onClick={() => removePhoto(photo)}
                  >
                    <Icon name="close" size={16} />
                  </button>
                </li>
              ))}
            </ul>
          )}

          {photos.length < MAX_PHOTOS && (
            <div className="capture-buttons">
              <label className={photos.length === 0 ? "button button-primary button-big" : "button button-secondary"}>
                <Icon name="camera" />
                {t("takePhoto")}
                <input type="file" accept="image/*" capture="environment" hidden onChange={addPhotos} />
              </label>
              <label className="button button-secondary">
                <Icon name="image" />
                {t("fromGallery")}
                <input type="file" accept="image/*" multiple hidden onChange={addPhotos} />
              </label>
            </div>
          )}

          <label className="field">
            <span className="field-label">{t("describeLabel")}</span>
            <textarea
              rows={2}
              value={draft.description}
              placeholder={t("describePlaceholder")}
              onChange={(event) => dispatch({ type: "describe", description: event.target.value })}
            />
          </label>

          {canEstimate && (
            <button type="button" className="button button-primary button-big" onClick={() => estimate()}>
              {t("estimate")}
            </button>
          )}
        </section>
      )}

      {error && (
        <p className="notice notice-error" role="alert">
          {error}
        </p>
      )}

      {draft.items.length > 0 && (
        <>
          <TotalCard total={sum} thumbnail={draft.estimated ? draft.thumbnail : null} />

          {draft.estimated && !photosGone && draft.questions.length > 0 && (
            <section className="card questions">
              <h2>{t("questionsTitle")}</h2>
              <p className="muted small">{t("questionsHint")}</p>
              {draft.questions.map((question) => (
                <fieldset key={question.text} className="question">
                  <legend>{question.text}</legend>
                  <div className="chips">
                    {question.options.map((option) => (
                      <button
                        key={option}
                        type="button"
                        className={draft.answers[question.text] === option ? "chip chip-on" : "chip"}
                        aria-pressed={draft.answers[question.text] === option}
                        onClick={() => {
                          dispatch({ type: "answer", question: question.text, answer: option });
                          setUnsentAnswers(true);
                        }}
                      >
                        {option}
                      </button>
                    ))}
                  </div>
                </fieldset>
              ))}
              {unsentAnswers && (
                <button type="button" className="button button-primary" onClick={() => estimate()}>
                  {t("updateEstimate")}
                </button>
              )}
            </section>
          )}

          <section className="card">
            <h2>{t("itemsTitle")}</h2>
            <ul className="items">
              {draft.items.map((item) => (
                <ItemRow
                  key={item.id}
                  item={item}
                  onChange={(next) => dispatch({ type: "replace", item: next })}
                  onRemove={() => dispatch({ type: "remove", id: item.id })}
                />
              ))}
            </ul>
            {draft.notes.map((note) => (
              <p key={note} className="notice">
                <Icon name="info" size={18} /> {note}
              </p>
            ))}
          </section>

          {draft.estimated && !photosGone && (
            <form
              className="correction"
              onSubmit={(event) => {
                event.preventDefault();
                if (correction.trim()) {
                  void estimate({ type: "correct", text: correction.trim() });
                }
              }}
            >
              <textarea
                rows={2}
                value={correction}
                placeholder={t("correctionPlaceholder")}
                aria-label={t("correctionPlaceholder")}
                onChange={(event) => setCorrection(event.target.value)}
              />
              <button type="submit" className="button button-secondary" disabled={!correction.trim()}>
                {t("updateEstimate")}
              </button>
            </form>
          )}
          {photosGone && <p className="muted small">{t("photosGone")}</p>}
        </>
      )}

      <section className="add-more">
        {draft.items.length > 0 && <h2>{t("addMore")}</h2>}
        <div className="add-buttons">
          <button type="button" className="add-button" onClick={() => setSheet("foods")}>
            <Icon name="bookmark" />
            <span>
              <strong>{t("myFoods")}</strong>
              <small>{t("myFoodsHint")}</small>
            </span>
          </button>
          <button type="button" className="add-button" onClick={() => setSheet("packaged")}>
            <Icon name="barcode" />
            <span>
              <strong>{t("packaged")}</strong>
              <small>{t("packagedHint")}</small>
            </span>
          </button>
          <button type="button" className="add-button" onClick={() => setSheet("manual")}>
            <Icon name="pencil" />
            <span>
              <strong>{t("manual")}</strong>
              <small>{t("manualHint")}</small>
            </span>
          </button>
        </div>
      </section>

      {draft.items.length > 0 && (
        <footer className="save-bar">
          <p className="muted small">{t("estimateDisclaimer")}</p>
          <button type="button" className="button button-primary button-big" disabled={saving} onClick={save}>
            {saving ? t("saving") : t("save")}
          </button>
          <button
            type="button"
            className="button button-quiet"
            onClick={() => window.confirm(t("discardConfirm")) && startOver()}
          >
            {t("discard")}
          </button>
        </footer>
      )}

      {sheet === "foods" && (
        <FoodsSheet
          onClose={() => setSheet(null)}
          onAdd={(item) => {
            dispatch({ type: "add", item });
            setSheet(null);
          }}
        />
      )}
      {sheet === "packaged" && (
        <PackagedSheet
          onClose={() => setSheet(null)}
          onAdd={(item) => {
            dispatch({ type: "add", item });
            setSheet(null);
          }}
        />
      )}
      {sheet === "manual" && (
        <ManualSheet
          onClose={() => setSheet(null)}
          onAdd={(item) => {
            dispatch({ type: "add", item });
            setSheet(null);
          }}
        />
      )}
    </div>
  );
}
