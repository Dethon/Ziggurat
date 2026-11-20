import { useState, type ReactNode } from "react";
import { foldOpen, type FoldStart } from "../fold";
import { Icon } from "./Icon";

const key = (id: string) => `sugary.fold.${id}`;

function stored(id: string): string | null {
  try {
    return localStorage.getItem(key(id));
  } catch {
    return null;
  }
}

// The same literal as styles.css's "A desktop".
const desktop = () => typeof matchMedia === "function" && matchMedia("(min-width: 64rem)").matches;

// A card she can fold to its title, and its one-line gist while folded; how she left it is kept on the
// device (a per-viewer convenience, so browser storage), else folded on a phone and open on a desktop.
// One that starts "folded" keeps nothing: it is folded each time it is drawn.
export function Fold({
  id,
  title,
  gist,
  aside,
  lede,
  start = "by-screen",
  className = "",
  children,
}: {
  id: string;
  title: string;
  gist?: string;
  // On the title's own line, folded or not: the one thing the card is read for.
  aside?: ReactNode;
  // Under the title, folded or not: what must never be a tap away.
  lede?: ReactNode;
  // How it opens until she has folded or opened it on this device.
  start?: FoldStart;
  className?: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(() => foldOpen(stored(id), desktop(), start));
  return (
    <details
      className={`card fold ${className}`}
      open={open}
      onToggle={(event) => {
        const now = event.currentTarget.open;
        setOpen(now);
        if (start === "folded") {
          return;
        }
        try {
          localStorage.setItem(key(id), now ? "open" : "closed");
        } catch {
          // Storage refused: it opens as the device's default next time.
        }
      }}
    >
      <summary>
        <span className="fold-head">
          <span className="fold-title-line">
            <h2 className="section-title">{title}</h2>
            {aside}
          </span>
          {lede}
          {!open && gist && <span className="fold-gist">{gist}</span>}
        </span>
        <Icon name="chevron" size={20} />
      </summary>
      <div className="fold-body">{children}</div>
    </details>
  );
}
