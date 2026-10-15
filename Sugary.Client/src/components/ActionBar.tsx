import { useEffect, useRef, type ReactNode } from "react";

// A screen's one primary action, pinned above the tab bar. Its height is published as
// --footer-space so the page can scroll its last content clear of it and the toast sits above it.
export function ActionBar({ active, children }: { active: boolean; children: ReactNode }) {
  const bar = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const element = bar.current;
    const root = document.documentElement;
    if (!element || !active) {
      return;
    }
    const publish = () => root.style.setProperty("--footer-space", `${element.offsetHeight}px`);
    publish();
    const observer = new ResizeObserver(publish);
    observer.observe(element);
    return () => {
      observer.disconnect();
      root.style.removeProperty("--footer-space");
    };
  }, [active]);

  return (
    <div ref={bar} className="action-bar">
      <div className="action-bar-inner">{children}</div>
    </div>
  );
}
