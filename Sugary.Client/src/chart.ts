import { useLayoutEffect, useState, type RefObject } from "react";

export interface Box {
  width: number;
  height: number;
}

// How much larger than drawn a chart is ever shown: a phone's card scales it a little, as it
// always has; a desktop's wider card would blow its words and lines up to twice their size.
const MOST_SCALE = 1.15;
// How much taller than drawn it may grow on a wide card, keeping its shape until then.
const MOST_TALLER = 1.5;

// The box a chart draws in, its viewBox, for a card `measured` pixels wide (0 before it is measured).
// Up to a phone's width it is its own box, scaled; past it, it is drawn wider rather than larger.
export function chartBox(measured: number, drawn: Box): Box {
  const scale = Math.min(measured / drawn.width, MOST_SCALE);
  if (measured <= 0 || scale < MOST_SCALE) {
    return drawn;
  }
  const width = Math.round(measured / scale);
  const height = Math.round(Math.min(drawn.height * MOST_TALLER, (width * drawn.height) / drawn.width));
  return { width, height };
}

// The width an element is laid out at, followed as it changes; 0 until the first layout.
export function useWidth(element: RefObject<Element | null>): number {
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const target = element.current;
    if (!target) {
      return;
    }
    const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
    observer.observe(target);
    return () => observer.disconnect();
  }, [element]);
  return width;
}
