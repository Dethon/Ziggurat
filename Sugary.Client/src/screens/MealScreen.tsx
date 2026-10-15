import { useEffect, useRef, useState, type ChangeEvent, type RefObject } from "react";
import { api } from "../api";
import { ActionBar } from "../components/ActionBar";
import { shownTotal, toMealItem } from "../carbs";
import { CameraButton, CameraFallbackNote } from "../components/Camera";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { ItemRow } from "../components/ItemRow";
import { PortionSheet } from "../components/PortionSheet";
import { TotalCard } from "../components/TotalCard";
import { reduce, useDraft, type DraftAction } from "../draft";
import { errorMessage } from "../errors";
import { estimateDraft } from "../estimate";
import type { StringKey } from "../i18n";
import { forUpload, thumbnail } from "../images";
import { bindUnits, footerAction, grouped, usuals, type FooterAction } from "../meal";
import { useSettings } from "../settings";
import type { DraftItem, Food, Question } from "../types";
import { AmountSheet, FoodsSheet } from "./FoodsSheet";
import { ManualSheet } from "./ManualSheet";
import { PackagedSheet } from "./PackagedSheet";

const MAX_PHOTOS = 4;

interface Photo {
  blob: Blob;
  url: string;
  // A clip's length, when known.
  seconds?: number;
}

// What the footer says for each action, and the quiet way out it offers beside it.
const ACTIONS: Record<FooterAction, { primary: StringKey; secondary?: StringKey; pending?: StringKey }> = {
  estimate: { primary: "estimate", secondary: "saveWithoutEstimate" },
  retry: { primary: "tryAgain", secondary: "saveAsIs" },
  update: { primary: "updateWithAnswers", secondary: "saveWithoutAnswers", pending: "answersPending" },
  sendCorrection: { primary: "sendCorrection", secondary: "saveWithoutCorrection", pending: "correctionPending" },
  save: { primary: "save" },
};

