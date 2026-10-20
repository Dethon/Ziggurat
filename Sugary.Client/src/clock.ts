import { useEffect, useState } from "react";

const MINUTE = 60_000;

// The time, read again every minute and whenever the app comes back to the screen, so what depends
// on it (a meal crossing the two hours after which its outcome is asked) shows up without a reload.
export function useMinute(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const tick = () => setNow(Date.now());
    const timer = window.setInterval(tick, MINUTE);
    const shown = () => document.visibilityState === "visible" && tick();
    document.addEventListener("visibilitychange", shown);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", shown);
    };
  }, []);
  return now;
}
