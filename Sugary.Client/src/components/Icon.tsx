// One hand-drawn set on a 24 px grid, 1.8 stroke, round caps, drawn in the current text colour.
const paths = {
  camera:
    "M4 8.5A2.5 2.5 0 0 1 6.5 6h1.2l.9-1.4A1.5 1.5 0 0 1 9.9 4h4.2a1.5 1.5 0 0 1 1.3.6l.9 1.4h1.2A2.5 2.5 0 0 1 20 8.5v8A2.5 2.5 0 0 1 17.5 19h-11A2.5 2.5 0 0 1 4 16.5v-8ZM12 16a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z",
  image:
    "M5.5 4h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-13A1.5 1.5 0 0 1 5.5 4ZM4 16l4.5-4.5 3.5 3.5 2.5-2.5L20 18M15.5 9.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z",
  video: "M4 7.5A1.5 1.5 0 0 1 5.5 6h9A1.5 1.5 0 0 1 16 7.5v9a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 4 16.5v-9ZM16 10.5l4-2.5v8l-4-2.5",
  barcode: "M4 6v12M7 6v12M10 6v9M13 6v12M16 6v9M20 6v12M9.5 18h1M15.5 18h1",
  label: "M4 4.5h7.2l8.3 8.3a1.5 1.5 0 0 1 0 2.1l-4.6 4.6a1.5 1.5 0 0 1-2.1 0L4.5 11.2V4.5ZM8.5 9a.5.5 0 1 0 0-1 .5.5 0 0 0 0 1Z",
  pencil: "M4 20l1-4L16.5 4.5a1.8 1.8 0 0 1 2.5 0l.5.5a1.8 1.8 0 0 1 0 2.5L8 19l-4 1ZM14.5 6.5l3 3",
  plate: "M15 20a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13ZM15 16.5a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM4 4v4.5a2 2 0 0 0 4 0V4M6 4v16",
  book: "M5 5.5A1.5 1.5 0 0 1 6.5 4H19v13.5H6.5A1.5 1.5 0 0 0 5 19V5.5ZM5 19a1.5 1.5 0 0 0 1.5 1.5H19M9 8.5h6",
  jar: "M8 3.5h8v3H8v-3ZM7.5 6.5h9a2 2 0 0 1 2 2V19a1.5 1.5 0 0 1-1.5 1.5H7A1.5 1.5 0 0 1 5.5 19V8.5a2 2 0 0 1 2-2ZM8.5 11.5h7v4h-7v-4Z",
  pot: "M5 10h14v7.5A2.5 2.5 0 0 1 16.5 20h-9A2.5 2.5 0 0 1 5 17.5V10ZM3 10h18M2.5 13H5M19 13h2.5M7.5 7.5h9M12 5v2.5",
  slice: "M3.5 19.5V14.8L19.8 8.1a.9.9 0 0 1 1.2.8v10.6H3.5ZM3.5 16.8h17.5M14 9.6V8.4M15.5 5.2a1.3 1.3 0 1 0 0 2.6 1.3 1.3 0 0 0 0-2.6Z",
  bookmark: "M7 4h10a1 1 0 0 1 1 1v15l-6-4-6 4V5a1 1 0 0 1 1-1Z",
  download: "M12 4v11M7.5 11l4.5 4.5 4.5-4.5M5 19h14",
  file: "M7 3.5h6.8l4.2 4.2V20a.5.5 0 0 1-.5.5h-10.5A.5.5 0 0 1 6.5 20V4a.5.5 0 0 1 .5-.5ZM13.5 3.5V8H18",
  back: "M14 6l-6 6 6 6",
  list: "M8 7h12M8 12h12M8 17h12M4 7h.01M4 12h.01M4 17h.01",
  sliders: "M5 8h8M17 8h2M5 16h2M11 16h8M15 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM9 18a2 2 0 1 0 0-4 2 2 0 0 0 0 4Z",
  close: "M6 6l12 12M18 6L6 18",
  chevron: "M8 10l4 4 4-4",
  chevronRight: "M10 7l5 5-5 5",
  minus: "M6 12h12",
  plus: "M12 6v12M6 12h12",
  check: "M5 12.5l4.5 4.5L19 7.5",
  checkCircle: "M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM8.5 12.2l2.4 2.4 4.6-4.8",
  info: "M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM12 11v5M12 8h.01",
  alert: "M12 4.5 20.5 19h-17L12 4.5ZM12 10v4M12 16.6h.01",
  flip: "M4.5 11a7.5 7.5 0 0 1 13.4-4.2M19.5 13a7.5 7.5 0 0 1-13.4 4.2M18.5 3v4h-4M5.5 21v-4h4",
  search: "M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13ZM15.5 15.5 20 20",
  play: "M9 7.2v9.6l7.6-4.8L9 7.2Z",
  arrowDown: "M12 5v13M6.5 12.5 12 18l5.5-5.5",
  arrowUp: "M12 19V6M6.5 11.5 12 6l5.5 5.5",
  trash: "M5 7h14M10 7V5h4v2M7 7l1 12.5h8L17 7",
  restart: "M4.5 12a7.5 7.5 0 1 0 2.2-5.3M4.5 4v3.5H8",
  eye: "M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12ZM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z",
  eyeOff:
    "M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12ZM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM4 4l16 16",
  torch: "M8 3h8l-1 5 2 3v2h-4v8h-2v-8H7v-2l2-3-1-5Z",
  // An insulin pen, for a dose; rapid and basal are told apart by a bolt and a clock as well as by tint.
  // A reminder, for a pen due to be replaced.
  bell: "M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15L6 16.5ZM10 20.5a2 2 0 0 0 4 0",
  insulin: "M3.5 9.5h11a1.5 1.5 0 0 1 1.5 1.5v2a1.5 1.5 0 0 1-1.5 1.5h-11v-5ZM16 12h5M3.5 12H2M8 9.5v5M10.5 9.5v2.5",
  bolt: "M13 3.5 6 13h5.5l-1 7.5L18 11h-5.5l.5-7.5Z",
  clock: "M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM12 7.5V12l3 2",
  // A drop, for her period: the tab, a recorded day's mark and the Log's period day.
  drop: "M12 3.5c3 4.1 6 7.4 6 11a6 6 0 0 1-12 0c0-3.6 3-6.9 6-11Z",
  // Her exercise: a figure walking, a dumbbell for the gym, a pulse for anything else, and a square
  // to stop the one running.
  walk: "M13.5 6a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3ZM12.5 8l-2 5.5M10.5 13.5 8 20.5M10.5 13.5l3 2.5 1 4.5M7.5 11.5l2.5-3.5h2.5l1.5 2.5 3 1",
  dumbbell: "M3.5 10v4M6.5 7.5v9M9 9.5v5M15 9.5v5M17.5 7.5v9M20.5 10v4M9 12h6",
  pulse: "M3.5 12h4l2.5-6 4 12 2.5-6h4",
  stop: "M8.5 7h7A1.5 1.5 0 0 1 17 8.5v7a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 7 15.5v-7A1.5 1.5 0 0 1 8.5 7Z",
  calendar: "M5.5 5.5h13A1.5 1.5 0 0 1 20 7v11.5a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5V7a1.5 1.5 0 0 1 1.5-1.5ZM4 10h16M8.5 3.5v4M15.5 3.5v4",
  signOut: "M14 4.5h4A1.5 1.5 0 0 1 19.5 6v12a1.5 1.5 0 0 1-1.5 1.5h-4M10 8l-4 4 4 4M6 12h9",
  // A person, for their account.
  person: "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM4.5 20a7.5 7.5 0 0 1 15 0",
} as const;

export type IconName = keyof typeof paths;

export function Icon({ name, size = 22 }: { name: IconName; size?: number }) {
  return (
    <svg
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}

// How sure the estimate is, drawn as one, two or three filled bars so it never rests on colour.
export function Meter({ level, size = 20 }: { level: 1 | 2 | 3; size?: number }) {
  return (
    <svg className="icon meter" width={size} height={size} viewBox="0 0 16 16" aria-hidden="true">
      {[0, 1, 2].map((bar) => (
        <rect
          key={bar}
          x={1 + bar * 5}
          y={10 - bar * 3.5}
          width="4"
          height={5 + bar * 3.5}
          rx="1"
          fill={bar < level ? "currentColor" : "none"}
          stroke="currentColor"
          strokeWidth="1.2"
        />
      ))}
    </svg>
  );
}
