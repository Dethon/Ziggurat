namespace Domain.Prompts;

// How a built app is made, checked, published and mended, loaded when a request touches /apps. The apps
// server serves no prompt of its own: the mount's description in the mounts list says what /apps is,
// and the skill's advertisement is the whole choosing rule, so everything moved behind the load
// (docs/adr/0039). Served by the apps server, which the household's `apps` feature withholds, so only an
// agent offered /apps is offered it.
// Its words and claims are Skills/apps.md; this is the handle the server and the scenarios hold.
public static class AppsSkill
{
    public const string Name = "apps";

    private static readonly PromptDocument _document = PromptDocuments.Skill(Name);

    // The whole trigger: the one line about this skill that is in every turn.
    public static readonly string Description = _document.AsSkill().Description;

    public static readonly string Body = _document.Text;

    public static readonly SkillText Text = new(Name, Description, Body);

    public static readonly PromptClaim LoadsForAnAppRequest =
        _document.Claim("apps.loads-for-an-app-request");
}