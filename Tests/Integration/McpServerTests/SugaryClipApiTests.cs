using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using McpServerSugary.Settings;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A meal filmed around the plate. The app posts the clip as the phone recorded it; the server makes
// it the one shape every model that watches video takes (H.264 in MP4, at most 540 lines, no sound,
// at most 20 seconds), takes about eight sharp stills from it for a model that only sees pictures,
// and sends the clip naming its stills on the turn. The agent is played over MCP by its own channel
// connection, and every file is read back through the fetch hydration makes, so what is asserted is
// what would reach a model.
public class SugaryClipApiTests
{
    // A turn is sent once its clip is read, which the server allows its own bound for: a busy machine's
    // ffmpeg is still inside it when a shorter wait here gives up. The wait only ever bounds a failure.
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(new ClipSettings().TimeoutSeconds) + TimeSpan.FromSeconds(30);

    internal const string Estimate =
        """
        {"items":[{"name":"paella","portion":"a plate","gramsLow":250,"grams":300,"gramsHigh":350,"carbsPer100g":20,"confidence":"medium","note":null}],
         "questions":[],"notes":[]}
        """;

    // Whatever the phone recorded. A portrait clip keeps its shape, with its short side at 540, and
    // one stored sideways with a flag to turn it arrives turned.
    public static TheoryData<string, int, int> Recorded => new()
    {
        { "HEVC in a QuickTime file, portrait", 540, 960 },
        { "VP9 and Opus in WebM", 960, 540 },
        { "a browser recorder's WebM with no stated length", 640, 360 },
        { "stored sideways with a rotation flag", 540, 960 }
    };

