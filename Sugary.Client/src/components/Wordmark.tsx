// The app's mark: a slice seen side on, flat like the drawings, frosted on top and down its back
// with a cherry on. It is drawn in its own colours (the --logo-* tokens), lighter in the dark scheme
// so the frosting and the cherry do not sink into the chocolate. It stands wherever the app names
// itself; a screen's title carries its own tab's icon instead.
export function SliceMark() {
  return (
    <svg className="slice-mark" viewBox="0 -1 32 29" aria-hidden="true">
      <path className="logo-sponge" d="M3 15.5 29 6v20.8a1.2 1.2 0 0 1-1.2 1.2H4.2A1.2 1.2 0 0 1 3 26.8Z" />
      <rect className="logo-jam" x="3" y="21.6" width="26" height="1.8" />
      <path className="logo-frosting" d="M27.4 6.6 29 6v20.8a1.2 1.2 0 0 1-1.2 1.2h-.4Z" />
      <path
        className="logo-frosting"
        d="M3 15.5 29 6v3.5l-7.6 2.78v3.32a1.5 1.5 0 0 1-3 0v-2.23l-6.6 2.41v3.52a1.6 1.6 0 0 1-3.2 0v-2.35L3 19Z"
      />
      <path className="logo-stem" d="M19.8 3.9c.4-2 1.8-3.3 4-3.8" />
      <circle className="logo-cherry" cx="19.2" cy="6.9" r="3.1" />
      <ellipse className="logo-shine" cx="18.2" cy="5.9" rx="1" ry=".75" />
    </svg>
  );
}

// The app's name with its mark, the mark's foot on the name's baseline. The rail's is hidden from a
// screen reader, since the tabs' nav already says Sugary.
export function Wordmark({ rail = false }: { rail?: boolean }) {
  return (
    <p className={rail ? "wordmark rail-brand" : "wordmark"} aria-hidden={rail || undefined}>
      <SliceMark />
      Sugary
    </p>
  );
}
