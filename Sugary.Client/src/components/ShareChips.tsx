const SHARES = [
  { share: 0.5, label: "½" },
  { share: 0.75, label: "¾" },
  { share: 1, label: "1" },
  { share: 1.5, label: "1½" },
  { share: 2, label: "2" },
];

// Shares of a known amount (her usual, a serving, what the model saw), one convention everywhere:
// "×½ · 125 g", or "×½" alone when the amount is counted in portions.
export function ShareChips({
  base,
  unit,
  current,
  label,
  onPick,
}: {
  base: number;
  // Without a unit the amount is a number of portions.
  unit?: string;
  current: number | null;
  label: string;
  onPick: (amount: number) => void;
}) {
  return (
    <div className="chips shares" role="group" aria-label={label}>
      {SHARES.map(({ share, label: fraction }) => {
        const amount = unit ? Math.round(base * share) : share;
        const on = current !== null && (unit ? Math.round(current) === amount : current === amount);
        return (
          <button
            key={share}
            type="button"
            className={on ? "chip chip-on" : "chip"}
            aria-pressed={on}
            onClick={() => onPick(amount)}
          >
            <span className="share">×{fraction}</span>
            {unit && (
              <>
                {" · "}
                {amount}&nbsp;{unit}
              </>
            )}
          </button>
        );
      })}
    </div>
  );
}
