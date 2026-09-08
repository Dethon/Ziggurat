namespace Domain.Prompts;

public static class VaultPrompt
{
    public const string Name = "vault_prompt";

    public const string Prompt = """
        ## Vault Filesystem (Obsidian)

        The `/vault` mount is a personal **Obsidian** vault — a directory of plain-text notes that the user opens in the Obsidian desktop/mobile app. You and the user are editing the same files; treat the vault as the user's working notebook, not as scratch space.

        ### Layout

        - The vault is a normal directory tree of files. Folders represent topics or projects; nesting is meaningful and reflects how the user has chosen to organise things. Don't reshape the tree without being asked.
        - `.obsidian/` (and any other dotfile/dotdir at the root) is **Obsidian's own configuration** — workspace, plugins, themes, hotkeys, graph settings. Treat it as off-limits unless the user explicitly asks you to change a setting there. Never list its contents in summaries; never edit it incidentally.
        - There is usually a folder dedicated to **attachments** (images, PDFs, audio, etc.) — its name varies per vault (`attachments/`, `assets/`, `_media/`, `Files/`…). Don't rename or move it; binaries belong there, not next to the note that references them.

        ### File conventions

        - The primary note format is **Markdown (`.md`)**. Other text formats (`.txt`, `.json`, `.yaml`, `.toml`, `.ini`, `.conf`, `.cfg`) are accepted by the backend but Obsidian only renders Markdown — prefer `.md` for anything the user will browse as a note.
        - **Filenames are identifiers.** Wikilinks (see below) reference notes by filename (without the extension), so renaming a note silently breaks every link to it. If the user asks for a rename, search for `[[OldName]]` / `[[OldName|...]]` references first and update them in the same change.
        - **YAML frontmatter** at the very top of a Markdown file (between two `---` lines) holds metadata: `tags`, `aliases`, `created`, `cssclass`, etc. Keep it intact; only modify keys the user mentioned. If you add frontmatter to a file that has none, place it on line 1 with no blank line above.

        ### Obsidian-specific syntax to preserve

        - **Wikilinks:** `[[Note Name]]`, `[[Note Name|display text]]`, `[[Note Name#Heading]]`, `[[Note Name#^block-id]]`. These are not standard Markdown — never "fix" them into `[text](url)` form.
        - **Embeds:** `![[Note Name]]` embeds another note inline; `![[image.png]]` embeds an attachment. Same syntax rules as wikilinks.
        - **Block references:** `^block-id` at the end of a line (or paragraph) is a stable anchor that other notes can link to. Don't strip them.
        - **Tags:** inline `#tag` and frontmatter `tags:` entries are both indexed by Obsidian. Keep the form the user uses.
        - **Callouts:** `> [!note]`, `> [!warning]`, etc. — preserve the `[!type]` marker on the first line of the blockquote.
        - **Templates / Templater syntax:** `<% … %>` and `{{ … }}` placeholders live in template files; don't expand them — they are evaluated by an Obsidian plugin at note creation time.

        ### Placing new notes

        - **Survey before you create.** Before creating the first note in a vault session, glob the vault root for top-level folders — use a trailing slash (`*/`) to list directories only, or `**/` to include sub-folders. Cache that mental map for the rest of the turn — don't re-glob for every note.
        - **Fit into the existing tree.** Pick the deepest existing folder whose topic matches the note. A note about a recipe goes under the user's existing `Cooking/` (or `Recipes/`, or whatever they call it), not at the root. Match the user's naming style (casing, spaces vs. hyphens, language) when picking a filename.
        - **Don't dump at the root.** The vault root is reserved for the user's own top-level notes and folder structure. Only place a note there if it genuinely belongs at the top level (e.g. an index/MOC), if the vault has no folder structure at all, or if nothing in the tree fits (next bullet).
        - **Don't invent new top-level folders.** If nothing fits, use the vault's existing inbox folder; only if there is none, place it at the root — the one exception to the rule above. Don't ask the user where to put it.
        - **Attachments still go in the attachments folder**, not next to the note that references them — see the Layout section.

        ### Editing rules

        - **Read before you edit.** Read the file first to see its structure (frontmatter, headings, callouts, links). Reading is preparation, not output — never recite or summarise what you read unless asked.
        - **Prefer surgical edits over whole-file rewrites.** Wikilinks, block ids, and frontmatter make whole-file rewrites high-risk.
        - **Headings are referenceable.** Other notes may link to `[[ThisNote#Some Heading]]`. Renaming a heading is a rename like any other: before touching the heading, `text_search` the vault for `#<old heading>` and update every incoming link in the same turn, exactly as you would for a filename — the heading edit alone is half the job, and a reply that reports it done with the links still pointing at the old name is wrong.
        - **Attachments stay where they are.** When inserting an image/audio/pdf reference, use the path Obsidian already uses for that vault's attachment folder; don't introduce a parallel layout.
        - **Daily notes** (commonly `Daily/YYYY-MM-DD.md` or similar) are managed by the Daily Notes core plugin. Append to them rather than restructuring them.
        - **Ask before an irreversible change.** These are the user's own notes and there is no versioning here. Before a change that deletes or overwrites work you cannot restore, ask one short question first.

        ### Capabilities & limits

        - The vault supports the standard filesystem operations except command execution. If you need to run a script over vault content, use the sandbox: the `copy` (or `move`) tool transfers files and directories across mounts in a single call — the two filesystems don't share storage, but you don't need to hand-roll a read/write loop to bridge them.
        - Writes are restricted to a configured set of text extensions; attempts outside that set return an error envelope. The error names the accepted extensions — finish the write under the closest accepted one and say so in your reply; never retry another refused extension, and never bounce the choice back to the user. That refusal is not the end of the turn: the user asked for the content saved, not for a particular extension, so a turn that ends with the note unwritten has failed the request even if the reply explains why. Write the file, then say which extension you used.
        - The vault is a host-mounted directory: changes are immediately visible in the user's Obsidian app, and any edit the user makes there is immediately visible to you. Assume the user may be editing concurrently — re-read a file if a non-trivial amount of time has passed since you last looked.
        - There is no built-in versioning. Users typically keep their vault under git or use Obsidian Sync; either way, treat each edit as final from your side.
        """;
    // Every falsifiable statement the prose above makes. Most of them are about what an edit must
    // leave alone, which is exactly the kind of rule that is never noticed until a note is quietly
    // broken — declaring them is what makes the untested ones visible.
    public static readonly PromptClaim ConfigurationIsOffLimits =
        new("vault.configuration-is-off-limits",
            "Obsidian's own configuration directory is never edited incidentally and never listed in a summary.");

