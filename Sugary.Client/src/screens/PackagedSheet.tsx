import { useState, type ChangeEvent } from "react";
import { api, ApiError } from "../api";
import { readBarcode } from "../barcode";
import { formatGrams, fromPackaged, parseAmount } from "../carbs";
import { BarcodeScanner, canScanLive, type ScanProblem } from "../components/BarcodeScanner";
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
  // Scanning with the live camera needs a secure page; without one the photo is the way in.
  const [live] = useState(canScanLive);
  const [scanning, setScanning] = useState(false);
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

  function fromPhoto(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) {
      return;
    }
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

  return (
    <Sheet title={t("packagedTitle")} onClose={onClose}>
      {found === null ? (
        <div className="sheet-form">
          <p className="muted">
            {t(labels ? (live ? "scanIntro" : "packagedIntro") : live ? "scanIntroBarcode" : "packagedIntroBarcode")}
          </p>
          {scanning ? (
            <BarcodeScanner onDetected={fromScan} onProblem={scanFailed} onStop={() => setScanning(false)} />
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
          <label
            className={[live ? "button button-secondary" : "button button-primary button-big", busy && "disabled"]
              .filter(Boolean)
              .join(" ")}
          >
            <Icon name="camera" />
            {busy && !live ? t("reading") : t(labels ? "packagedPhoto" : "barcodePhoto")}
            <input type="file" accept="image/*" capture="environment" hidden disabled={busy} onChange={fromPhoto} />
          </label>
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
              {error}
            </p>
          )}
        </div>
      ) : (
        <form
          className="sheet-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (eaten === null || eaten <= 0) {
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
          <p className="per100">
            {t("per100", { n: found.food.carbsPer100g.toLocaleString(language), unit: found.food.unit })}
          </p>
          {found.food.note && (
            <p className="notice">
              <Icon name="info" size={18} /> {found.food.note}
            </p>
          )}
          <label className="field">
            <span className="field-label">{t("amountEaten")}</span>
            <span className="amount-control wide">
              <input inputMode="decimal" value={amount} autoFocus onChange={(event) => setAmount(event.target.value)} />
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
              100 {found.food.unit}
            </button>
          </div>
          <p className="packaged-result">
            {eaten !== null
              ? t("carbsFromAmount", { carbs: formatGrams((eaten * found.food.carbsPer100g) / 100) })
              : " "}
          </p>
          {found.food.foodId ? (
            <p className="muted small">
              <Icon name="bookmark" size={16} /> {t("fromMyFoods")}
            </p>
          ) : (
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
              {error}
            </p>
          )}
          <button
            type="submit"
            className="button button-primary button-big"
            disabled={busy || eaten === null || eaten <= 0}
          >
            {t("addToMeal")}
          </button>
        </form>
      )}
    </Sheet>
  );
}
