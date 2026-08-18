using Domain.Prompts;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// The user's own notes, edited without being quietly broken. What separates this family from the
// rest is that the tool call is almost never the failure: writing the sentence the user asked for
// and turning a wikilink into Markdown on the way past is one call, and it looks fine in the
// recording. The assertion is the note's own text once the turn is over.
public static class VaultScenarios
{
    public static IReadOnlyList<Scenario> All =>
        [AnEditKeepsTheSyntaxAroundIt, ARenameFollowsItsLinks, ANewRecipeLandsInTheKitchen];

    // An edit to a note carrying every piece of syntax the contract promises to preserve:
    // frontmatter, a piped wikilink, a callout, a block id, an embed and an inline tag.
    public static Scenario AnEditKeepsTheSyntaxAroundIt => new()
    {
        Name = "an edit keeps the syntax around it",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade al final de la nota Pasta al pesto que usé albahaca del mercado",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/vault*"),
            new CallPermission(EvalTools.Read, "/vault*"),
            new CallPermission(EvalTools.Info, "/vault*"),
            new CallPermission(EvalTools.Search, "/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.PastaNote),
            new CallPermission(EvalTools.Create, EvalVault.PastaNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.PastaNote,
                Contains =
                [
                    "tags: [receta, cena]",
                    "aliases: [Pesto]",
                    "[[Salsas|salsas frías]]",
                    "> [!tip] Cocción",
                    "^punto-de-sal",
                    "![[pesto.png]]",
                    "#cocina-rapida",
                    "mercado"
                ],
                // The one rewrite this family exists to catch: the link the user never mentioned,
                // "fixed" into Markdown by a model being helpful.
                Absent = ["](Salsas.md)", "](Salsas)"]
            },
            new FileExpectation { Path = EvalVault.SaucesNote, Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/.obsidian/app.json", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/.obsidian/workspace.json", Unchanged = true }
        ],
        CallCeiling = 6,
        // No citation: both syntax rules were deleted from the prompt and the note came out
        // intact anyway. The scenario stays because a model that starts tidying links would be
        // caught by nothing else, and the file's own text is the only place that shows it.
        Policy = new RunPolicy(2, 3),
        Tier = EvalTier.Smoke
    };

    // A rename, which in a vault is never just a rename: the filename is what every incoming
    // wikilink references, so a rename that stops there breaks the notes that pointed at it.
    public static Scenario ARenameFollowsItsLinks => new()
    {
        Name = "a rename takes the links that pointed at it",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "renombra la nota Salsas a Salsas base",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/vault*"),
            new CallPermission(EvalTools.Read, "/vault*"),
            new CallPermission(EvalTools.Info, "/vault*"),
            new CallPermission(EvalTools.Search, "/vault*"),
            new CallPermission(EvalTools.Move, "/vault*"),
            new CallPermission(EvalTools.Edit, "/vault*"),
            new CallPermission(EvalTools.Create, "/vault*"),
            new CallPermission(EvalTools.Remove, "/vault*")
        ],
        Files =
        [
            new FileExpectation
            {
                Path = $"{EvalVault.Mount}/Cocina/Salsas base.md",
                Contains = ["[[Pasta al pesto]]"]
            },
            // A copy that left the original behind would update both links and still leave the
            // user with two notes where they had one.
            new FileExpectation { Path = EvalVault.SaucesNote, Deleted = true },
            new FileExpectation
            {
                Path = EvalVault.PastaNote,
                Contains = ["[[Salsas base"],
                Absent = ["[[Salsas|", "[[Salsas]]"]
            }
        ],
        CallCeiling = 8,
        Claims = [VaultPrompt.RenameUpdatesIncomingLinks.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A new note with an obvious home. The vault has a Cocina folder holding two recipes, so the
    // root and a newly invented folder are both wrong answers that were available.
    public static Scenario ANewRecipeLandsInTheKitchen => new()
    {
        Name = "a new recipe lands in the folder the recipes are in",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "crea una nota con la receta de la tortilla de patatas: patatas, huevos y cebolla",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments = [Arg.PathMatches(@"^/vault/Cocina/[^/]+\.md$")]
            }
        ],
        Permitted = [.. CallPermission.Looking("/vault*")],
        CallCeiling = 6,
        // No citation: with the fit-into-the-tree bullet deleted the recipe still landed in
        // Cocina. A folder called Cocina holding two recipes is its own instruction.
        Policy = new RunPolicy(2, 3)
    };
}