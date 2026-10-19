namespace Domain.Prompts;

// How her carb log is laid out, loaded when a request is about her foods, what she ate, the insulin
// she recorded taking, her exercise or her periods. The
// columns themselves are explained by the README the mount serves, which is rendered beside the
// tables and so can never describe a column that is not there; this says what the tables are and how
// to ask them (docs/adr/0039).
public static class SugarySkill
{
    public const string Name = "sugary-log";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Reading her carb log under `/sugary` — the foods she keeps, the meals she logged and the foods in each, her insulin doses, her exercise and her periods (\"when did she last have paella\", \"how did her glucose do after pizza\", \"how much insulin did she take for it\", \"was she on her period that week\"). Which tables there are, where their columns are explained, and how to ask them.";

    public const string Body =
        """
        ### Her carb log at `/sugary`

        Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

        `/sugary/README.md` explains every column. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.

        Her log is tables, asked with `domain__filesystem__query` on `/sugary`: one DuckDB SQL statement sees every month at once.

        - `meals` — one row per meal she logged: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards (`outcome`).
        - `items` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her. `meal_id` joins it to `meals`.
        - `doses` — one row per insulin dose she took: its `units`, `rapid` or `basal` (long-acting). `dose_meals` links it to meals; unlinked, a rapid dose may be for a nearby meal, not only a correction.
        - `exercise` — one row per session she started: `walk`, `gym` or a named `other`, its start and end, and its `minutes` (empty while it is running).
        - `foods` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
        - `periods` — her menstrual periods: `start_date`, `end_date` (empty while it is going), each one's length and its cycle's. `day_notes` — what she wrote about a day. Both are days on the home's calendar: read `/ha` history for those days beside them.

        Every time is on the home's clock, the one the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up. Beside each is `<column>_local`, its wall time, for a day or an hour of her life: `eaten_at_local::date = '2026-10-03'`.

        Asked what she should take, work a dose out from her own numbers, never a guessed one: her carb ratio; for a correction, her correction factor, target and glucose now (the sensor under `/ha`); and how long her rapid insulin acts. Use the values she or fran gave you, which your memory keeps; ask for any you lack, and give no number until you have it. Show the working: the carbs as their logged range, so the dose is a range; rapid units from recent `doses` still on board, subtracted; any correction; and how similar meals went after what she took, naming them, where that shifts it. Adjust for exercise only by a rule she gave. Say which values you used, and that it is hers to check before injecting.

        ### Asking

        - When she last ate something: `select eaten_at from items where name ilike '%lentejas%' order by eaten_at desc limit 1`.
        - What she took for a meal: `doses` joined to `dose_meals` on `dose_id`, then to `meals` on `meal_id`. A day's units: `doses` alone, so a dose for two meals counts once.
        - A total, an average or a count: one statement that computes it, never a table read whole to add up by hand.
        - A table is not text, and reading one as a file is refused. For a chart, a longer analysis or her log beside `/ha` history, run a command in the sandbox: `duckdb` and Python's `polars` read `/sugary/meals/*.parquet` and the other tables' files directly.
        """;

    public static SkillText Text { get; } = new(Name, Description, Body);

    // The trigger claim: a request of this kind loads the skill.
    public static readonly PromptClaim LoadsForAQuestionAboutHerLog =
        new("sugary-log.loads-for-a-question-about-her-log",
            "A question about the foods she keeps or the meals she logged loads the sugary-log skill before /sugary is queried.");

    // The body claims: a dose is worked out from the numbers she gave, shown with its working, and a
    // number she has not given is asked for rather than assumed.
    public static readonly PromptClaim WorksADoseFromHerNumbers =
        new("sugary-log.works-a-dose-from-her-numbers",
            "Asked what dose she should take with her ratio given, the reply works one out from that ratio over the meal's logged carb range, weighs how similar meals went after what she took, says which values it used, and invents no value it was not given.");

    public static readonly PromptClaim AsksForAMissingNumber =
        new("sugary-log.asks-for-a-missing-number",
            "Asked what dose she should take without her ratio, the reply asks for it and gives no number of units.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
        [LoadsForAQuestionAboutHerLog, WorksADoseFromHerNumbers, AsksForAMissingNumber];
}