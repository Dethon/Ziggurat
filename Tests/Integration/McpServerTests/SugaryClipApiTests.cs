using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
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
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(60);

    private const string Estimate =
        """
        {"items":[{"name":"paella","portion":"a plate","gramsLow":250,"grams":300,"gramsHigh":350,"carbsPer100g":20,"confidence":"medium","note":null}],
         "questions":[],"notes":[]}
        """;

    [SkippableFact]
    public async Task AFilmedMeal_ReachesTheTurnAsAClipNamingItsStills_EachAnAttachmentOfTheSameMessage()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", clip: await TestClips.WithSound()));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldStartWith("A video of the meal is attached, filmed while moving around the plate.");
        turn.Content.ShouldNotContain("There is no photo");
        var attachments = turn.Attachments.ShouldNotBeNull();
        var clip = attachments.Where(a => a.MediaType.StartsWith("video/")).ShouldHaveSingleItem();
        clip.MediaType.ShouldBe("video/mp4");
        var stills = clip.Stills.ShouldNotBeNull();
        stills.Count.ShouldBeInRange(6, 8);
        stills.Distinct().Count().ShouldBe(stills.Count);
        // A still is an attachment of the message in its own right, since hydration fetches it as one.
        attachments.Where(a => a.Id != clip.Id).Select(a => a.Id).ShouldBe(stills, ignoreOrder: true);

        foreach (var still in attachments.Where(a => stills.Contains(a.Id)))
        {
            still.MediaType.ShouldBe("image/jpeg");
            var bytes = (await FetchAsync(agent, still)).ShouldNotBeNull();
            bytes.Length.ShouldBe((int)still.SizeBytes);
            bytes[..2].ShouldBe(new byte[] { 0xFF, 0xD8 });
            var picture = (await TestClips.ProbeAsync(bytes))["streams"]![0]!;
            // The size the app shrinks a photo to: the phone's picture, not the clip's 540 lines.
            picture["width"]!.GetValue<int>().ShouldBe(1280);
            picture["height"]!.GetValue<int>().ShouldBe(720);
        }

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The sound of a meal is the conversation at the table: it never leaves the server, and what the
    // agent fetches is the one shape every video model takes.
    [SkippableFact]
    public async Task AClipWithSound_IsSentAsH264WithinFiveHundredFortyLines_AndWithNoSound()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("paella", clip: await TestClips.WithSound()));
        var turn = await NextTurnAsync(agent);

        var clip = turn.Attachments.ShouldNotBeNull().Single(a => a.MediaType == "video/mp4");
        var probe = await TestClips.ProbeAsync((await FetchAsync(agent, clip)).ShouldNotBeNull());
        var streams = probe["streams"]!.AsArray();
        streams.ShouldHaveSingleItem()!["codec_type"]!.GetValue<string>().ShouldBe("video");
        streams[0]!["codec_name"]!.GetValue<string>().ShouldBe("h264");
        streams[0]!["width"]!.GetValue<int>().ShouldBe(960);
        streams[0]!["height"]!.GetValue<int>().ShouldBe(540);
        probe["format"]!["format_name"]!.GetValue<string>().ShouldContain("mp4");

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Whatever the phone recorded. A portrait clip keeps its shape, with its short side at 540, and
    // one stored sideways with a flag to turn it arrives turned.
    public static TheoryData<string, int, int> Recorded => new()
    {
        { "HEVC in a QuickTime file, portrait", 540, 960 },
        { "VP9 and Opus in WebM", 960, 540 },
        { "a browser recorder's WebM with no stated length", 640, 360 },
        { "stored sideways with a rotation flag", 540, 960 }
    };

    [SkippableTheory]
    [MemberData(nameof(Recorded))]
    public async Task WhatPhonesRecord_IsAccepted_AndSentAsTheOneShape(string what, int width, int height)
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        var recorded = what switch
        {
            "HEVC in a QuickTime file, portrait" => (await TestClips.HevcPortrait(), "video/quicktime", "clip.mov"),
            "VP9 and Opus in WebM" => (await TestClips.WebM(), "video/webm", "clip.webm"),
            "a browser recorder's WebM with no stated length" => (await TestClips.Recorder(3), "video/webm", "clip.webm"),
            _ => (await TestClips.Rotated(), "video/mp4", "clip.mp4")
        };
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates",
            Form("", clip: recorded.Item1, clipType: recorded.Item2, clipName: recorded.Item3));
        var turn = await NextTurnAsync(agent);

        var clip = turn.Attachments.ShouldNotBeNull().Single(a => a.MediaType == "video/mp4");
        var probe = await TestClips.ProbeAsync((await FetchAsync(agent, clip)).ShouldNotBeNull());
        var streams = probe["streams"]!.AsArray();
        streams.ShouldHaveSingleItem(what)!["codec_name"]!.GetValue<string>().ShouldBe("h264", what);
        streams[0]!["width"]!.GetValue<int>().ShouldBe(width, what);
        streams[0]!["height"]!.GetValue<int>().ShouldBe(height, what);
        clip.Stills.ShouldNotBeNull(what).ShouldNotBeEmpty(what);

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK, what);
    }

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

    // A still is taken from the sharpest moment of each stretch of the clip, and a stretch with
    // nothing sharp in it gives none: a blurred frame shows a model less than no frame.
    [SkippableFact]
    public async Task TheStills_SkipTheBlurredStretchesOfTheClip()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", clip: await TestClips.PartlyBlurred()));
        var turn = await NextTurnAsync(agent);

        var attachments = turn.Attachments.ShouldNotBeNull();
        var clip = attachments.Single(a => a.MediaType == "video/mp4");
        var stills = attachments.Where(a => clip.Stills!.Contains(a.Id)).ToList();
        // Seconds 2.5 to 5.5 of eight are blurred: the two stretches wholly inside give no still, and
        // the two it cuts into give their sharp moments.
        stills.Count.ShouldBeInRange(5, 6);
        var (from, to) = TestClips.Blurred;
        stills.Select(TakenAt).ShouldAllBe(at => at < from - 0.05 || at > to + 0.05);

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Her photos and her clip go together: the text counts the photos she took, never the stills,
    // and the photos stay ordinary pictures beside them.
    [SkippableFact]
    public async Task PhotosAndAClip_GoOnOneTurn_AndTheTextCountsHerPhotosAlone()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();
        byte[] photo = [0xFF, 0xD8, 1, 2, 3];

        var answer = app.PostAsync("/api/estimates", Form("", photos: [photo], clip: await TestClips.Silent()));
        var turn = await NextTurnAsync(agent);

        turn.Content.ShouldStartWith(
            "One photo of the meal is attached. A video of the same meal is attached too, filmed while moving around the plate.");
        var attachments = turn.Attachments.ShouldNotBeNull();
        var clip = attachments.Single(a => a.MediaType == "video/mp4");
        var photos = attachments.Where(a => a.Id != clip.Id && !clip.Stills!.Contains(a.Id)).ToList();
        (await FetchAsync(agent, photos.ShouldHaveSingleItem())).ShouldBe(photo);
        // Kept with the photos, under the conversation WebChat files the same meal under.
        attachments.Select(a => a.Id.Split('/')[0]).Distinct().ShouldHaveSingleItem();

        await ReplyAsync(agent, turn, Estimate);
        (await answer).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The choice between clip and stills is the send's, made for the model the turn resolved to
    // (ADR-0045), and the wire tests pin it over references built by hand. This pins that the
    // references this channel actually sends drive it: the clip alone for a model that watches
    // video, the stills and her photo for one that only sees pictures, and never a refusal.
    [SkippableFact]
    public async Task TheTurnsAttachments_GiveAVideoModelTheClip_AndAPictureModelTheStillsAndPhotos()
    {
        Skip.If(TestClips.Missing is not null, TestClips.Missing);
        await using var server = await SugaryServer.StartAsync();
        var agent = await server.AgentAsync();
        var app = await server.SignedInAsync();

        var answer = app.PostAsync("/api/estimates", Form("", photos: [[0xFF, 0xD8, 9]], clip: await TestClips.Silent()));
        var turn = await NextTurnAsync(agent);
        var attachments = turn.Attachments.ShouldNotBeNull();
        var clip = attachments.Single(a => a.MediaType == "video/mp4");

        var watched = ClipSelection.For(
            attachments, new ModelInputs("google/gemini-3.8-flash", Images: true, Video: true), withinDepth: true);
        var seen = ClipSelection.For(
            attachments, new ModelInputs("anthropic/claude-haiku-5.5", Images: true, Video: false), withinDepth: true);

        watched.Where(a => a.MediaType.StartsWith("image/")).ShouldHaveSingleItem().Id
            .ShouldNotBeOneOf([.. clip.Stills!]);
        watched.ShouldContain(clip);
        seen.ShouldNotContain(clip);
        seen.Count.ShouldBe(clip.Stills!.Count + 1);

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
        await agent.RegisterAgentsAsync([Sugary("text/only", accepts: [])], CancellationToken.None);
        var app = await server.SignedInAsync();

        var refused = await app.PostAsync("/api/estimates", Form("", clip: await TestClips.Silent()));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(refused)).ShouldBe(
            "text/only can neither watch a clip nor read photos, so this meal cannot be estimated from it. " +
            "Describe it in words instead, or count it by hand.");
        StoredFiles(server).ShouldBeEmpty();

        await agent.RegisterAgentsAsync([Sugary("sees/pictures", accepts: [AttachmentKind.Image])], CancellationToken.None);
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
    [Theory]
    [InlineData("nothing")]
    [InlineData("not json")]
    public async Task AClipWhoseProbeAnswersNothingReadable_IsRefusedAsUnreadable(string answer)
    {
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
    [Fact]
    public async Task ClipsAreReadAFewAtATime()
    {
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
        public string Directory { get; } = System.IO.Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sugary-tools", Guid.NewGuid().ToString("N"))).FullName;

        public int Started => System.IO.Directory.GetFiles(Directory, "started-*").Length;

        public string Write(string name, string body)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
    private static double TakenAt(AttachmentReference still) =>
        double.Parse(still.FileName.Split('-')[^1].TrimEnd('s', '.', 'j', 'p', 'g'),
            System.Globalization.CultureInfo.InvariantCulture);

    // The app's form, as Sugary.Client's estimateForm builds it: the clip a `clip` part named and
    // typed as the phone's file was, beside the photos and words.
    private static MultipartFormDataContent Form(
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

    private static MultipartFormDataContent TwoClips(byte[] clip)
    {
        var form = Form("paella", clip: clip);
        var second = new ByteArrayContent(clip);
        second.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(second, "clip", "second.mp4");
        return form;
    }

    private static AgentCatalogEntry Sugary(string model, IReadOnlyList<AttachmentKind> accepts) =>
        new("sugary", "Sugary", null, model, DefaultModelAttachmentKinds: accepts);

    private static IReadOnlyList<string> StoredFiles(SugaryServer server) =>
        Directory.Exists(server.Settings.Uploads.StoragePath)
            ? Directory.GetFiles(server.Settings.Uploads.StoragePath, "*", SearchOption.AllDirectories)
            : [];

    private static Task<byte[]?> FetchAsync(McpChannelConnection agent, AttachmentReference reference) =>
        new ChannelAttachmentSource([agent]).FetchAsync("sugary", reference.Id, CancellationToken.None);

    private static async Task<ChannelMessage> NextTurnAsync(McpChannelConnection agent)
    {
        using var cts = new CancellationTokenSource(_wait);
        return await agent.Messages.FirstAsync(cts.Token);
    }

    private static async Task ReplyAsync(McpChannelConnection agent, ChannelMessage turn, string reply)
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

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>();
}