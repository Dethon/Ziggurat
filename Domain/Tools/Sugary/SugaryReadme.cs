using System.Globalization;
using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary;

// What a reader of /sugary needs before reading a number, and how to ask its tables: columns are easy to misread in ways that
// put a meal on the wrong side of a glucose curve — carbs taken for total carbohydrate, the first
// estimate taken for the whole meal, basal units taken for a meal dose, and a dose saved with its
// meal taken as injected at exactly the meal's minute — and glucose statistics counted by rows when
// the readings come at uneven intervals. Rendered per log, since it says that person's own unit and
// target range.
public static class SugaryReadme
{
    // The consensus target range, what time in range is reported against when there is no range of
    // the person's own.
    private const int DefaultLow = 70;
    private const int DefaultHigh = 180;

    public static string For(TimeZoneInfo zone, GlucoseTargets? targets) =>
        $$"""
          # Sugary — the carb log of the person you are talking with

          The meals they logged in Sugary, the foods in each meal, the insulin doses they recorded, the exercise
          they did, the foods they keep, their menstrual periods, the notes they wrote about their days, and
          their glucose as their sensor measured it, as Parquet tables. They are rendered from Sugary's database each time they are read, so they are
          always current. Nothing here can be written: the log changes only through Sugary.

          - `meals/<YYYY-MM>.parquet` — one row per meal eaten that month, oldest first.
          - `items/<YYYY-MM>.parquet` — one row per food in those meals, in the order of each meal.
          - `doses/<YYYY-MM>.parquet` — one row per insulin dose they took that month, oldest first.
          - `dose-meals.parquet` — one row per meal a dose was taken for.
          - `exercise/<YYYY-MM>.parquet` — one row per session of exercise they started that month, oldest first.
          - `foods.parquet` — one row per food they keep on their own list.
          - `periods.parquet` — one row per menstrual period they recorded, oldest first.
          - `day-notes.parquet` — one row per day they wrote a note about, oldest first.
          - `glucose/<YYYY-MM>.parquet` — one row per glucose reading measured that month, oldest first.
          - `sensors.parquet` — one row per glucose sensor they have worn.

          A month with no meals has no meals or items file, a month with no doses no doses file, and a month
          with no exercise no exercise file, a month with no readings no glucose file. A meal belongs to the
          month its `eaten_at` shows, a dose to the month its `taken_at` shows, a session to the month its
          `started_at` shows, a reading to the month its `read_at` shows.

          ## Asking

          - **`query`** answers one DuckDB SQL statement over the whole log, every month at once, as the
            tables `meals`, `items`, `doses`, `dose_meals`, `exercise`, `foods`, `periods`, `day_notes`,
            `glucose` and `sensors`. Join on `meal_id` (meals, items, dose_meals), `dose_id` (doses,
            dose_meals) and `food_id` (items, foods); join `glucose` to the rest on time (below). Only the
            rows that answer come back.
          - **A command in the sandbox** reads the files themselves at these same paths:
            `duckdb -c "select … from '/sugary/glucose/*.parquet'"`, or `polars.scan_parquet` in Python — for
            an analysis or a chart.
          - A table is not text: reading one as lines is refused.

          ## Conventions

          - **Carbs are available carbohydrate, fibre excluded**, in grams — the figure a European label
            gives and the one they count insulin against. Never compare them with a total that includes fibre.
          - Every carb figure is a range: a best value with a low and a high bound.
          - Every time is an instant, read on the home's clock ({{zone.Id}}), the same in every table, so a
            meal lines up with the glucose readings after it. `query`
            writes one as ISO 8601 with its offset. Beside each time is `<column>_local`, the wall time on
            that clock with no offset: filter a day or an hour of their life on it (`eaten_at_local::date`).
          - Days (`start_date`, `end_date`, `date`) are calendar days with no time and no offset.
          - Numbers are numbers: carbs, amounts and units are decimals, positions, minutes and day counts
            whole numbers, `edited` a boolean. An empty cell is unknown or does not apply.

          ## meals/<YYYY-MM>.parquet

          - `meal_id` — the meal's identifier; the items of the meal carry the same `meal_id`.
          - `eaten_at` — when they ate it.
          - `carbs_g` — the meal's carbs as finally logged: the sum of `carbs_g` over its items.
          - `carbs_low_g`, `carbs_high_g` — the bounds of that range, the sums of the items' bounds.
          - `first_estimate_g` — what the model first said, before any answer or correction. It covers
            only the items whose `source` is `estimate`: foods they added from a label, a barcode or their own
            list are not in it, so judge the model against the logged `carbs_g` of the meal's `estimate`
            items, never against the meal's `carbs_g`. Empty when nothing in the meal was estimated.
          - `first_estimate_low_g`, `first_estimate_high_g` — the bounds of that first range.
          - `outcome` — what their glucose did after the meal, recorded by them afterwards: `inRange`, `high`,
            `low`, or `unknown` when they did not say.
          - `items` — the names of the meal's foods in order, joined by `; `.
          - `notes` — what they wrote about the meal.

          ## items/<YYYY-MM>.parquet

          - `meal_id` — the meal the food was eaten in.
          - `eaten_at` — when that meal was eaten.
          - `position` — the food's place in the meal, from 1.
          - `name` — what the food is.
          - `portion` — the portion in words, as estimated or as they described it.
          - `amount` — the portion's weight in grams (or volume in ml), when known.
          - `carbs_per_100` — carbs per 100 g or 100 ml of the food, when known.
          - `carbs_g` — the carbs in this portion; `carbs_low_g`, `carbs_high_g` — its bounds.
          - `source` — where the numbers came from: `estimate` (the model), `label` (a nutrition label read),
            `barcode` (a product looked up) or `manual` (typed by them or taken from their own foods).
          - `confidence` — how sure the estimate was: `low`, `medium` or `high`. Only on `estimate` items.
          - `edited` — `true` when they changed the item's numbers before saving the meal.
          - `food_id` — the food on their own list this item was made from, if any (`food_id` in `foods.parquet`).
            The item keeps its own numbers, so a food edited or deleted later does not change it.

          ## doses/<YYYY-MM>.parquet

          Their record of the insulin they took, entered by them: what they injected, never a recommendation.

          - `dose_id` — the dose's identifier.
          - `taken_at` — when they took it, on the same clock as the meals.
          - `kind` — which insulin it was:
            - `rapid` — rapid-acting, taken for a meal, as a correction, or both;
            - `basal` — long-acting, their background dose, which belongs to no meal.
          - `units` — how much they took, in insulin units (not grams).
          - `note` — what they wrote about the dose.

          The meals a dose was for are in `dose-meals.parquet`.

          A dose saved together with its meal carries the meal's time unless they changed it, so a gap of
          zero minutes between the two is not a measurement of when they injected.

          ## dose-meals.parquet

          Which meals each dose was taken for, one row per link.

          - `dose_id` — the dose (`dose_id` in `doses/<YYYY-MM>.parquet`).
          - `meal_id` — a meal it was for (`meal_id` in `meals/<YYYY-MM>.parquet`).

          A meal may have several doses, a split dose or a top-up, and one dose may be for several meals, a
          meal and the dessert logged after it: that dose is all of each meal's insulin, never a share of
          it. A dose they linked to no meal has no row, which does not make it a correction: linking is
          optional and often skipped, so a rapid dose near a meal may well be for it. Match those by time,
          `taken_at` against `eaten_at` (a bolus is usually from about 30 minutes before a meal to an hour
          after it), and say that the match is yours, not theirs. A sum of `units` over this table joined to `doses` counts a dose
          for two meals twice; total a day's insulin from `doses` alone. A meal deleted from the log loses
          its rows here, and its doses stay in `doses`.

          ## exercise/<YYYY-MM>.parquet

          Their record of the exercise they did, entered by them as they did it. How exercise changes what they
          need is theirs to say: read a session beside a meal or a dose, and adjust nothing for it by a rule
          of your own.

          - `exercise_id` — the session's identifier.
          - `started_at` — when they started it, on the same clock as the meals. Started with a tap as they set
            off, so read it to the quarter hour, not the minute.
          - `ended_at` — when they stopped it; empty while it is still running.
          - `minutes` — how long it lasted, worked out from the two times and rounded to the minute; empty
            while it is still running, which is not zero minutes.
          - `kind` — what they did: `walk`, `gym`, or `other` for anything else.
          - `name` — what they called an `other` session (a bike ride, a swim); empty for a walk or the gym.
          - `note` — what they wrote about it.

          ## periods.parquet

          Their record of their periods, entered by them. The dates are days on the home's calendar, written
          `YYYY-MM-DD` with no time and no offset: to line a period up with glucose, read `glucose` for those
          days (`read_at_local::date`). The app's forecasts of their next period and fertile window
          are estimates it shows them; the forecasts are not in these tables, so every day here is one they
          recorded.

          - `period_id` — the period's identifier.
          - `start_date` — the first day of the period.
          - `end_date` — its last day; empty while it is still going.
          - `days` — how long it lasted, both ends counted; empty while it is still going.
          - `cycle_days` — the days from this period's start to the next one's: the length of the cycle it
            began. Empty for their latest period.

          ## day-notes.parquet

          What they wrote about a day — how they felt, what they craved — on a period day or any other.

          - `date` — the day, on the home's calendar.
          - `note` — what they wrote.

          ## foods.parquet

          - `food_id` — the food's identifier.
          - `name` — what they call it.
          - `carbs_per_100` — carbs per 100 of its unit, when known. Empty for a food kept only as a portion.
          - `unit` — `g` or `ml`.
          - `portion_amount` — the amount they usually have, in its unit.
          - `portion_carbs_g` — the carbs in that usual portion, for a food kept without `carbs_per_100`.
          - `barcode` — the product's barcode, when it has one.
          - `source` — where the numbers came from: `label`, `barcode` or `manual`.

          ## glucose/<YYYY-MM>.parquet

          Their glucose as their sensor measured it, which Sugary reads from LibreLinkUp through the follower
          account they linked in the app. A record of what was measured: what to do about a value is theirs
          and their clinic's. Being warned when it crosses a level, falls fast or stops coming is a glucose
          watch under `/watchers`, which the `glucose-watches` skill writes.

          - `read_at` — when the sensor measured it.
          - `mg_dl` — the value in mg/dL. Every value is kept in mg/dL, whatever unit they read in.
          - `mmol_l` — the same value in mmol/L (mg/dL ÷ 18.016), to one decimal.
          - `trend` — the sensor's arrow when it was read: `falling_fast`, `falling`, `steady`, `rising`,
            `rising_fast`, or `unknown`. Only `current` readings carry an arrow.
          - `source` — where the reading came from:
            - `current` — the reading LibreLinkUp showed live, about one a minute while their phone was in reach;
            - `graph` — a point of the curve LibreLinkUp keeps, every 5 minutes, kept only where there was no
              live reading: before they linked, or while their phone was out of reach;
            - `import` — a value from Home Assistant's history, at irregular times;
            - `import-hourly` — an hourly mean from Home Assistant's long-term statistics: an hour's average,
              not a measurement, so its lows and highs are flattened.

          {{TargetsLine(targets)}}

          A meal and its glucose line up on time: `read_at` against `eaten_at`. Each meal with the first reading,
          the peak and the last reading of the three hours after it:

          ```sql
          {{GlucoseQueries.AfterMeals}}
          ```

          ## sensors.parquet

          - `serial` — the sensor's serial number.
          - `activated_at` — when it was started.
          - `ends_at` — when it stops: {{(int)SugaryTables.SensorLifetime.TotalDays}} days after it was started, the usual sensor's
            lifetime. A sensor made to last longer ends later than this says.
          - `last_seen_at` — when LibreLinkUp last listed it. The latest started of those last listed is the one
            they wear now.

          ## Glucose statistics

          The clinic's figures for a period, as one statement: change the `period` line and run it with `query`.
          They are shares of time, not of rows. Readings come at uneven intervals — about one a minute live,
          every 5 minutes on the curve, hourly from `import-hourly` — so a count of rows weighs a stretch by
          how often it was read. Each reading stands for the minutes until the next one, at most
          {{GlucoseQueries.MaxMinutesPerReading}}; a longer gap, an hourly mean's included, is time with no reading. These are the
          figures the Sugary app shows them, worked out the same way. A `TIMESTAMPTZ '2026-10-01 00:00:00'`
          literal reads on the home's clock.

          ```sql
          {{GlucoseQueries.Statistics(GlucoseQueries.LastFourteenDays, GlucoseQueries.Now, targets?.Low ?? DefaultLow, targets?.High ?? DefaultHigh)}}
          ```

          - `below_54_pct`, `below_70_pct` — time below 54 and below 70 mg/dL (the second includes the first).
          - `in_70_180_pct` — time in the consensus range, 70–180 mg/dL; `in_target_pct` — time in their own
            target range, both ends included.
          - `above_180_pct`, `above_250_pct` — time above 180 and above 250 mg/dL (the first includes the second).
          - `mean_mg_dl` — the mean, weighted by time.
          - `gmi_pct` — the glucose management indicator, 3.31 + 0.02392 × the mean in mg/dL: an estimate of
            the A1c, in %.
          - `cv_pct` — the coefficient of variation: the time-weighted population standard deviation over the
            mean, in %. 36 % or less is the target.
          - `coverage_pct` — the share of the period covered by readings. Under 70 % the figures may not stand
            for the period: give each one with its coverage ("62 % in range, over the 55 % of the fortnight
            with readings"). No row means no readings in the period.
          """;

    // Their unit and range as their link keeps them, or that there is none.
    private static string TargetsLine(GlucoseTargets? targets) => targets is null
        ? "They have not linked LibreLinkUp in Sugary, so there is no unit or target range of theirs here: give "
          + $"values in mg/dL, and judge time in range against the consensus range, {DefaultLow}–{DefaultHigh} mg/dL."
        : (targets.Unit == GlucoseUnit.Mmol ? "They read glucose in mmol/L" : "They read glucose in mg/dL")
          + $", and their target range is {targets.Low}–{targets.High} mg/dL ({Mmol(targets.Low)}–{Mmol(targets.High)} mmol/L). "
          + "Give values in their unit.";

    private static string Mmol(int mgDl) =>
        Math.Round(mgDl / SugaryTables.MgDlPerMmolL, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
}