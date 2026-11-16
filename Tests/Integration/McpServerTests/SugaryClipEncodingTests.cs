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

using static Tests.Integration.McpServerTests.SugaryClipApiTests;

namespace Tests.Integration.McpServerTests;

// The clip's encoding half of SugaryClipApiTests: what a filmed meal becomes on its way to the turn —
// the one shape, the stills, a phone's every container — each case running ffmpeg on a real clip. A
// class of its own so the two halves run beside each other rather than as one chain, which was among
// the suite's longest; the helpers stay with SugaryClipApiTests.
public class SugaryClipEncodingTests
{
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

    [SkippableTheory]
    [MemberData(nameof(Recorded), MemberType = typeof(SugaryClipApiTests))]
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
}