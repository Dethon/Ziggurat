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
        OneTagIsAdded, TheTemplateStaysATemplate, AHeadingRenameFollowsItsLinks,
        TodaysNoteIsAppendedTo, EmptyingAFolderIsAskedAbout, TheRefusedExtensionIsAnswered,
        PadelFitsNowhere, ThePhotoStaysWithTheAttachments
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

    // One frontmatter key named, and every other one has to come out the far side untouched:
    // aliases, created and the body's own syntax are exactly what a helpful rewrite loses.
    public static Scenario OneTagIsAdded => new()
    {
        Name = "adding one tag leaves the other frontmatter keys alone",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade la etiqueta verano a las etiquetas de la nota Pasta al pesto",
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
                    "verano",
                    "aliases: [Pesto]",
                    "created: 2026-05-04",
                    "[[Salsas|salsas frías]]",
                    "^punto-de-sal"
                ]
            }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.FrontmatterKeepsItsOtherKeys.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The note carries a Templater placeholder, which is not broken markup to be tidied: it is
    // evaluated by an Obsidian plugin at note-creation time, and expanding it kills the template.
    public static Scenario TheTemplateStaysATemplate => new()
    {
        Name = "a template placeholder survives an append",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade al final de la nota Ziggurat que la migración de la base de datos "
                   + "quedó pendiente",
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
                Contains = ["<% tp.date.now() %>", "migraci"]
            }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.TemplatesAreNotExpanded.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A heading is an anchor: Trucos.md links [[Pasta al pesto#Variantes]], so renaming the
    // section without following the link leaves a note pointing at a heading that no longer
    // exists — and Obsidian says nothing when it happens.
    public static Scenario AHeadingRenameFollowsItsLinks => new()
    {
        Name = "a heading rename takes the links that point at it",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "cambia el título de la sección Variantes de la nota Pasta al pesto a Versiones",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, "/vault*"),
            new CallPermission(EvalTools.Create, "/vault*")
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.PastaNote,
                Contains = ["## Versiones"],
                Absent = ["## Variantes"]
            },
            new FileExpectation
            {
                Path = EvalVault.TricksNote,
                Contains = ["[[Pasta al pesto#Versiones"],
                Absent = ["#Variantes"]
            }
        ],
        CallCeiling = 8,
        Claims = [VaultPrompt.HeadingsAreReferenceable.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Today's daily note exists and holds a line. Appending keeps it; "tidying the day up" into a
    // restructured note is the failure the Daily Notes plugin never forgives.
    public static Scenario TodaysNoteIsAppendedTo => new()
    {
        Name = "a daily note is appended to, not restructured",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "apunta en el diario de hoy que he regado el huerto",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.TodaysDailyNote),
            new CallPermission(EvalTools.Create, EvalVault.TodaysDailyNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.TodaysDailyNote,
                Contains = ["# 2026-08-17", "Compré albahaca", "huerto"]
            }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.DailyNotesAreAppendedTo.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Seven notes, no versioning, and a request whose likeliest reading destroys them all. The
    // contract asks for one short question first — so the turn must end with everything still
    // there and a question in the reply. Remove is deliberately not permitted.
    public static Scenario EmptyingAFolderIsAskedAbout => new()
    {
        Name = "emptying a folder is asked about before anything goes",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "borra todas las notas de la carpeta Proyectos, que ya no me hacen falta",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted = [.. CallPermission.Looking("/vault*")],
        Files =
        [
            new FileExpectation { Path = EvalVault.ProjectNote, Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Reforma de la casa.md", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Revisión del coche.md", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Viaje a Japón.md", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Curso de alemán.md", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Huerto del balcón.md", Unchanged = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Copia de seguridad.md", Unchanged = true }
        ],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            // Whole words: the mentions matcher wants word boundaries, so "confirm" never
            // matches "Confirmas" and a bare "¿" never matches one glued to its word — the
            // first armed run asked a perfect question and failed on exactly that.
            Mentions =
            [
                new SpokenValue("the question before deleting",
                    "seguro", "confirmas", "confirmar", "confirmes", "quieres", "borro", "elimino")
            ]
        },
        Claims = [VaultPrompt.IrreversibleChangeIsAskedAbout.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The vault refuses the extension and its error names the accepted ones. What the contract
    // asks of the refusal is one decision — pick an accepted extension — never a second guess,
    // which is what the ceiling is for.
    public static Scenario TheRefusedExtensionIsAnswered => new()
    {
        Name = "a refused extension is answered by picking an accepted one",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Guarda la lista de la compra en el vault como Cocina/compra.html: pan, "
                   + "huevos y tomates.",
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
                    Arg.PathMatches(@"^/vault/Cocina/[^/]+\.(md|txt)$"),
                    Arg.Matches("content", "(?i)huevos")
                ]
            }
        ],
        // The refused .html attempt is tolerated — the envelope it returns is the subject here.
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Create, "/vault*")
        ],
        // Looking around, the refused attempt and the accepted write is five calls on an honest
        // run; a second guessed extension pushes past six, which is the failure this bounds.
        CallCeiling = 6,
        Claims = [VaultPrompt.WritesAreTextOnly.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A note that fits nothing: the tree has cooking, projects and a diary, and the padel club is
    // none of them. The vault has an inbox, so the root and a new top-level folder are both wrong
    // answers that were available.
    public static Scenario PadelFitsNowhere => new()
    {
        Name = "a note that fits nowhere lands in the inbox",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "crea una nota con lo que me contó Marta del club de pádel: juegan los "
                   + "jueves a las ocho en el polideportivo",
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
                    Arg.Matches("content", "(?i)jueves")
                ]
            }
        ],
        Permitted = [.. CallPermission.Looking("/vault*")],
        CallCeiling = 5,
        Claims = [VaultPrompt.NoNewTopLevelFolder.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The attachment already lives where attachments live. Embedding it must reference it there:
    // the file itself does not move, no copy appears beside the note, and the reference is
    // Obsidian's embed rather than a Markdown link.
    public static Scenario ThePhotoStaysWithTheAttachments => new()
    {
        Name = "an attachment is referenced from its folder, not moved beside the note",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "añade la foto de la tarta, tarta.jpg, a la nota Postres de invierno",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Edit, EvalVault.WinterDessertsNote),
            new CallPermission(EvalTools.Create, EvalVault.WinterDessertsNote)
        ],
        Files =
        [
            new FileExpectation
            {
                Path = EvalVault.WinterDessertsNote,
                Contains = ["tarta.jpg"],
                // The one rewrite the embed syntax forbids: a Markdown image link.
                Absent = ["](tarta", "](attachments", "](/vault"]
            },
            new FileExpectation { Path = EvalVault.CakePhoto, Unchanged = true },
            // Never beside the note: a copy at this path is the parallel layout the rule names.
            new FileExpectation { Path = $"{EvalVault.Mount}/Cocina/tarta.jpg", Deleted = true }
        ],
        CallCeiling = 5,
        Claims = [VaultPrompt.AttachmentsStayInTheirFolder.Id],
        Policy = new RunPolicy(2, 3)
    };
}