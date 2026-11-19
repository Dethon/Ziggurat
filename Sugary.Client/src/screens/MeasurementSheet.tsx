import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { BOUNDS, dayTaken, measurementProblem, parseValue } from "../profile";
import { useSettings } from "../settings";
import { openedMeasurement, overRecord, useShown } from "../stale";
import type { Measurement, MeasurementKind, SaveMeasurement } from "../types";

// A height or a weight: a new one, or one opened to correct or delete. The kind stays: a weight is
// never corrected into a height.
export function MeasurementSheet({
  measurement,
  kind,
  initial,
  others,
  today,
  onClose,
  onSaved,
  onDelete,
}: {
  measurement?: Measurement;
  kind: MeasurementKind;
  initial: { on: string; value: string };
  others: readonly Measurement[];
  today: string;
  onClose: () => void;
  onSaved: () => void;
  onDelete?: (measurement: Measurement) => void;
}) {
  const { t } = useSettings();
  const [on, setOn] = useState(initial.on);
  const [value, setValue] = useState(initial.value);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const problem =
    measurementProblem(kind, value, on, today) ?? (dayTaken(others, kind, on, measurement?.id) ? "measurementDayTaken" : null);
  const values: SaveMeasurement = { kind, on, value: parseValue(value) ?? 0 };
  const edited = useEdited(values);
  const shown = useShown(values);
  const ready = problem === null && (!measurement || edited);

  async function save() {
    if (!ready) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await (measurement
        ? api.updateMeasurement(
            measurement.id,
            overRecord(openedMeasurement(measurement), shown, values),
            openedMeasurement(measurement),
          )
        : api.createMeasurement(values));
      onSaved();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  return (
    <Sheet
      title={t(measurement ? `measurementEdit_${kind}` : `measurementAdd_${kind}`)}
      onClose={onClose}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !ready}>
          {t("saveMeasurement")}
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
        <label className="field">
          <span className="field-label">{t(`measurementValue_${kind}`)}</span>
          <input
            type="text"
            required
            inputMode="decimal"
            autoComplete="off"
            value={value}
            onChange={(event) => setValue(event.target.value)}
          />
        </label>
        <label className="field">
          <span className="field-label">{t("measurementOn")}</span>
          <input type="date" required value={on} max={today} onChange={(event) => setOn(event.target.value)} />
        </label>
        {problem && (value !== "" || measurement) && (
          <p className="field-problem" role="alert">
            {t(problem, BOUNDS[kind])}
          </p>
        )}
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {measurement && onDelete && (
          <button type="button" className="button button-quiet danger" disabled={busy} onClick={() => onDelete(measurement)}>
            <Icon name="trash" size={20} />
            {t("deleteMeasurement")}
          </button>
        )}
      </form>
    </Sheet>
  );
}
