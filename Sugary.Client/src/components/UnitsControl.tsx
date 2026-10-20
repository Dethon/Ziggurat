import { stepUnits } from "../dose";
import { useSettings } from "../settings";
import { Icon } from "./Icon";

// A dose's units, typed or stepped a unit at a time with the thumb. The field keeps what she wrote;
// a tap only ever moves it from there, and is off when it has nowhere to go.
export function UnitsControl({
  id,
  text,
  invalid,
  describedBy,
  onChange,
}: {
  // The input's own id, for the field's label to name it.
  id: string;
  text: string;
  invalid: boolean;
  describedBy?: string;
  onChange: (text: string) => void;
}) {
  const { t, language } = useSettings();
  const down = stepUnits(text, -1, language);
  const up = stepUnits(text, 1, language);
  const unit = t("unitsShort");

  return (
    <span className="amount-control">
      <button
        type="button"
        className="stepper"
        aria-label={`−1 ${unit}`}
        disabled={down === null}
        onClick={() => down !== null && onChange(down)}
      >
        <Icon name="minus" size={22} />
      </button>
      <span className="amount-input">
        <input
          id={id}
          inputMode="decimal"
          autoComplete="off"
          value={text}
          aria-invalid={invalid}
          aria-describedby={describedBy}
          onChange={(event) => onChange(event.target.value)}
        />
        <span className="amount-unit">{unit}</span>
      </span>
      <button
        type="button"
        className="stepper"
        aria-label={`+1 ${unit}`}
        disabled={up === null}
        onClick={() => up !== null && onChange(up)}
      >
        <Icon name="plus" size={22} />
      </button>
    </span>
  );
}
