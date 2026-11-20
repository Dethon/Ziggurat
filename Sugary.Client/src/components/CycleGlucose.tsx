import { phaseShift } from "../cycle";
import { formatGlucose, formatShare, UNIT_LABEL } from "../glucose";
import type { StringKey } from "../i18n";
import { useSettings } from "../settings";
import type { CyclePhases, PhaseFigures } from "../types";
import { Fold } from "./Fold";
import { Icon } from "./Icon";

const PHASES: { key: "menstrual" | "follicular" | "luteal"; label: StringKey }[] = [
  { key: "menstrual", label: "phaseMenstrual" },
  { key: "follicular", label: "phaseFollicular" },
  { key: "luteal", label: "phaseLuteal" },
];

// Her glucose in each part of her finished cycles, from her own readings: the server's CyclePhases. Many
// women with diabetes run higher before a period; this shows whether she does, in her numbers. A
// description of her data, said to be one: what to do about it is her diabetes team's to say.
export function CycleGlucose({ phases }: { phases: CyclePhases }) {
  const { t, language } = useSettings();
  const unit = UNIT_LABEL[phases.unit];
  const shift = phaseShift(phases.follicular, phases.luteal);
  const glucose = (mgPerDl: number) => formatGlucose(mgPerDl, phases.unit, language);

  const said = shift
    ? shift.kind === "same"
      ? t("shiftSame")
      : t(shift.kind === "higher" ? "shiftHigher" : "shiftLower", { mean: glucose(Math.abs(shift.mean)), unit })
    : null;

  // Folded, her pattern's one sentence stays in sight under the title.
  return (
    <Fold id="cycle-glucose" className="cycle-glucose" title={t("cycleGlucoseTitle")} gist={said ?? undefined}>
      {phases.cycles === 0 && <p className="muted small">{t("cycleGlucoseWaiting")}</p>}
      {said && <p className="cycle-glucose-shift">{said}</p>}
      {phases.cycles > 0 && (
        <>
          <ul className="phases">
            {PHASES.map(({ key, label }) => (
              <Phase key={key} label={t(label)} figures={phases[key]} glucose={glucose} unit={unit} />
            ))}
          </ul>
          <p className="notice">
            <Icon name="info" size={18} />
            <span>
              {phases.cycles === 1 ? t("cycleGlucoseBasisOne") : t("cycleGlucoseBasis", { n: phases.cycles })} {t("cycleGlucoseDisclaimer")}
            </span>
          </p>
        </>
      )}
    </Fold>
  );
}

function Phase({ label, figures, glucose, unit }: { label: string; figures: PhaseFigures; glucose: (mgPerDl: number) => string; unit: string }) {
  const { t, language } = useSettings();
  return (
    <li className="phase">
      <span className="phase-name">{label}</span>
      {figures.mean === null || figures.inRange === null ? (
        <span className="phase-none muted small">{t("phaseNotEnough")}</span>
      ) : (
        <>
          <span className="phase-figure">
            <strong>{glucose(figures.mean)}</strong> <span className="muted small">{unit}</span>
          </span>
          <span className="phase-range">
            <span className="phase-bar" aria-hidden="true">
              <span style={{ width: `${Math.round(figures.inRange * 100)}%` }} />
            </span>
            <span className="small">{t("phaseInRange", { share: formatShare(figures.inRange, language) })}</span>
          </span>
        </>
      )}
    </li>
  );
}
