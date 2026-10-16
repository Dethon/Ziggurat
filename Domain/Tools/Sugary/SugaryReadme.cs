namespace Domain.Tools.Sugary;

// What a reader of /sugary needs before reading a number: columns are easy to misread in ways that
// put a meal on the wrong side of a glucose curve — carbs taken for total carbohydrate, the first
// estimate taken for the whole meal, basal units taken for a meal dose, and a dose saved with its
// meal taken as injected at exactly the meal's minute.
public static class SugaryReadme
{
    public static string For(TimeZoneInfo zone) =>
        $$"""
          # Sugary — her carb log

          The meals she logged in Sugary, the foods in each meal, the insulin doses she recorded, the foods
          she keeps, her menstrual periods and the notes she wrote about her days, as tables. They are rendered from Sugary's database each time they are read, so they are
          always current. Nothing here can be written: her log changes only through Sugary.

          - `meals/<YYYY-MM>.csv` — one row per meal eaten that month, oldest first.
          - `items/<YYYY-MM>.csv` — one row per food in those meals, in the order of each meal.
          - `doses/<YYYY-MM>.csv` — one row per insulin dose she took that month, oldest first.
          - `foods.csv` — one row per food she keeps on her own list.
          - `periods.csv` — one row per menstrual period she recorded, oldest first.
          - `day-notes.csv` — one row per day she wrote a note about, oldest first.

          A month with no meals has no meals or items file, and a month with no doses has no doses file. A
          meal belongs to the month its `eaten_at` shows, a dose to the month its `taken_at` shows.

          ## Conventions

          - **Carbs are available carbohydrate, fibre excluded**, in grams — the figure a European label
            gives and the one she counts insulin against. Never compare them with a total that includes fibre.
          - Every carb figure is a range: a best value with a low and a high bound.
          - Every time is ISO 8601 with its offset, in the home's zone ({{zone.Id}}), the clock the Home
            Assistant mount's history is stamped on, so a meal lines up with the glucose readings after it.
            Use the offset as written; do not convert.
          - Numbers use a point for decimals and at most two decimal places. An empty cell is unknown or
            does not apply.
          - RFC 4180: a header row, cells with commas, quotes or line breaks quoted. UTF-8 without a
            byte-order mark. Text beginning with `=`, `+`, `-` or `@` is written with a leading apostrophe so
            a spreadsheet shows it rather than running it.

          ## meals/<YYYY-MM>.csv

          - `meal_id` — the meal's identifier; the items of the meal carry the same `meal_id`.
          - `eaten_at` — when she ate it.
          - `carbs_g` — the meal's carbs as finally logged: the sum of `carbs_g` over its items.
          - `carbs_low_g`, `carbs_high_g` — the bounds of that range, the sums of the items' bounds.
          - `first_estimate_g` — what the model first said, before any answer or correction. It covers
            only the items whose `source` is `estimate`: foods she added from a label, a barcode or her own
            list are not in it, so judge the model against the logged `carbs_g` of the meal's `estimate`
            items, never against the meal's `carbs_g`. Empty when nothing in the meal was estimated.
          - `first_estimate_low_g`, `first_estimate_high_g` — the bounds of that first range.
          - `outcome` — what her glucose did after the meal, recorded by her afterwards: `inRange`, `high`,
            `low`, or `unknown` when she did not say.
          - `items` — the names of the meal's foods in order, joined by `; `.
          - `notes` — what she wrote about the meal.

          ## items/<YYYY-MM>.csv

          - `meal_id` — the meal the food was eaten in.
          - `eaten_at` — when that meal was eaten.
          - `position` — the food's place in the meal, from 1.
          - `name` — what the food is.
          - `portion` — the portion in words, as estimated or as she described it.
          - `amount` — the portion's weight in grams (or volume in ml), when known.
          - `carbs_per_100` — carbs per 100 g or 100 ml of the food, when known.
          - `carbs_g` — the carbs in this portion; `carbs_low_g`, `carbs_high_g` — its bounds.
          - `source` — where the numbers came from: `estimate` (the model), `label` (a nutrition label read),
            `barcode` (a product looked up) or `manual` (typed by her or taken from her own foods).
          - `confidence` — how sure the estimate was: `low`, `medium` or `high`. Only on `estimate` items.
          - `edited` — `true` when she changed the item's numbers before saving the meal.
          - `food_id` — the food on her own list this item was made from, if any (`food_id` in `foods.csv`).
            The item keeps its own numbers, so a food edited or deleted later does not change it.

          ## doses/<YYYY-MM>.csv

          Her record of the insulin she took, entered by her: never a recommendation, and never a basis for
          one.

          - `dose_id` — the dose's identifier.
          - `taken_at` — when she took it, on the same clock as the meals.
          - `kind` — which insulin it was:
            - `rapid` — rapid-acting, taken for a meal or as a correction;
            - `basal` — long-acting, her background dose, which belongs to no meal.
          - `units` — how much she took, in insulin units (not grams).
          - `meal_id` — the meal it was taken for (`meal_id` in `meals/<YYYY-MM>.csv`),
            empty for a dose not linked to a meal. A meal may have several doses, a split dose or a top-up.
            A meal deleted from the log keeps its doses here, with this cell emptied.
          - `note` — what she wrote about the dose.

          A dose saved together with its meal carries the meal's time unless she changed it, so a gap of
          zero minutes between the two is not a measurement of when she injected.

          ## periods.csv

          Her record of her periods, entered by her. The dates are days on the home's calendar, written
          `YYYY-MM-DD` with no time and no offset: to line a period up with glucose, read the history under
          the Home Assistant mount for those days. The app's forecasts of her next period and fertile window
          are estimates it shows her; the forecasts are not in these tables, so every day here is one she
          recorded.

          - `period_id` — the period's identifier.
          - `start_date` — the first day of the period.
          - `end_date` — its last day; empty while it is still going.
          - `days` — how long it lasted, both ends counted; empty while it is still going.
          - `cycle_days` — the days from this period's start to the next one's: the length of the cycle it
            began. Empty for her latest period.

          ## day-notes.csv

          What she wrote about a day — how she felt, what she craved — on a period day or any other.

          - `date` — the day, on the home's calendar.
          - `note` — what she wrote.

          ## foods.csv

          - `food_id` — the food's identifier.
          - `name` — what she calls it.
          - `carbs_per_100` — carbs per 100 of its unit, when known. Empty for a food kept only as a portion.
          - `unit` — `g` or `ml`.
          - `portion_amount` — the amount she usually has, in its unit.
          - `portion_carbs_g` — the carbs in that usual portion, for a food kept without `carbs_per_100`.
          - `barcode` — the product's barcode, when it has one.
          - `source` — where the numbers came from: `label`, `barcode` or `manual`.
          """;
}