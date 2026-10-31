namespace Domain.Prompts;

// The doing rules of the vault, loaded when a request writes to it. What the vault is, that it
// cannot execute and that work needing exec is transferred once stay in the vault prompt, because
// the model decides where a task runs before it loads anything; everything it needs while writing
// a note is here (docs/adr/0039).
// Its words and claims are Skills/obsidian-vault.md; this is the handle the server and the scenarios hold.
public static class ObsidianVaultSkill
{
    public const string Name = "obsidian-vault";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAVaultWrite =
        _document.Claim("obsidian-vault.loads-for-a-vault-write");

    public static readonly PromptClaim ConfigurationIsOffLimits =
        _document.Claim("obsidian-vault.configuration-is-off-limits");

    public static readonly PromptClaim MarkdownIsTheNoteFormat =
        _document.Claim("obsidian-vault.markdown-is-the-note-format");

    public static readonly PromptClaim RenameUpdatesIncomingLinks =
        _document.Claim("obsidian-vault.rename-updates-incoming-links");

    public static readonly PromptClaim FrontmatterKeepsItsOtherKeys =
        _document.Claim("obsidian-vault.frontmatter-keeps-its-other-keys");

    public static readonly PromptClaim WikilinksAreNeverFixed =
        _document.Claim("obsidian-vault.wikilinks-are-never-fixed");

    public static readonly PromptClaim EmbedsBlockIdsAndCalloutsSurvive =
        _document.Claim("obsidian-vault.embeds-block-ids-and-callouts-survive");

    public static readonly PromptClaim TemplatesAreNotExpanded =
        _document.Claim("obsidian-vault.templates-are-not-expanded");

    public static readonly PromptClaim NewNoteFitsTheTree =
        _document.Claim("obsidian-vault.new-note-fits-the-tree");

    public static readonly PromptClaim NoNewTopLevelFolder =
        _document.Claim("obsidian-vault.no-new-top-level-folder");

    public static readonly PromptClaim EditsAreSurgical =
        _document.Claim("obsidian-vault.edits-are-surgical");

    public static readonly PromptClaim HeadingsAreReferenceable =
        _document.Claim("obsidian-vault.headings-are-referenceable");

    public static readonly PromptClaim DailyNotesAreAppendedTo =
        _document.Claim("obsidian-vault.daily-notes-are-appended-to");

    public static readonly PromptClaim AttachmentsStayInTheirFolder =
        _document.Claim("obsidian-vault.attachments-stay-in-their-folder");

    public static readonly PromptClaim IrreversibleChangeIsAskedAbout =
        _document.Claim("obsidian-vault.irreversible-change-is-asked-about");

    public static readonly PromptClaim WritesAreTextOnly =
        _document.Claim("obsidian-vault.writes-are-text-only");
}