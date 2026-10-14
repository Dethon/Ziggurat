namespace Domain.Prompts;

// How her carb log is laid out, loaded when a request is about her foods or what she ate. The
// columns themselves are explained by the README the mount serves, which is rendered beside the
// tables and so can never describe a column that is not there; this says where things are and how
// to find them (docs/adr/0039).
public static class SugarySkill
{
    public const string Name = "sugary-log";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Reading her carb log under `/sugary` — the foods she keeps, the meals she logged and the foods in each (\"what is in her usual breakfast\", \"when did she last have paella\", \"how did her glucose do after pizza\"). Where each table is, where its columns are explained, and how to find a food or a month.";

    public const string Body =
        """
        ### Her carb log at `/sugary`

        Read-only, and rendered from Sugary's database on every read, so it is always current. Nothing under it can be written: her log changes only through the Sugary app.

        - `/sugary/README.md` — what every column means. Read it before reading a number: carbs are available carbohydrate with fibre excluded, every carb figure is a range, and a meal's first estimate covers only the foods the model estimated.
        - `/sugary/foods.csv` — the foods she keeps, one row each: what she calls it, its carbs per 100 g or ml, the amount she usually has and, for a food kept only as a portion, the carbs in that portion.
        - `/sugary/meals/<YYYY-MM>.csv` — one row per meal she logged that month: its carbs as finally logged, the model's first estimate, and what her glucose did afterwards.
        - `/sugary/items/<YYYY-MM>.csv` — one row per food in those meals, with where its numbers came from: an estimate, a label, a barcode, or typed by her.

        A month with no meals has no file, and a meal is filed under the month its `eaten_at` shows. Every time carries its offset in the home's zone, the clock the glucose history is stamped on.

        ### Finding things

        - One of her foods by name: `domain__filesystem__text_search` for the name, with `path` set to `/sugary/foods.csv`.
        - When she ate something: `domain__filesystem__text_search` for the name, with `directoryPath` set to `/sugary/items`; each hit carries its `meal_id` and `eaten_at`.
        - Which months she logged: `domain__filesystem__glob` for `*.csv` with `basePath` set to `/sugary/meals`.
        - A month whole: `domain__filesystem__file_read` of its file.
        """;

    public static SkillText Text { get; } = new(Name, Description, Body);

    // The trigger claim: a request of this kind loads the skill.
    public static readonly PromptClaim LoadsForAQuestionAboutHerLog =
        new("sugary-log.loads-for-a-question-about-her-log",
            "A question about the foods she keeps or the meals she logged loads the sugary-log skill before /sugary is read.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [LoadsForAQuestionAboutHerLog];
}