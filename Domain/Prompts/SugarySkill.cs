namespace Domain.Prompts;

// How her carb log is laid out, loaded when a request is about her foods, what she ate or the insulin
// she recorded taking. The
// columns themselves are explained by the README the mount serves, which is rendered beside the
// tables and so can never describe a column that is not there; this says where things are and how
// to find them (docs/adr/0039).
public static class SugarySkill
{
    public const string Name = "sugary-log";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Reading her carb log under `/sugary` — the foods she keeps, the meals she logged, the foods in each and the insulin doses she recorded (\"what is in her usual breakfast\", \"when did she last have paella\", \"how did her glucose do after pizza\", \"how much insulin did she take for it\"). Where each table is, where its columns are explained, and how to find a food, a meal's dose or a month.";

    public const string Body =
        """
        ### Her carb log at `/sugary`

        Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

        - `/sugary/README.md` — what every column means. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.
        - `/sugary/foods.csv` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
        - `/sugary/meals/<YYYY-MM>.csv` — one row per meal she logged that month: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards.
        - `/sugary/items/<YYYY-MM>.csv` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her.
        - `/sugary/doses/<YYYY-MM>.csv` — one row per insulin dose she took that month: its units, `rapid` or `basal` (long-acting), and the `meal_id` of the meal it was for, if any.

        A month with nothing logged has no file, and a row is filed under the month its own time shows. Every time carries its offset in the home's zone, the clock the glucose history under `/ha` is stamped on, so a meal, its dose and the readings after them line up.

        Her doses are her record of what she took, never a basis for advice. Asked what she should take, answer with what she took, the meal's logged carbs and what her glucose did, and give no number of units for any meal — not her ratio applied to its carbs, not even as arithmetic someone asked for — and no correction or timing: her dose is hers and her diabetes team's to decide.

        ### Finding things

        - One of her foods by name: `domain__filesystem__text_search` for the name, with `path` set to `/sugary/foods.csv`.
        - When she ate something: `domain__filesystem__text_search` for the name, with `directoryPath` set to `/sugary/items`; each hit carries its `meal_id` and `eaten_at`.
        - What she took for a meal: `domain__filesystem__text_search` for its `meal_id`, with `directoryPath` set to `/sugary/doses`.
        - Which months she logged: `domain__filesystem__glob` for `*.csv` with `basePath` set to `/sugary/meals` (or `/sugary/doses` for her insulin).
        - A month whole: `domain__filesystem__file_read` of its file.
        """;

    public static SkillText Text { get; } = new(Name, Description, Body);

    // The trigger claim: a request of this kind loads the skill.
    public static readonly PromptClaim LoadsForAQuestionAboutHerLog =
        new("sugary-log.loads-for-a-question-about-her-log",
            "A question about the foods she keeps or the meals she logged loads the sugary-log skill before /sugary is read.");

    // The body claim: what she took is reported, and never turned into a dose for next time.
    public static readonly PromptClaim NeverAdvisesFromHerDoses =
        new("sugary-log.never-advises-from-her-doses",
            "Asked what dose she should take, given her logged doses and outcomes, the reply reports what she took and suggests no dose, ratio, correction or timing.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [LoadsForAQuestionAboutHerLog, NeverAdvisesFromHerDoses];
}