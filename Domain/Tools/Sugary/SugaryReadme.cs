namespace Domain.Tools.Sugary;

// What a reader of /sugary needs before reading a number: two columns are easy to misread in ways
// that put a meal on the wrong side of a glucose curve — carbs taken for total carbohydrate, and
// the first estimate taken for the whole meal.
public static class SugaryReadme
{
    public static string For(TimeZoneInfo zone) =>
        $$"""
          # Sugary — her carb log

          The meals she logged in Sugary, the foods in each meal and the foods she keeps, as tables. They are
          rendered from Sugary's database each time they are read, so they are always current. Nothing here
          can be written: her log changes only through Sugary.

          - `meals/<YYYY-MM>.csv` — one row per meal eaten that month, oldest first.
          - `items/<YYYY-MM>.csv` — one row per food in those meals, in the order of each meal.
          - `foods.csv` — one row per food she keeps on her own list.

          A month with no meals has no file. A meal belongs to the month its `eaten_at` shows.

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