    public static readonly PromptClaim MarkdownIsTheNoteFormat =
        new("vault.markdown-is-the-note-format",
            "A note the user will browse is written as Markdown rather than in another accepted text format.");

    public static readonly PromptClaim RenameUpdatesIncomingLinks =
        new("vault.rename-updates-incoming-links",
            "A renamed note has every incoming wikilink updated in the same turn, because a filename is what links reference.");

    public static readonly PromptClaim FrontmatterKeepsItsOtherKeys =
        new("vault.frontmatter-keeps-its-other-keys",
            "Frontmatter survives an edit with only the keys the user mentioned changed.");

    public static readonly PromptClaim WikilinksAreNeverFixed =
        new("vault.wikilinks-are-never-fixed",
            "A wikilink is never rewritten into Markdown link syntax.");

    public static readonly PromptClaim EmbedsBlockIdsAndCalloutsSurvive =
        new("vault.embeds-block-ids-and-callouts-survive",
            "Embeds, block ids, tags and callout markers survive an edit to the note that carries them.");

    public static readonly PromptClaim TemplatesAreNotExpanded =
        new("vault.templates-are-not-expanded",
            "Template placeholders are left for the plugin that evaluates them rather than expanded.");

    // Two sentences in the prose deliberately declare no claim: survey-the-tree-first and
    // read-before-editing describe means, not outcomes. Surveying is tolerated everywhere and
    // required nowhere — a model that already knows where the note goes has broken nothing, and
    // requiring the glob would test the habit rather than the outcome, which is what
    // vault.new-note-fits-the-tree and the edit scenarios' file assertions already witness.

    public static readonly PromptClaim NewNoteFitsTheTree =
        new("vault.new-note-fits-the-tree",
            "A new note lands in the deepest existing folder whose topic matches it.");

    public static readonly PromptClaim NoNewTopLevelFolder =
        new("vault.no-new-top-level-folder",
            "A new top-level folder is never invented; the existing inbox, or the root, takes a note that fits nowhere.");

    public static readonly PromptClaim EditsAreSurgical =
        new("vault.edits-are-surgical",
            "An edit changes the part it was asked to change rather than rewriting the whole file.");

    public static readonly PromptClaim HeadingsAreReferenceable =
        new("vault.headings-are-referenceable",
            "A renamed heading has every incoming heading link updated in the same turn.");

    public static readonly PromptClaim DailyNotesAreAppendedTo =
        new("vault.daily-notes-are-appended-to",
            "A daily note is appended to rather than restructured.");

    public static readonly PromptClaim AttachmentsStayInTheirFolder =
        new("vault.attachments-stay-in-their-folder",
            "An attachment is referenced from the vault's attachments folder rather than placed beside the note.");

    public static readonly PromptClaim IrreversibleChangeIsAskedAbout =
        new("vault.irreversible-change-is-asked-about",
            "A change that deletes or overwrites work with no versioning behind it is preceded by one short question.");

    public static readonly PromptClaim TransferIsOneCall =
        new("vault.transfer-is-one-call",
            "Moving vault content to a mount that can execute uses the single transfer call rather than a read-and-write loop.");

    public static readonly PromptClaim WritesAreTextOnly =
        new("vault.writes-are-text-only",
            "A write outside the configured text extensions is finished under an accepted one rather than retried or bounced back to the user.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        ConfigurationIsOffLimits,
        MarkdownIsTheNoteFormat,
        RenameUpdatesIncomingLinks,
        FrontmatterKeepsItsOtherKeys,
        WikilinksAreNeverFixed,
        EmbedsBlockIdsAndCalloutsSurvive,
        TemplatesAreNotExpanded,
        NewNoteFitsTheTree,
        NoNewTopLevelFolder,
        EditsAreSurgical,
        HeadingsAreReferenceable,
        DailyNotesAreAppendedTo,
        AttachmentsStayInTheirFolder,
        IrreversibleChangeIsAskedAbout,
        TransferIsOneCall,
        WritesAreTextOnly
    ];
}