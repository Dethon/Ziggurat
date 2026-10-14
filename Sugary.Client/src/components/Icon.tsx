const paths = {
  camera:
    "M4 8.5A2.5 2.5 0 0 1 6.5 6h1.2l.9-1.4A1.5 1.5 0 0 1 9.9 4h4.2a1.5 1.5 0 0 1 1.3.6l.9 1.4h1.2A2.5 2.5 0 0 1 20 8.5v8A2.5 2.5 0 0 1 17.5 19h-11A2.5 2.5 0 0 1 4 16.5v-8ZM12 16a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z",
  image:
    "M5.5 4h13A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-13A1.5 1.5 0 0 1 5.5 4ZM4 16l4.5-4.5 3.5 3.5 2.5-2.5L20 18M15.5 9.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z",
  barcode: "M4 6v12M7 6v12M10 6v9M13 6v12M16 6v9M20 6v12M9.5 18h1M15.5 18h1",
  pencil: "M4 20l1-4L16.5 4.5a1.8 1.8 0 0 1 2.5 0l.5.5a1.8 1.8 0 0 1 0 2.5L8 19l-4 1ZM14.5 6.5l3 3",
  plate: "M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z",
  bookmark: "M7 4h10a1 1 0 0 1 1 1v15l-6-4-6 4V5a1 1 0 0 1 1-1Z",
  download: "M12 4v11M7.5 11l4.5 4.5 4.5-4.5M5 19h14",
  back: "M14 6l-6 6 6 6",
  list: "M8 7h12M8 12h12M8 17h12M4 7h.01M4 12h.01M4 17h.01",
  sliders: "M5 8h8M17 8h2M5 16h2M11 16h8M15 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM9 18a2 2 0 1 0 0-4 2 2 0 0 0 0 4Z",
  close: "M6 6l12 12M18 6L6 18",
  chevron: "M8 10l4 4 4-4",
  minus: "M6 12h12",
  plus: "M12 6v12M6 12h12",
  check: "M5 12.5l4.5 4.5L19 7.5",
  info: "M12 20a8 8 0 1 0 0-16 8 8 0 0 0 0 16ZM12 11v5M12 8h.01",
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
