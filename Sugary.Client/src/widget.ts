// What a KWGT home screen widget is told to show: a formula for each text and the curve's image address.
// The age is worked out on the phone from the reading's instant, so it keeps moving between fetches and
// an old reading can never pass for a current one.
export interface WidgetFormulas {
  reading: string;
  unit: string;
  arrow: string;
  age: string;
  color: string;
  curve: string;
}

export function widgetFormulas(origin: string, token: string): WidgetFormulas {
  const base = `${origin}/widget/${token}`;
  const field = (name: string) => `wg("${base}/now.json", json, .${name})`;
  return {
    reading: `$${field("value")}$`,
    unit: `$${field("unit")}$`,
    arrow: `$${field("arrow")}$`,
    age: `$tf(dp(${field("at")}, auto))$`,
    color: `$${field("color")}$`,
    curve: `${base}/curve.png?hours=6&width=800&height=320&theme=dark`,
  };
}
