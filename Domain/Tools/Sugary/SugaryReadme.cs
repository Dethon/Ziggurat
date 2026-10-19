namespace Domain.Tools.Sugary;

// What a reader of /sugary needs before reading a number, and how to ask its tables: columns are easy to misread in ways that
// put a meal on the wrong side of a glucose curve — carbs taken for total carbohydrate, the first
// estimate taken for the whole meal, basal units taken for a meal dose, and a dose saved with its
// meal taken as injected at exactly the meal's minute.
public static class SugaryReadme
{
    public static string For(TimeZoneInfo zone) =>
        $$"""
          # Sugary — her carb log

          The meals she logged in Sugary, the foods in each meal, the insulin doses she recorded, the exercise
          she did, the foods she keeps, her menstrual periods and the notes she wrote about her days, as
          Parquet tables. They are rendered from Sugary's database each time they are read, so they are
          always current. Nothing here can be written: her log changes only through Sugary.

          - `meals/<YYYY-MM>.parquet` — one row per meal eaten that month, oldest first.
          - `items/<YYYY-MM>.parquet` — one row per food in those meals, in the order of each meal.
          - `doses/<YYYY-MM>.parquet` — one row per insulin dose she took that month, oldest first.
          - `dose-meals.parquet` — one row per meal a dose was taken for.
          - `exercise/<YYYY-MM>.parquet` — one row per session of exercise she started that month, oldest first.
          - `foods.parquet` — one row per food she keeps on her own list.
          - `periods.parquet` — one row per menstrual period she recorded, oldest first.
          - `day-notes.parquet` — one row per day she wrote a note about, oldest first.

          A month with no meals has no meals or items file, a month with no doses no doses file, and a month
          with no exercise no exercise file. A meal belongs to the month its `eaten_at` shows, a dose to the
          month its `taken_at` shows, a session to the month its `started_at` shows.

          ## Asking

          - **`query`** answers one DuckDB SQL statement over the whole log, every month at once, as the
            tables `meals`, `items`, `doses`, `dose_meals`, `exercise`, `foods`, `periods` and `day_notes`.
            Join on `meal_id` (meals, items, dose_meals), `dose_id` (doses, dose_meals) and `food_id`
            (items, foods). Only the rows that answer come back.
          - **A command in the sandbox** reads the files themselves at these same paths:
            `duckdb -c "select … from '/sugary/meals/*.parquet'"`, or `polars.scan_parquet` in Python — for
            an analysis, a chart, or her log beside the glucose history under the Home Assistant mount.
          - A table is not text: reading one as lines is refused.

          ## Conventions

          - **Carbs are available carbohydrate, fibre excluded**, in grams — the figure a European label
            gives and the one she counts insulin against. Never compare them with a total that includes fibre.
          - Every carb figure is a range: a best value with a low and a high bound.
          - Every time is an instant, read on the home's clock ({{zone.Id}}) — the clock the Home Assistant
            mount's history is stamped on, so a meal lines up with the glucose readings after it. `query`
            writes one as ISO 8601 with its offset. Beside each time is `<column>_local`, the wall time on
            that clock with no offset: filter a day or an hour of her life on it (`eaten_at_local::date`).
          - Days (`start_date`, `end_date`, `date`) are calendar days with no time and no offset.
          - Numbers are numbers: carbs, amounts and units are decimals, positions, minutes and day counts
            whole numbers, `edited` a boolean. An empty cell is unknown or does not apply.

          ## meals/<YYYY-MM>.parquet

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

          ## items/<YYYY-MM>.parquet

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
          - `food_id` — the food on her own list this item was made from, if any (`food_id` in `foods.parquet`).
            The item keeps its own numbers, so a food edited or deleted later does not change it.

          ## doses/<YYYY-MM>.parquet

          Her record of the insulin she took, entered by her: what she injected, never a recommendation.

          - `dose_id` — the dose's identifier.
          - `taken_at` — when she took it, on the same clock as the meals.
          - `kind` — which insulin it was:
            - `rapid` — rapid-acting, taken for a meal, as a correction, or both;
            - `basal` — long-acting, her background dose, which belongs to no meal.
          - `units` — how much she took, in insulin units (not grams).
          - `note` — what she wrote about the dose.

          The meals a dose was for are in `dose-meals.parquet`.

          A dose saved together with its meal carries the meal's time unless she changed it, so a gap of
          zero minutes between the two is not a measurement of when she injected.

          ## dose-meals.parquet

          Which meals each dose was taken for, one row per link.

          - `dose_id` — the dose (`dose_id` in `doses/<YYYY-MM>.parquet`).
          - `meal_id` — a meal it was for (`meal_id` in `meals/<YYYY-MM>.parquet`).

          A meal may have several doses, a split dose or a top-up, and one dose may be for several meals, a
          meal and the dessert logged after it: that dose is all of each meal's insulin, never a share of
          it. A dose she linked to no meal has no row, which does not make it a correction: linking is
          optional and often skipped, so a rapid dose near a meal may well be for it. Match those by time,
          `taken_at` against `eaten_at` (a bolus is usually from about 30 minutes before a meal to an hour
          after it), and say that the match is yours, not hers. A sum of `units` over this table joined to `doses` counts a dose
          for two meals twice; total a day's insulin from `doses` alone. A meal deleted from the log loses
          its rows here, and its doses stay in `doses`.

          ## exercise/<YYYY-MM>.parquet

          Her record of the exercise she did, entered by her as she did it. How exercise changes what she
          needs is hers to say: read a session beside a meal or a dose, and adjust nothing for it by a rule
          of your own.

          - `exercise_id` — the session's identifier.
          - `started_at` — when she started it, on the same clock as the meals. Started with a tap as she set
            off, so read it to the quarter hour, not the minute.
          - `ended_at` — when she stopped it; empty while it is still running.
          - `minutes` — how long it lasted, worked out from the two times and rounded to the minute; empty
            while it is still running, which is not zero minutes.
          - `kind` — what she did: `walk`, `gym`, or `other` for anything else.
          - `name` — what she called an `other` session (a bike ride, a swim); empty for a walk or the gym.
          - `note` — what she wrote about it.

          ## periods.parquet

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

          ## day-notes.parquet

          What she wrote about a day — how she felt, what she craved — on a period day or any other.

          - `date` — the day, on the home's calendar.
          - `note` — what she wrote.

          ## foods.parquet

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