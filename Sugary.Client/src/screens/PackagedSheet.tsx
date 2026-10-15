import { useId, useRef, useState } from "react";
import { api, ApiError } from "../api";
import { readBarcode } from "../barcode";
import { formatGrams, fromPackaged, parseAmount } from "../carbs";
import { BarcodeScanner, canScanLive, type ScanProblem } from "../components/BarcodeScanner";
import { CameraButton, CameraFallbackNote } from "../components/Camera";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { labelFailureMessage } from "../errors";
import { keep } from "../foods";
import { forUpload } from "../images";
import { useSettings } from "../settings";
import type { DraftItem, ItemSource, PackagedFood } from "../types";

interface Found {
  food: PackagedFood;
  source: ItemSource;
  barcode?: string;
}

// Without `labels` a photo is read for its barcode only, and says so when it has none.
export function PackagedSheet({
  labels,
  onClose,
  onAdd,
}: {
  labels: boolean;
  onClose: () => void;
  onAdd: (item: DraftItem) => void;
}) {
  const { t, language } = useSettings();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [barcode, setBarcode] = useState("");
  const digits = useRef<HTMLInputElement>(null);
  // Scanning with the live camera needs a secure page; without one the photo is the way in.
  const [live] = useState(canScanLive);
  // Where the live camera works, the sheet opens scanning: the barcode is the usual way in.
  const [scanning, setScanning] = useState(live);
  const [found, setFound] = useState<Found | null>(null);
  const [name, setName] = useState("");
  const [amount, setAmount] = useState("");
  const [saveFood, setSaveFood] = useState(true);

  function show(next: Found) {
    setFound(next);
    setName(next.food.name ?? "");
    setAmount(next.food.servingGrams ? formatGrams(next.food.servingGrams) : "");
  }

  async function lookUp(code: string): Promise<boolean> {
    try {
      show({ food: await api.product(code), source: "barcode", barcode: code });
      return true;
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 404) {
        return false;
      }
      throw failure;
    }
  }

  async function run(work: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (failure) {
      setError(labelFailureMessage(failure, t));
    } finally {
      setBusy(false);
    }
  }

  function fromPhoto(file: Blob) {
    setScanning(false);
    void run(async () => {
      // A barcode is looked for in the photo as taken, since thin bars need the pixels; only a
      // label is shrunk, for the upload.
      const code = await readBarcode(file);
      if (code) {
        await find(code);
        return;
      }
      if (!labels) {
        setError(t("labelsUnavailable"));
        return;
      }
      show({ food: await api.readLabel(await forUpload(file), language), source: "label" });
    });
  }

  // The digits go into the box whatever happens next, so a code that was read but is not in
  // the database can be checked against the pack.
  async function find(code: string) {
    setBarcode(code);
    if (!(await lookUp(code))) {
      setError(t("productNotFound"));
    }
  }

  function fromCode() {
    void run(() => find(barcode.trim()));
  }

  function fromScan(code: string) {
    setScanning(false);
    void run(() => find(code));
  }

  function scanFailed(problem: ScanProblem) {
    setScanning(false);
    setError(t(problem === "denied" ? "scanDenied" : "scanUnavailable"));
  }

  const eaten = parseAmount(amount);
  const formId = useId();
  const valid = eaten !== null && eaten > 0;

  return (
    <Sheet
      title={t("packagedTitle")}
      onClose={onClose}
      onBack={found ? () => setFound(null) : undefined}
      footer={
        found && (
          <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !valid}>
            {t("addToMeal")}
          </button>
        )
      }
    >
      {found === null ? (
        <div className="sheet-form">
          <p className="muted">
            {t(labels ? (live ? "scanIntro" : "packagedIntro") : live ? "scanIntroBarcode" : "packagedIntroBarcode")}
          </p>
          {scanning ? (
            <BarcodeScanner
              onDetected={fromScan}
              onProblem={scanFailed}
              onStop={() => setScanning(false)}
              onType={() => {
                setScanning(false);
                requestAnimationFrame(() => digits.current?.focus());
              }}
            />
          ) : (
            live && (
              <button
                type="button"
                className="button button-primary button-big"
                disabled={busy}
                onClick={() => {
                  setError(null);
                  setScanning(true);
                }}
              >
                <Icon name="barcode" />
                {busy ? t("reading") : t("scanBarcode")}
              </button>
            )
          )}
          <CameraButton
            mode="photo"
            icon="camera"
            className={live ? "button button-secondary" : "button button-primary button-big"}
            disabled={busy}
            onPhoto={fromPhoto}
          >
            {busy && !live ? t("reading") : t(labels ? "packagedPhoto" : "barcodePhoto")}
          </CameraButton>
          <CameraFallbackNote modes={["photo"]} />
          <form
            className="inline-form"
            onSubmit={(event) => {
              event.preventDefault();
              fromCode();
            }}
          >
            <label className="field">
              <span className="field-label">{t("barcodeManual")}</span>
              <input
                ref={digits}
                inputMode="numeric"
                value={barcode}
                onChange={(event) => setBarcode(event.target.value.replace(/\D/g, ""))}
              />
            </label>
            <button type="submit" className="button button-secondary" disabled={busy || barcode.length < 8}>
              {t("lookUp")}
            </button>
          </form>
          {error && (
            <p className="notice notice-error" role="alert">
              <Icon name="alert" size={18} /> <span>{error}</span>
            </p>
          )}
        </div>
      ) : (
        <form
          id={formId}
          className="sheet-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (!valid || eaten === null) {
              return;
            }
            const named = name.trim() || t("unnamedProduct");
            void run(async () => {
              // Kept with this amount as her usual one, so next time it is one tap.
              const foodId =
                found.food.foodId ??
                (saveFood
                  ? (
                      await keep({
                        name: named,
                        carbsPer100g: found.food.carbsPer100g,
                        unit: found.food.unit,
                        portionGrams: eaten,
                        portionCarbs: null,
                        barcode: found.barcode ?? null,
                        source: found.source === "barcode" ? "barcode" : "label",
                      })
                    ).id
                  : null);
              onAdd(fromPackaged(found.food, eaten, named, found.source, foodId));
            });
          }}
        >
          <label className="field">
            <span className="field-label">{t("name")}</span>
            <input value={name} onChange={(event) => setName(event.target.value)} />
          </label>
          <div className="provenance">
            <p className="per100">
              {t("per100", { n: found.food.carbsPer100g.toLocaleString(language), unit: found.food.unit })}
            </p>
            {/* Where the number came from, before she adds it: a database, a model's reading, or her own list. */}
            <p className="muted small provenance-line">
              <Icon name={found.food.foodId ? "jar" : found.source === "barcode" ? "barcode" : "label"} size={18} />
              <span>
                {found.food.foodId
                  ? t("fromMyFoods")
                  : found.source === "barcode"
                    ? t("sourceProduct", { code: found.barcode ?? "" })
                    : t("sourceLabel")}
              </span>
            </p>
          </div>
          {found.food.note && (
            <p className="notice">
              <Icon name="info" size={18} /> <span>{found.food.note}</span>
            </p>
          )}
          <label className="field">
            <span className="field-label">{t("amountEaten")}</span>
            <span className="amount-input">
              <input inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} />
              <span className="amount-unit">{found.food.unit}</span>
            </span>
          </label>
          <div className="chips">
            {found.food.servingGrams && (
              <>
                <button type="button" className="chip" onClick={() => setAmount(formatGrams(found.food.servingGrams!))}>
                  {t("serving", { n: formatGrams(found.food.servingGrams), unit: found.food.unit })}
                </button>
                <button
                  type="button"
                  className="chip"
                  onClick={() => setAmount(formatGrams(found.food.servingGrams! / 2))}
                >
                  {t("halfServing")}
                </button>
              </>
            )}
            <button type="button" className="chip" onClick={() => setAmount("100")}>
              100&nbsp;{found.food.unit}
            </button>
          </div>
          <p className="packaged-result">
            {eaten !== null
              ? t("carbsFromAmount", { carbs: formatGrams((eaten * found.food.carbsPer100g) / 100) })
              : " "}
          </p>
          {!found.food.foodId && (
            <label className="check">
              <input type="checkbox" checked={saveFood} onChange={(event) => setSaveFood(event.target.checked)} />
              <span>
                {t("saveToFoods")}
                <small>{t("saveToFoodsHint")}</small>
              </span>
            </label>
          )}
          {error && (
            <p className="notice notice-error" role="alert">
              <Icon name="alert" size={18} /> <span>{error}</span>
            </p>
          )}
        </form>
      )}
    </Sheet>
  );
}