// `estimates` and `labels` say whether the server answers those two actions yet; when it does not,
// the photo and description are replaced by a notice and only the other ways in remain.
export function MealScreen({
  active,
  adder,
  estimates,
  labels,
  onSaved,
}: {
  // Whether the Meal tab is the one on screen; the screen stays mounted behind the others.
  active: boolean;
  // Filled in by the screen: how the Foods tab adds one of her foods to this meal.
  adder: RefObject<((item: DraftItem) => void) | null>;
  estimates: boolean;
  labels: boolean;
  onSaved: () => void;
}) {
  const { t, language } = useSettings();
  const [draft, dispatch] = useDraft();
  const [confirm, confirming] = useConfirm();
  const [photos, setPhotos] = useState<Photo[]>([]);
  // At most one clip, shown as she filmed it; the server makes it small and takes its stills.
  const [clip, setClip] = useState<Photo | null>(null);
  const [running, setRunning] = useState<AbortController | null>(null);
  const [error, setError] = useState<string | null>(null);
  // The last change to an estimate on screen failed: the numbers shown are from before it.
  const [stale, setStale] = useState(false);
  const [sheet, setSheet] = useState<"foods" | "packaged" | "manual" | null>(null);
  const [usual, setUsual] = useState<Food | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [correcting, setCorrecting] = useState(false);
  const [correction, setCorrection] = useState("");
  const [unsentAnswers, setUnsentAnswers] = useState(false);
  const [foods, setFoods] = useState<Food[]>([]);
  // Every question the model has asked on this meal, kept here so an answer already sent can be
  // changed: the draft keeps only the answers, and the questions still unanswered.
  const [asked, setAsked] = useState<Record<string, Question>>({});
  const [changing, setChanging] = useState<string | null>(null);
  const livePhotos = useRef(photos);
  livePhotos.current = photos;
  const liveClip = useRef(clip);
  liveClip.current = clip;
  const questionsCard = useRef<HTMLElement>(null);

  useEffect(
    () => () =>
      [...livePhotos.current, ...(liveClip.current ? [liveClip.current] : [])].forEach((photo) =>
        URL.revokeObjectURL(photo.url),
      ),
    [],
  );

  useEffect(() => {
    adder.current = (item) => dispatch({ type: "add", item });
    return () => {
      adder.current = null;
    };
  }, [adder, dispatch]);

  useEffect(() => {
    if (draft.questions.length > 0) {
      setAsked((known) => ({ ...known, ...Object.fromEntries(draft.questions.map((q) => [q.text, q])) }));
    }
  }, [draft.questions]);

  // Her usuals follow what she uses, so they are read again whenever she comes back to the meal.
  useEffect(() => {
    if (active) {
      api.foods().then(setFoods, () => setFoods([]));
    }
  }, [active]);

  // After a reload the draft survives but the photos do not, and asking the model again
  // without them would quietly produce a worse estimate than the one on screen.
  const photosGone = draft.estimated && draft.thumbnail !== null && photos.length === 0;
  const canEstimate = photos.length > 0 || clip !== null || draft.description.trim() !== "";
  const sum = shownTotal(draft.items);
  const { estimated: estimatedItems, counted: countedItems } = grouped(draft.items);
  const questions = draft.estimated && !photosGone ? draft.questions : [];
  const action = running
    ? null
    : footerAction({
        items: draft.items.length,
        canEstimate: estimates && canEstimate,
        estimated: draft.estimated,
        stale: stale && !photosGone,
        unsentAnswers: unsentAnswers && !photosGone,
        unsentCorrection: draft.estimated && !photosGone && correction.trim() !== "",
      });
  const editingItem = draft.items.find((item) => item.id === editing) ?? null;
  // Answers already in a request: their questions are no longer among the open ones.
  const sentAnswers = draft.estimated
    ? Object.entries(draft.answers).filter(([text]) => !draft.questions.some((q) => q.text === text))
    : [];

  function pickPhotos(event: ChangeEvent<HTMLInputElement>) {
    const files = [...(event.target.files ?? [])];
    event.target.value = "";
    void addPhotos(files);
  }

  // Photos from the gallery and from the camera go the same way: shrunk for the upload, the first
  // one also kept small as the meal's picture in the log.
  async function addPhotos(picked: Blob[]) {
    const files = picked.slice(0, MAX_PHOTOS - photos.length);
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

  function addClip(file: File, seconds?: number) {
    if (clip) {
      URL.revokeObjectURL(clip.url);
    }
    setClip({ blob: file, url: URL.createObjectURL(file), seconds });
  }

  function removeClip() {
    if (clip) {
      URL.revokeObjectURL(clip.url);
    }
    setClip(null);
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
    if (before?.type === "correct") {
      // It is in the draft now and goes with every request until one succeeds.
      setCorrection("");
      setCorrecting(false);
    }
    const controller = new AbortController();
    setRunning(controller);
    setError(null);
    try {
      const result = await estimateDraft(
        asked,
        { photos: photos.map((photo) => photo.blob), clip: clip?.blob ?? null },
        language,
        (input) => api.estimate(input, controller.signal),
      );
      if (result.items.length === 0) {
        setError([t("noFood"), ...result.notes].join(" "));
      } else {
        dispatch({ type: "estimated", estimate: result });
        setUnsentAnswers(false);
        setStale(false);
      }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(errorMessage(failure, t, "estimate"));
        setStale(asked.estimated && asked.items.length > 0);
      }
    } finally {
      setRunning(null);
      window.scrollTo(0, 0);
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
      setError(errorMessage(failure, t, "save"));
    } finally {
      setSaving(false);
    }
  }

  function startOver() {
    photos.forEach((photo) => URL.revokeObjectURL(photo.url));
    setPhotos([]);
    removeClip();
    setError(null);
    setStale(false);
    setCorrection("");
    setCorrecting(false);
    setUnsentAnswers(false);
    setEditing(null);
    setAsked({});
    setChanging(null);
    dispatch({ type: "reset" });
  }

  async function askStartOver() {
    const yes = await confirm({
      title: t("discardTitle"),
      body: t("discardBody"),
      confirm: t("discardYes"),
      cancel: t("discardNo"),
    });
    if (yes) {
      startOver();
      window.scrollTo(0, 0);
    }
  }

  function runPrimary(next: FooterAction) {
    if (next === "save") {
      void save();
    } else if (next === "sendCorrection") {
      void estimate({ type: "correct", text: correction.trim() });
    } else {
      void estimate();
    }
  }

  function add(item: DraftItem) {
    dispatch({ type: "add", item });
    setSheet(null);
    setUsual(null);
  }

  if (running) {
    return (
      <div className="screen estimating">
        {draft.thumbnail && <img className="estimating-photo" src={draft.thumbnail} alt="" />}
        <div className="spinner" aria-hidden="true" />
        <h1 className="estimating-title" role="status">
          {t("estimating")}
        </h1>
        <p className="muted">{t("estimatingHint")}</p>
        <button type="button" className="button button-secondary" onClick={() => running.abort()}>
          {t("cancel")}
        </button>
      </div>
    );
  }

  const hasMeal = draft.items.length > 0;
  const started = hasMeal || photos.length > 0 || clip !== null;
  const shown = ACTIONS[action ?? "save"];

  return (
    <div className="screen meal">
      <header className="screen-header">
        <div>
          {!started && (
            <p className="wordmark">
              <Icon name="slice" size={22} />
              Sugary
            </p>
          )}
          <h1 className={hasMeal ? "title-small" : undefined}>{t(hasMeal ? "mealDraftTitle" : "captureTitle")}</h1>
        </div>
        {started && (
          <button type="button" className="button button-quiet button-small" onClick={askStartOver}>
            <Icon name="restart" size={20} />
            {t("discard")}
          </button>
        )}
      </header>

      {error && (
        <div className="notice notice-error" role="alert">
          <Icon name="alert" size={18} />
          <span className="notice-body">
            <span>{error}</span>
            {stale && <strong>{t("staleAfterError")}</strong>}
          </span>
        </div>
      )}

      {hasMeal && (
        <>
          <TotalCard
            total={sum}
            stale={stale}
            thumbnail={draft.estimated ? draft.thumbnail : null}
            questions={questions.length}
            onJumpToQuestions={() => questionsCard.current?.scrollIntoView({ behavior: "smooth", block: "start" })}
          />

          {estimatedItems.length > 0 && (
            <section className="card item-group" aria-labelledby="estimated-title">
              <h2 id="estimated-title">{t("itemsTitle")}</h2>
              <ul className="items">
                {estimatedItems.map((item) => (
                  <ItemRow key={item.id} item={item} onOpen={() => setEditing(item.id)} />
                ))}
              </ul>
              {draft.notes.map((note) => (
                <p key={note} className="notice">
                  <Icon name="info" size={18} /> <span>{bindUnits(note)}</span>
                </p>
              ))}
            </section>
          )}

          {countedItems.length > 0 && (
            <section className="card item-group counted" aria-labelledby="counted-title">
              <div>
                <h2 id="counted-title">{t("countedTitle")}</h2>
                {estimatedItems.length > 0 && <p className="muted small">{t("countedHint")}</p>}
              </div>
              <ul className="items">
                {countedItems.map((item) => (
                  <ItemRow key={item.id} item={item} onOpen={() => setEditing(item.id)} />
                ))}
              </ul>
            </section>
          )}

          {questions.length > 0 && (
            <section ref={questionsCard} className="card questions" aria-labelledby="questions-title">
              <div>
                <h2 id="questions-title">{t("questionsTitle")}</h2>
                <p className="muted small">{t("questionsHint")}</p>
              </div>
              {questions.map((question) => (
                <fieldset key={question.text} className="question">
                  <legend>{bindUnits(question.text)}</legend>
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
                        {bindUnits(option)}
                      </button>
                    ))}
                  </div>
                </fieldset>
              ))}
            </section>
          )}

          {sentAnswers.length > 0 && (
            <section className="card answers" aria-labelledby="answers-title">
              <h2 id="answers-title">{t("answersTitle")}</h2>
              <ul className="answer-list">
                {sentAnswers.map(([text, answer]) => {
                  const question = asked[text];
                  const canChange = question !== undefined && !photosGone;
                  return (
                    <li key={text} className="answer">
                      <div className="answer-row">
                        <span className="item-text">
                          <span className="item-meta">{bindUnits(text)}</span>
                          <span className="answer-given">{bindUnits(answer)}</span>
                        </span>
                        {canChange && changing !== text && (
                          <button
                            type="button"
                            className="button button-quiet button-small"
                            aria-label={t("changeAnswerSpoken", { question: text })}
                            onClick={() => setChanging(text)}
                          >
                            {t("changeAnswer")}
                          </button>
                        )}
                      </div>
                      {canChange && changing === text && (
                        <div className="chips" role="group" aria-label={text}>
                          {question.options.map((option) => (
                            <button
                              key={option}
                              type="button"
                              className={answer === option ? "chip chip-on" : "chip"}
                              aria-pressed={answer === option}
                              onClick={() => {
                                if (option !== answer) {
                                  dispatch({ type: "answer", question: text, answer: option });
                                  setUnsentAnswers(true);
                                }
                                setChanging(null);
                              }}
                            >
                              {bindUnits(option)}
                            </button>
                          ))}
                        </div>
                      )}
                    </li>
                  );
                })}
              </ul>
            </section>
          )}

          {draft.estimated && !photosGone && (
            <section className="correction">
              {correcting || correction ? (
                <label className="field">
                  <span className="field-label">{t("correctionToggle")}</span>
                  <textarea
                    rows={2}
                    autoFocus={correcting && !correction}
                    value={correction}
                    placeholder={t("correctionPlaceholder")}
                    onChange={(event) => setCorrection(event.target.value)}
                  />
                </label>
              ) : (
                <button type="button" className="row-button" onClick={() => setCorrecting(true)}>
                  <Icon name="pencil" size={20} />
                  <span>{t("correctionToggle")}</span>
                  <Icon name="chevron" size={20} />
                </button>
              )}
            </section>
          )}
          {photosGone && <p className="muted small">{t("photosGone")}</p>}
        </>
      )}

      {!draft.estimated && !estimates && (
        <p className="notice">
          <Icon name="info" size={18} /> <span>{t("estimatesUnavailable")}</span>
        </p>
      )}

      {!draft.estimated && estimates && (
        <section className="capture" aria-label={t("captureTitle")}>
          {!started && <p className="muted">{t("captureHint")}</p>}

          {(photos.length > 0 || clip) && (
            <ul className="photos">
              {clip && (
                <li key={clip.url}>
                  <video
                    src={clip.url}
                    muted
                    playsInline
                    preload="metadata"
                    aria-label={clip.seconds ? t("clipSpoken", { s: clip.seconds }) : t("clip")}
                    onLoadedMetadata={(event) => {
                      // From the phone's camera app the length is read from the file, where it is known.
                      const duration = event.currentTarget.duration;
                      if (!clip.seconds && Number.isFinite(duration) && duration > 0) {
                        setClip((current) => current && { ...current, seconds: Math.round(duration) });
                      }
                    }}
                  />
                  <span className="clip-badge" aria-hidden="true">
                    <Icon name="play" size={16} />
                    {clip.seconds ? `${clip.seconds}\u00a0s` : t("clip")}
                  </span>
                  <button type="button" className="photo-remove" aria-label={t("removeClip")} onClick={removeClip}>
                    <Icon name="close" size={16} />
                  </button>
                </li>
              )}
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

          {photos.length === 0 && !clip && (
            <CameraButton
              mode="photo"
              icon="camera"
              className="button button-primary capture-hero"
              onPhoto={(photo) => void addPhotos([photo])}
            >
              {t("takePhoto")}
            </CameraButton>
          )}

          <div className="capture-row">
            {(photos.length > 0 || clip) && photos.length < MAX_PHOTOS && (
              <CameraButton
                mode="photo"
                icon="camera"
                className="button button-secondary tile-button"
                onPhoto={(photo) => void addPhotos([photo])}
              >
                {t("takeAnother")}
              </CameraButton>
            )}
            {!clip && (
              <CameraButton mode="video" icon="video" className="button button-secondary tile-button" onClip={addClip}>
                <span className="tile-label">
                  {t("filmPlate")}
                  <small>{t("filmPlateSub")}</small>
                </span>
              </CameraButton>
            )}
            {photos.length < MAX_PHOTOS && (
              <label className="button button-secondary tile-button">
                <Icon name="image" />
                {t("fromGallery")}
                <input type="file" accept="image/*" multiple hidden onChange={pickPhotos} />
              </label>
            )}
          </div>
          <CameraFallbackNote modes={["photo", "video"]} />

          <label className="field">
            <span className="field-label">{t("describeLabel")}</span>
            <textarea
              rows={2}
              value={draft.description}
              placeholder={t("describePlaceholder")}
              onChange={(event) => dispatch({ type: "describe", description: event.target.value })}
            />
          </label>
        </section>
      )}

      {!draft.estimated && usuals(foods).length > 0 && (
        <section className="usuals" aria-labelledby="usuals-title">
          <h2 id="usuals-title">{t("usualsTitle")}</h2>
          <div className="chips chips-scroll">
            {usuals(foods).map((food) => (
              <button key={food.id} type="button" className="chip chip-food" onClick={() => setUsual(food)}>
                <Icon name="plus" size={18} />
                {food.name}
              </button>
            ))}
          </div>
        </section>
      )}

      <section className="add-more" aria-labelledby="add-title">
        <h2 id="add-title">{t(hasMeal ? "addMore" : "otherWaysTitle")}</h2>
        <div className="tiles">
          <button type="button" className="tile" onClick={() => setSheet("foods")}>
            <Icon name="jar" size={26} />
            {t("myFoods")}
          </button>
          <button type="button" className="tile" onClick={() => setSheet("packaged")}>
            <Icon name="barcode" size={26} />
            {t(labels ? "packaged" : "packagedHintBarcode")}
          </button>
          <button type="button" className="tile" onClick={() => setSheet("manual")}>
            <Icon name="pencil" size={26} />
            {t("manual")}
          </button>
        </div>
      </section>

      {action && (
        <ActionBar active={active}>
          {shown.pending && (
            <p className="action-pending" role="status">
              <Icon name="alert" size={18} />
              {t(shown.pending)}
            </p>
          )}
          <div className="footer-row">
            {shown.secondary && (action !== "estimate" || hasMeal) && (
              <button type="button" className="button button-quiet" disabled={saving} onClick={() => void save()}>
                {t(shown.secondary)}
              </button>
            )}
            <button
              type="button"
              className="button button-primary button-grow"
              disabled={saving}
              onClick={() => runPrimary(action)}
            >
              {action === "save" && saving ? t("saving") : t(shown.primary)}
            </button>
          </div>
        </ActionBar>
      )}

      {editingItem && (
        <PortionSheet
          item={editingItem}
          onChange={(next) => dispatch({ type: "replace", item: next })}
          onRemove={() => {
            dispatch({ type: "remove", id: editingItem.id });
            setEditing(null);
          }}
          onClose={() => setEditing(null)}
        />
      )}
      {usual && <AmountSheet food={usual} onAdd={add} onClose={() => setUsual(null)} />}
      {confirming}
      {sheet === "foods" && <FoodsSheet onClose={() => setSheet(null)} onAdd={add} />}
      {sheet === "packaged" && <PackagedSheet labels={labels} onClose={() => setSheet(null)} onAdd={add} />}
      {sheet === "manual" && <ManualSheet onClose={() => setSheet(null)} onAdd={add} />}
    </div>
  );
}
