import { useState, type ReactNode } from "react";
import { foldOpen } from "../fold";
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
export function Fold({
  id,
  title,
  gist,
  className = "",
  children,
}: {
  id: string;
  title: string;
  gist?: string;
  className?: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(() => foldOpen(stored(id), desktop()));
  return (
    <details
      className={`card fold ${className}`}
      open={open}
      onToggle={(event) => {
        const now = event.currentTarget.open;
        setOpen(now);
        try {
          localStorage.setItem(key(id), now ? "open" : "closed");
        } catch {
          // Storage refused: it opens as the device's default next time.
        }
      }}
    >
      <summary>
        <span className="fold-head">
          <h2 className="section-title">{title}</h2>
          {!open && gist && <span className="fold-gist">{gist}</span>}
        </span>
        <Icon name="chevron" size={20} />
      </summary>
      <div className="fold-body">{children}</div>
    </details>
  );
}