    // Refused before anything is kept or sent — there is no agent even connected — with a reason
    // she can act on.
    [SkippableFact]
    public async Task AClipLongerThanTwentySeconds_IsRefusedWithItsReason_BeforeAnythingIsKeptOrSent()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("paella", clip: await TestClips.Long(25)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldBe(
            "The clip is 25 seconds long. Film at most 20 seconds around the plate, or take a photo instead.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // A browser's recorder states no length, so the clip's length is read off its last frame.
    [SkippableFact]
    public async Task ARecordersClipWithNoStatedLength_IsStillRefusedPastTwentySeconds()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates",
            Form("", clip: await TestClips.Recorder(25), clipType: "video/webm", clipName: "clip.webm"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldStartWith("The clip is 25 seconds long.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // A phone stops a "20 second" clip a few frames late.
    [SkippableFact]
    public async Task AClipOfTwentySeconds_AndAFewFramesMore_IsSent_CutToTwenty()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", clip: await TestClips.Long(20.3)));
        var turn = await NextTurnAsync(agent);

        var clip = turn.Attachments.ShouldNotBeNull().Single(a => a.MediaType == "video/mp4");
        var probe = await TestClips.ProbeAsync((await FetchAsync(agent, clip)).ShouldNotBeNull());
        double.Parse(probe["format"]!["duration"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeLessThanOrEqualTo(20.05);

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string, Func<Task<MultipartFormDataContent>>, string> Refused => new()
    {
        { "not a video", () => Task.FromResult(Form("paella", clip: [1, 2, 3, 4, 5, 6, 7, 8])),
            "That clip could not be read. Film it again, or take a photo instead." },
        { "empty", () => Task.FromResult(Form("paella", clip: [])), "The clip arrived empty. Film it again." },
        { "two clips", async () => TwoClips(await TestClips.Silent()), "One clip per meal." }
    };

    [SkippableTheory]
    [MemberData(nameof(Refused))]
    public async Task AClipTheServerCannotUse_IsRefusedWithItsReason_BeforeAnythingIsKeptOrSent(
        string what, Func<Task<MultipartFormDataContent>> form, string reason)
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", await form());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, what);
        (await DetailAsync(response)).ShouldBe(reason, what);
        StoredFiles(server).ShouldBeEmpty(what);
    }

    [Fact]
    public async Task AClipOverTheSizeCeiling_IsRefusedBeforeItIsRead()
    {
        await using var server = await SugaryServer.StartAsync(configure: settings =>
            settings with { Uploads = settings.Uploads with { MaxClipBytes = 1024 * 1024 } });
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("", clip: new byte[2 * 1024 * 1024]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldBe("A clip must be under 1 MB. Film a shorter one.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // A phone's 20 seconds of 1080p is tens of megabytes, more than a web server takes in one
    // request unless told otherwise. A body that size reaches the clip's own rules, not a 413.
    [Fact]
    public async Task ARequestCarryingAPhonesClip_IsNotRefusedForItsSize()
    {
        await using var server = await SugaryServer.StartAsync();
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("", clip: new byte[40 * 1024 * 1024]));

        response.StatusCode.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldContain("could not be read");
    }

    // A model that takes neither video nor pictures would answer as if no clip had been sent. One
    // that sees pictures only is sent the stills, so the clip goes.
    [SkippableFact]
    public async Task AModelThatCanNeitherWatchNorSee_HasAFilmedMealRefused_AndAPictureModelGetsIt()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        AgentCatalogEntry[] registered = [Sugary("text/only", accepts: [])];
        await agent.RegisterAgentsAsync(registered, CancellationToken.None);
        server.HouseholdRuns(registered);
        var app = await server.SignedInAsync();

        var refused = await app.PostAsync("/api/estimates", Form("", clip: await TestClips.Silent()));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(refused)).ShouldBe(
            "text/only can neither watch a clip nor read photos, so this meal cannot be estimated from it. " +
            "Describe it in words instead, or count it by hand.");
        StoredFiles(server).ShouldBeEmpty();

        AgentCatalogEntry[] seesPictures = [Sugary("sees/pictures", accepts: [AttachmentKind.Image])];
        await agent.RegisterAgentsAsync(seesPictures, CancellationToken.None);
        server.HouseholdRuns(seesPictures);
        var answer = app.PostAsync("/api/estimates", Form("", clip: await TestClips.Silent()));
        var turn = await NextTurnAsync(agent);
        turn.Attachments.ShouldNotBeNull().ShouldContain(a => a.MediaType == "video/mp4");
        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // An answer continues an estimate whose clip the agent already holds.
    [SkippableFact]
    public async Task AnAnswerWithAClip_IsRefused_AndNothingIsKept()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        var first = app.PostAsync("/api/estimates", Form("", clip: await TestClips.Silent()));
        await ReplyAsync(agent, await NextTurnAsync(agent), Estimate);
        var conversation = (await (await first).Content.ReadFromJsonAsync<JsonNode>())!["conversationId"]!.GetValue<string>();
        var stored = StoredFiles(server);

        var form = Form("", clip: await TestClips.Silent());
        form.Add(new StringContent(conversation), "conversationId");
        var response = await app.PostAsync("/api/estimates", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldContain("already");
        StoredFiles(server).ShouldBe(stored);
    }

    // A server whose image lost its ffmpeg cannot read a clip, and says so rather than sending the
    // meal without it.
    [Fact]
    public async Task WithoutTheToolsToReadAClip_TheMealIsRefusedWithAReason_AndNothingIsSent()
    {
        await using var server = await SugaryServer.StartAsync(configure: settings =>
            settings with { Clips = settings.Clips with { Ffmpeg = "/nonexistent/ffmpeg", Ffprobe = "/nonexistent/ffprobe" } });
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("", clip: [1, 2, 3]));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await DetailAsync(response)).ShouldBe(
            "Clips cannot be read on this server right now. Take a photo instead, or describe the meal.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // A probe that answers nothing it can read says the clip is not one, as a probe that fails does:
    // it is the clip being refused, not the server failing.
    [SkippableTheory]
    [InlineData("nothing")]
    [InlineData("not json")]
    public async Task AClipWhoseProbeAnswersNothingReadable_IsRefusedAsUnreadable(string answer)
    {
        Skip.If(FakeTools.Missing is not null, FakeTools.Missing);
        using var tools = new FakeTools();
        var ffprobe = tools.Write("ffprobe", answer == "nothing" ? "exit 0" : "echo 'not json'; exit 0");
        await using var server = await SugaryServer.StartAsync(configure: settings =>
            settings with { Clips = settings.Clips with { Ffprobe = ffprobe } });
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("", clip: [1, 2, 3]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).ShouldBe("That clip could not be read. Film it again, or take a photo instead.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // Reading a clip is a dozen ffmpeg runs, each a whole core for seconds; a handful of meals filmed
    // at once wait their turn rather than all running together.
    [SkippableFact]
    public async Task ClipsAreReadAFewAtATime()
    {
        Skip.If(FakeTools.Missing is not null, FakeTools.Missing);
        using var tools = new FakeTools();
        var ffprobe = tools.Write("ffprobe",
            $"touch \"{tools.Directory}/started-$$\"; while [ ! -e \"{tools.Directory}/release\" ]; do sleep 0.05; done; exit 1");
        await using var server = await SugaryServer.StartAsync(configure: settings =>
            settings with { Clips = settings.Clips with { Ffprobe = ffprobe, MaxConcurrent = 1 } });
        var app = await server.SignedInAsync();

        var first = app.PostAsync("/api/estimates", Form("", clip: [1, 2, 3]));
        var second = app.PostAsync("/api/estimates", Form("", clip: [4, 5, 6]));
        await Eventually.Until(() => tools.Started > 0, "a clip to be read");
        await Eventually.Settle();

        tools.Started.ShouldBe(1);
        tools.Release();
        (await first).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await second).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tools.Started.ShouldBe(2);
    }

    // The time a clip may take is the whole clip's, not each of the dozen runs reading it: a run that
    // stays inside it a dozen times over is a meal waited on for minutes.
    [SkippableFact]
    public async Task AClipIsReadWithinOneBudget_NotOnePerRun()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        Skip.If(FakeTools.Missing is not null, FakeTools.Missing);
        using var tools = new FakeTools();
        var ffmpeg = tools.Write("ffmpeg", "sleep 1; exec ffmpeg \"$@\"");
        await using var server = await SugaryServer.StartAsync(configure: settings =>
            settings with { Clips = settings.Clips with { Ffmpeg = ffmpeg, TimeoutSeconds = 2 } });
        var app = await server.SignedInAsync();

        var response = await app.PostAsync("/api/estimates", Form("", clip: await TestClips.Silent()));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await DetailAsync(response)).ShouldBe(
            "Clips cannot be read on this server right now. Take a photo instead, or describe the meal.");
        StoredFiles(server).ShouldBeEmpty();
    }

    // Stand-ins for ffprobe and ffmpeg, as shell scripts in a directory of their own.
    private sealed class FakeTools : IDisposable
    {
        public static string? Missing => OperatingSystem.IsWindows() ? "The stand-in tools are shell scripts." : null;

        public string Directory { get; } = System.IO.Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sugary-tools", Guid.NewGuid().ToString("N"))).FullName;

        public int Started => System.IO.Directory.GetFiles(Directory, "started-*").Length;

        public string Write(string name, string body)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        public void Release() => File.WriteAllText(Path.Combine(Directory, "release"), "");

        public void Dispose()
        {
            Release();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    // The still's file name carries the moment it was taken from, which WebChat's review shows and
    // this test reads.
    internal static double TakenAt(AttachmentReference still) =>
        double.Parse(still.FileName.Split('-')[^1].TrimEnd('s', '.', 'j', 'p', 'g'),
            System.Globalization.CultureInfo.InvariantCulture);

    // The app's form, as Sugary.Client's estimateForm builds it: the clip a `clip` part named and
    // typed as the phone's file was, beside the photos and words.
    internal static MultipartFormDataContent Form(
        string description, IReadOnlyList<byte[]>? photos = null, byte[]? clip = null,
        string clipType = "video/mp4", string clipName = "meal.mp4")
    {
        var form = new MultipartFormDataContent();
        foreach (var (photo, index) in (photos ?? []).Select((p, i) => (p, i)))
        {
            var part = new ByteArrayContent(photo);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "photos", $"meal-{index}.jpg");
        }

        if (clip is not null)
        {
            var part = new ByteArrayContent(clip);
            part.Headers.ContentType = new MediaTypeHeaderValue(clipType);
            form.Add(part, "clip", clipName);
        }

        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(""), "clarifications");
        form.Add(new StringContent(""), "counted");
        form.Add(new StringContent("en"), "language");
        return form;
    }

    internal static MultipartFormDataContent TwoClips(byte[] clip)
    {
        var form = Form("paella", clip: clip);
        var second = new ByteArrayContent(clip);
        second.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(second, "clip", "second.mp4");
        return form;
    }

    internal static AgentCatalogEntry Sugary(string model, IReadOnlyList<AttachmentKind> accepts) =>
        new("sugary", "Sugary", null, model, DefaultModelAttachmentKinds: accepts);

    internal static IReadOnlyList<string> StoredFiles(SugaryServer server) =>
        Directory.Exists(server.Settings.Uploads.StoragePath)
            ? Directory.GetFiles(server.Settings.Uploads.StoragePath, "*", SearchOption.AllDirectories)
            : [];

    internal static Task<byte[]?> FetchAsync(McpChannelConnection agent, AttachmentReference reference) =>
        new ChannelAttachmentSource([agent]).FetchAsync("sugary", reference.Id, CancellationToken.None);

    internal static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(Wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

    internal static async Task ReplyAsync(McpChannelConnection agent, ChannelMessage turn, string reply)
    {
        await agent.SendReplyAsync(new SendReplyParams
        {
            ConversationId = turn.ConversationId,
            Content = reply,
            ContentType = ReplyContentType.Text,
            IsComplete = false,
            MessageId = "resp_1",
            TurnKey = turn.TurnKey
        }, CancellationToken.None);
        await agent.SendReplyAsync(new SendReplyParams
        {
            ConversationId = turn.ConversationId,
            Content = "",
            ContentType = ReplyContentType.StreamComplete,
            IsComplete = true,
            MessageId = "resp_1",
            TurnKey = turn.TurnKey
        }, CancellationToken.None);
    }

    internal static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>();
}