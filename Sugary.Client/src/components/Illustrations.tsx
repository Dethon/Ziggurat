// The cake drawings: decoration for empty and waiting moments, never beside a number. Drawn with
// the palette's own colours (see .art in styles.css), so they turn with the dark mode.

// The drawings are flat shapes without outlines, as the app icon is; anything glossy (the dome, the
// jar, the cherry) gets one highlight, a cream shape at 45 %.

// An empty cake stand under its glass dome: the log with nothing in it yet.
export function CakeStand() {
  return (
    <svg className="art" viewBox="0 0 160 128" width="160" height="128" aria-hidden="true">
      <ellipse className="art-shadow" cx="80" cy="120" rx="46" ry="5" />
      <path className="art-glass" d="M30 92c0-34 22-60 50-60s50 26 50 60Z" />
      <path className="art-highlight" d="M44 80c0-17 8-31 19-38 2-1 4 1 3 3-9 8-15 20-16 35 0 3-6 3-6 0Z" />
      <circle className="art-raspberry" cx="80" cy="27" r="6" />
      <rect className="art-sponge" x="20" y="90" width="120" height="8" rx="4" />
      <rect className="art-under" x="24" y="96" width="112" height="3" rx="1.5" />
      <path className="art-sponge" d="M74 98h12l4 16H70Z" />
      <rect className="art-sponge" x="58" y="112" width="44" height="6" rx="3" />
      <circle className="art-crumb" cx="62" cy="88" r="2" />
      <circle className="art-crumb" cx="96" cy="87" r="1.6" />
      <rect className="art-pistachio" x="70" y="85" width="7" height="2.6" rx="1.3" transform="rotate(-20 73.5 86.3)" />
      <rect className="art-lemon" x="86" y="84" width="7" height="2.6" rx="1.3" transform="rotate(25 89.5 85.3)" />
    </svg>
  );
}

// A storage jar with a raspberry lid, its label blank: the pantry with nothing kept yet.
export function Jar() {
  return (
    <svg className="art" viewBox="0 0 160 128" width="160" height="128" aria-hidden="true">
      <ellipse className="art-shadow" cx="80" cy="120" rx="40" ry="5" />
      <rect className="art-raspberry" x="52" y="14" width="56" height="16" rx="5" />
      <rect className="art-raspberry-dark" x="56" y="28" width="48" height="6" rx="2" />
      <path className="art-glass" d="M50 40a8 8 0 0 1 8-8h44a8 8 0 0 1 8 8v66a10 10 0 0 1-10 10H60a10 10 0 0 1-10-10Z" />
      <rect className="art-cream" x="60" y="58" width="40" height="26" rx="4" />
      <path className="art-label-line" d="M68 68h24M72 75h16" />
      <rect className="art-highlight" x="56" y="44" width="5" height="54" rx="2.5" />
      <circle className="art-crumb" cx="70" cy="110" r="2.2" />
      <circle className="art-crumb" cx="84" cy="111" r="1.6" />
      <rect className="art-lemon" x="90" y="108" width="7" height="2.6" rx="1.3" transform="rotate(-15 93.5 109.3)" />
    </svg>
  );
}

// The slice from the app's icon: the estimate on its way when there is no photo to show.
export function Slice() {
  return (
    <svg className="art" viewBox="96 180 320 250" width="160" height="125" aria-hidden="true">
      <ellipse className="art-shadow" cx="262" cy="372" rx="150" ry="14" />
      <g transform="translate(0 -44)">
        <polygon className="art-crust" points="318,322 408,226 408,314 318,410" />
        <polygon className="art-raspberry-dark" points="318,322 408,226 408,246 318,342" />
        <polygon className="art-sponge" points="104,320 318,380 318,410 104,350" />
        <polygon className="art-cream" points="104,306 318,366 318,380 104,320" />
        <polygon className="art-sponge" points="104,280 318,340 318,366 104,306" />
        <polygon className="art-raspberry" points="104,262 318,322 318,342 104,282" />
        <path className="art-raspberry" d="M156 296 L188 305 L188 324 a16 16 0 0 1 -32 0 Z" />
        <path className="art-raspberry" d="M248 322 L272 328.7 L272 343 a12 12 0 0 1 -24 0 Z" />
        <polygon className="art-raspberry" points="104,262 318,322 408,226" />
        <rect className="art-pistachio" x="150" y="258" width="22" height="7" rx="3.5" transform="rotate(-24 161 261)" />
        <rect className="art-lemon" x="206" y="276" width="22" height="7" rx="3.5" transform="rotate(18 217 279)" />
        <rect className="art-cream" x="232" y="252" width="22" height="7" rx="3.5" transform="rotate(-8 243 255)" />
        <path className="art-stem" d="M294 236 C296 214 306 200 324 190" />
        <circle className="art-raspberry-dark" cx="292" cy="258" r="26" />
        <ellipse className="art-highlight" cx="283" cy="249" rx="8" ry="6" />
      </g>
    </svg>
  );
}

// A band of frosting under a title, at the sign-in drip's weight: rounded ends, a lower edge that
// swells and dips, and three uneven drips whose necks taper into round tips, none at the edges.
// Drawn at its own scale, never stretched.
export function Drip({ className = "drip-line" }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 358 42" aria-hidden="true">
      <path d="M7 0H351A7 7 0 0 1 358 7V9Q358 13 352 13C339.8 13 337.3 14.8 327.5 14.8S310.4 13 303 13C294.8 13 292.5 16.8 292.5 20.8V23.5A4.5 4.5 0 0 1 283.5 23.5V20.8C283.5 16.8 281.2 13 273 13C253.2 13 249.3 11.8 233.5 11.8S205.8 13 194 13C181.9 13 178.5 19.5 178.5 28.6V32.5A6.5 6.5 0 0 1 165.5 32.5V28.6C165.5 19.5 162.1 13 150 13C133.5 13 130.2 15.2 117 15.2S93.9 13 84 13C74.1 13 71.5 18 71.5 24.2V27.5A5.5 5.5 0 0 1 60.5 27.5V24.2C60.5 18 57.9 13 48 13C37.5 13 35.4 14.5 27 14.5S10 13 6 13Q0 13 0 9V7A7 7 0 0 1 7 0Z" />
    </svg>
  );
}
