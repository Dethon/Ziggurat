using Domain.DTOs;
using Domain.Security;
using Mcp.Hosting;

namespace McpServerSugary.Settings;

public record SugarySettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // The SQLite file, in the server's own volume. Its directory is created on first start.
    public string DatabasePath { get; init; } = "/data/sugary.db";

    // The zone every time in the tables is written in, as an IANA id. Empty means the container's
    // own zone, which compose sets from the same TZ anchor the Home Assistant container runs on —
    // the clock the /ha mount stamps glucose history in, so a meal lines up with the readings after
    // it. An id the container does not know fails startup rather than writing times on a wrong clock.
    public string HomeZone { get; init; } = "";

    // The app's own gate, apart from /mcp's: the browser routes are called by her phone, which never
    // holds the deployment secret.
    public SugaryAppSettings SugaryApp { get; init; } = new();

    // Where a barcode none of her saved foods carries is looked up.
    public string OpenFoodFactsUrl { get; init; } = "https://world.openfoodfacts.org/";

    // How long the app's request waits on the sugary agent's turn before she is told it took too long
    // and the turn is cancelled. The standalone app's estimates took up to about half a minute.
    public int TurnTimeoutSeconds { get; init; } = 120;

    // How long after the last estimate of a meal her answers may still continue its conversation. A
    // meal is answered at the table; a draft found again the next day is a meal long eaten, and the
    // app estimates it afresh instead.
    public int FollowUpHours { get; init; } = 24;

    // The agent the app's turns are answered by: agentDefaults.byChannel.sugary in the agent's own
    // settings, spelled again here because the message names no agent and routing is the agent
    // host's. Asked about only to refuse a photo its model cannot see; a test keeps the two equal.
    public string Agent { get; init; } = "sugary";

    // The model a nutrition label is read on, switched to for that turn alone by the turn's config
    // patch. A stronger one than meals use: a cheaper model read a protein row as carbs. The agent
    // honours a patch only for a model on its patchableModels list, so this must be one of them (a
    // test keeps it so), and a label is refused when the agent's catalogue says it is not.
    public string LabelModel { get; init; } = "anthropic/claude-opus-5.5";

    // Where the photos of a meal being estimated rest while the agent may still ask for them.
    public UploadSettings Uploads { get; init; } = new();

    // How a clip of a meal is made into what every model that watches video takes, and its stills.
    public ClipSettings Clips { get; init; } = new();

    // The deployment's retention policy, read from Domain's retention.json like every host's: the
    // upload store keeps a photo as long as an attachment is kept anywhere (ADR-0020).
    public RetentionSettings Retention { get; init; } = new();
}

public record UploadSettings
{
    // Inside the server's own volume, beside the database but never in it.
    public string StoragePath { get; init; } = "/data/uploads";

    // The most any model the sugary agent may run on takes in one image: Anthropic's API refuses an
    // image over 5 MB, and the label model and Haiku are among its patchable models. The app shrinks
    // a photo to 1568 px before sending, which lands far below this; the standalone app allowed 8 MB.
    public long MaxPhotoBytes { get; init; } = 5L * 1024 * 1024;

    // The standalone app's limit, kept: more angles of one plate stop adding anything.
    public int MaxPhotos { get; init; } = 4;

    // The most a clip may weigh as the phone recorded it, before it is made small. Twenty seconds of
    // 1080p is 20–50 MB on a phone and of 4K at 60 frames up to about 150 MB; past this the clip is
    // refused before it is read, and the request is allowed to be this large and no larger.
    public long MaxClipBytes { get; init; } = 200L * 1024 * 1024;
}

public record ClipSettings
{
    // The tools a clip is read and made with, on the PATH of the server's image.
    public string Ffmpeg { get; init; } = "ffmpeg";

    public string Ffprobe { get; init; } = "ffprobe";

    // The longest clip sent. A longer one is refused rather than cut, so she knows the end of it
    // was not seen; a few frames over, the way a phone stops, are cut instead.
    public double MaxSeconds { get; init; } = 20;

    // The short side of the clip as sent: what the standalone benchmark's clips had, and plenty for
    // a model that samples about a frame a second.
    public int MaxLines { get; init; } = 540;

    // How many stills a model that only sees pictures is sent instead of the clip, at most.
    public int Stills { get; init; } = 8;

    // A still's longest edge: the size the app shrinks a photo to before sending it.
    public int StillEdge { get; init; } = 1568;

    // How long reading and making one clip may take before the meal is refused: the whole clip,
    // every run of the tools together, not each run.
    public int TimeoutSeconds { get; init; } = 120;

    // How many clips are read at once. Each run of ffmpeg takes a core for seconds, so a few meals
    // filmed together wait their turn rather than all running at the same time.
    public int MaxConcurrent { get; init; } = 2;
}

public record SugaryAppSettings
{
    // The code typed once per phone to get in (SUGARYAPP__ACCESSCODE, a deployment secret). Unset
    // refuses every sign-in, as an unset shared secret refuses every call.
    public string AccessCode { get; init; } = "";

    // False only where the deployment itself keeps strangers out (a VPN): every app route opens with
    // no sign-in and the session tells the app not to ask. /mcp keeps the deployment secret either way.
    public bool RequireAccessCode { get; init; } = true;
}