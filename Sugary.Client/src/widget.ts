// The one image a KWGT home screen widget shows: the server draws the reading, its trend, its time and the
// curve. KWGT re-fetches an image whose address changes, but leaves a text's web formula on its cache, so
// the address carries the minute, whole, because a value that came round again could be answered from it.
export function widgetImageAddress(origin: string, token: string): string {
  return `${origin}/widget/${token}/card.png?hours=6&width=800&height=320&theme=dark&t=$df(yyMMddHHmm)$`;
}
