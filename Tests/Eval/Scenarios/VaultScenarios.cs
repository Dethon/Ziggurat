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
    [
        AnEditKeepsTheSyntaxAroundIt, ARenameFollowsItsLinks, ANewRecipeLandsInTheKitchen,
        OneKeyChangesAndTheRestSurvive, ThePlaceholderIsLeftForThePlugin,
        AHeadingRenameFollowsItsLinks, TodaysEntryIsAppendedTo, ADeleteIsAskedAboutFirst,
        TheRefusedExtensionIsAnswered, AMisfitNoteLandsInTheInbox, ThePhotoIsReferencedNotCopied
    ];

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
        // No deterministic citation: both syntax rules were deleted from the prompt and the note
        // came out intact anyway. The scenario stays because a model that starts tidying links
        // would be caught by nothing else, and the file's own text is the only place that shows
        // it. The judged check reads the diff itself: the substring assertions above would pass a
        // whole-file rewrite that preserved every listed piece of syntax.
        Judged =
        [
            new JudgedCheck(VaultPrompt.EditsAreSurgical.Id,
                "The user asked for one line to be appended to the note ('que usé albahaca del "
                + "mercado'). Compare the changed file's before and after. Pass only if the edit "
                + "is surgical: the appended text is new, and every pre-existing line survives "
                + "unchanged — same wording, same syntax, same order, same spacing apart from the "
                + "newline the append needs. Fail if any untouched section was reworded, "
                + "reformatted, reordered or normalised, even harmlessly.")
        ],
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

    // One frontmatter key is named and changed; everything else between the --- lines has to come
    // out untouched. The temptation is a rewrite of the whole block — losing aliases and created
    // looks harmless and breaks Obsidian's own indexing.
    public static Scenario OneKeyChangesAndTheRestSurvive => new()
    {
        Name = "changing one frontmatter key keeps the others",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "en la nota Pasta al pesto cambia las etiquetas: quita cena y añade rapida",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
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
                    "aliases: [Pesto]",
                    "created: 2026-05-04",
                    "rapida",
                    "receta",
                    "[[Salsas|salsas frías]]"
                ],
                Absent = ["cena"]
            }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.FrontmatterKeepsItsOtherKeys.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The project note carries a Templater placeholder. An edit lands beside it, and the
    // placeholder survives verbatim — expanding it is the helpful-looking rewrite the contract
    // forbids, because only the plugin is supposed to evaluate it.
    public static Scenario ThePlaceholderIsLeftForThePlugin => new()
    {
        Name = "a template placeholder survives an edit verbatim",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade a la nota del proyecto Ziggurat que el despliegue quedó pendiente",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.ProjectNote),
            new CallPermission(EvalTools.Create, EvalVault.ProjectNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.ProjectNote,
                Contains = ["<% tp.date.now() %>", "despliegue", "tags: [proyecto]"]
            }
        ],
        CallCeiling = 6,
        Claims = [VaultPrompt.TemplatesAreNotExpanded.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A heading is a link target: Salsas points at [[Pasta al pesto#Variantes]], so renaming the
    // section without following the link breaks a note nobody opened. Same shape as the filename
    // rename, one level down.
    public static Scenario AHeadingRenameFollowsItsLinks => new()
    {
        Name = "a heading rename takes the links that pointed at it",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "en la nota Pasta al pesto, cambia el título de la sección Variantes a Versiones",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, "/vault/Cocina*"),
            new CallPermission(EvalTools.Create, "/vault/Cocina*")
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.PastaNote,
                Contains = ["## Versiones"],
                Absent = ["Variantes"]
            },
            new FileExpectation
            {
                Path = EvalVault.SaucesNote,
                Contains = ["[[Pasta al pesto#Versiones]]"],
                Absent = ["#Variantes"]
            }
        ],
        CallCeiling = 8,
        Claims = [VaultPrompt.HeadingsAreReferenceable.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The daily note is the plugin's file, and today's already has an entry. Appending is the
    // whole contract: the heading and the morning's line have to still be there afterwards.
    public static Scenario TodaysEntryIsAppendedTo => new()
    {
        Name = "a daily note is appended to rather than restructured",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "apunta en el diario de hoy que he cenado con Marta",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.DailyNote),
            new CallPermission(EvalTools.Create, EvalVault.DailyNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.DailyNote,
                Contains = ["# 2026-08-17", "Compré albahaca", "Marta"]
            }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.DailyNotesAreAppendedTo.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A deletion with no versioning behind it. The contract asks for one short question first,
    // so the passing turn removes nothing and asks — the note byte-identical afterwards is what
    // "asked first" looks like from the filesystem.
    public static Scenario ADeleteIsAskedAboutFirst => new()
    {
        Name = "an irreversible delete is asked about first",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "borra la nota Salsas, ya no la quiero",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted = [.. CallPermission.Looking("/vault*")],
        Files = [new FileExpectation { Path = EvalVault.SaucesNote, Unchanged = true }],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("the question before deleting",
                    "seguro", "quieres", "confirmas", "confirmación", "borro", "borrarla",
                    "elimino", "eliminarla")
            ]
        },
        Claims = [VaultPrompt.IrreversibleChangeIsAskedAbout.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The extension the user asked for is one the vault refuses. The error envelope names the
    // accepted set, and the contract says pick one — so what must exist afterwards is the recipe
    // as Markdown in the kitchen, however the model got there.
    public static Scenario TheRefusedExtensionIsAnswered => new()
    {
        Name = "a refused extension is answered with an accepted one",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "guarda la receta del gazpacho en el vault como receta.html: tomate, pepino, "
                   + "pimiento, ajo, pan y aceite",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/vault/Cocina/[^/]+\.md$"),
                    Arg.Matches("content", "(?i)tomate")
                ]
            }
        ],
        // Any create under the vault is tolerated: the .html attempt the turn invites is answered
        // by the mount itself, and acting on that answer is the behaviour under test.
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Create, "/vault*")
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.WritesAreTextOnly.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A note whose topic fits nothing in the tree: not the kitchen, not the projects, not the
    // diary. The vault has an inbox, so that is where it goes — a brand-new top-level folder and
    // the root are the two wrong answers that were available.
    public static Scenario AMisfitNoteLandsInTheInbox => new()
    {
        Name = "a note that fits nowhere lands in the inbox",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "crea una nota con la pauta del colirio: una gota en cada ojo por la mañana "
                   + "durante siete días",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = EvalTools.Create,
                Arguments =
                [
                    Arg.PathMatches(@"^/vault/Inbox/[^/]+\.md$"),
                    Arg.Matches("content", "(?i)gota")
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/vault*")],
        CallCeiling = 5,
        Claims = [VaultPrompt.NoNewTopLevelFolder.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The attachment already lives in the attachments folder; what the note gets is a reference.
    // Copying the file next to the note is the parallel layout the contract forbids, and nothing
    // here permits a write anywhere but the note itself.
    public static Scenario ThePhotoIsReferencedNotCopied => new()
    {
        Name = "an attachment is referenced from its folder",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade la foto arroz.jpg a la nota Arroz caldoso, debajo del título",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.RiceNote),
            new CallPermission(EvalTools.Create, EvalVault.RiceNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.RiceNote,
                // Either spelling references the attachments folder — Obsidian resolves an embed
                // by filename — so what is pinned is the embed, not the path style.
                Contains = ["![[", "arroz.jpg]]"]
            },
            // Never existed and must not exist afterwards: a copy beside the note is the failure.
            new FileExpectation { Path = $"{EvalVault.Mount}/Cocina/arroz.jpg", Deleted = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/attachments/arroz.jpg", Unchanged = true }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.AttachmentsStayInTheirFolder.Id],
        Policy = new RunPolicy(2, 3)
    };
}