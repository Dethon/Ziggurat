import { useRef, useState } from "react";
import { api, ApiError } from "../api";
import { readBarcode } from "../barcode";
import { errorMessage, labelFailureMessage, type Asking } from "../errors";
import { forUpload } from "../images";
import { useSettings } from "../settings";
import type { ItemSource, PackagedFood } from "../types";
import { BarcodeScanner, canScanLive, type ScanProblem } from "./BarcodeScanner";
import { CameraButton, CameraFallbackNote } from "./Camera";
import { Icon } from "./Icon";

// A pack as it was found: its numbers, and whether they came from its barcode or its label.
export interface Found {
  food: PackagedFood;
  source: Extract<ItemSource, "barcode" | "label">;
  barcode?: string;
}

// A pack found by its barcode, scanned live, photographed or typed, or by a photo of its nutrition
// table. Without `labels` a photo is read for its barcode only, and says so when it has none.
export function ProductFinder({ labels, onFound }: { labels: boolean; onFound: (found: Found) => void }) {
  const { t, language } = useSettings();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [barcode, setBarcode] = useState("");
  // Which request is under way, so a failure is said for what it was: a lookup or a label.
  const asking = useRef<Asking>("product");
  // Scanning with the live camera needs a secure page; without one the photo is the way in.
  const [live] = useState(canScanLive);
  // Where the live camera works, it opens scanning: the barcode is the usual way in.
  const [scanning, setScanning] = useState(live);

  async function lookUp(code: string): Promise<boolean> {
    try {
      asking.current = "product";
      onFound({ food: await api.product(code), source: "barcode", barcode: code });
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
      setError(asking.current === "label" ? labelFailureMessage(failure, t) : errorMessage(failure, t, asking.current));
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
      asking.current = "label";
      onFound({ food: await api.readLabel(await forUpload(file), language), source: "label" });
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

  function fromScan(code: string) {
    setScanning(false);
    void run(() => find(code));
  }

  function scanFailed(problem: ScanProblem) {
    setScanning(false);
    setError(t(problem === "denied" ? "scanDenied" : "scanUnavailable"));
  }

  return (
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
          void run(() => find(barcode.trim()));
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
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
    </div>
  );
}
