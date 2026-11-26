using System.Text;
using Domain.Apps;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// The app ask's wire: what a page may send, what an app's instructions may be, and what each refusal of
// the agent's is to the app.
public class AppAsksTests
{
    // The bound is what keeps an app's instructions inside the section they are composed into: at the
    // bound, the section is never reported over its budget.
    [Fact]
    public void TheInstructionsBound_FitsTheAppInstructionsSectionsBudget()
    {
        var budget = PromptManifest.Find(PromptManifest.AppInstructions)!.TokenBudget;

        PromptTokens.Estimate(new string('x', AppAsks.MaxInstructionsBytes)).ShouldBeLessThanOrEqualTo(budget);
    }

    public static TheoryData<string, string, bool> Refusals => new()
    {
        { RefusalAnswers.NotVerified, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.Suspended, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.Disabled, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.AppsOff, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.AgentGone("jonas"), ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.WithoutKey, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.KeyRefused, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.OutOfCredit, ToolError.Codes.PermissionDenied, true },
        // Whether the household lets it through could not be read: worth asking again.
        { RefusalAnswers.Unchecked, ToolError.Codes.TransientDependency, true },
        // No refusal: what the agent caught, kept from the page.
        { RefusalAnswers.SetupFailed, ToolError.Codes.TransientDependency, false },
        { "Provider returned 500: {\"error\":\"credits\"}", ToolError.Codes.TransientDependency, false }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusal_IsTheEnvelopeItsCodeSays_InTheAgentsWordsOnlyWhenItIsARefusal(
        string answer, string code, bool shown)
    {
        var envelope = AppAsks.Refusal(answer);

        envelope.ErrorCode.ShouldBe(code);
        (envelope.Message == answer).ShouldBe(shown);
        envelope.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ARefusalNamingTheAgent_NamesItToTheApp() =>
        AppAsks.Refusal(RefusalAnswers.AgentGone("jonas")).Message.ShouldContain("'jonas'");

    [Fact]
    public void AnAsksText_IsRead()
    {
        var request = AppAskRequest.Parse("""{"text":"How much is this?"}"""u8, maxText: 100, maxSchemaBytes: 100)
            .ShouldBeOfType<AppAskRequestParse.Valid>().Request;

        request.Text.ShouldBe("How much is this?");
        request.Schema.ShouldBeNull();
    }

    // A conversation to continue is read as written: whether it is one this app and viewer may continue is
    // the apps server's to say, and every id it may not continue is answered alike, whatever its shape.
    [Theory]
    [InlineData("c1-topic")]
    [InlineData("")]
    public void AnAsksConversation_IsReadAsWritten(string conversation) =>
        AppAskRequest.Parse(Encoding.UTF8.GetBytes($$"""{"text":"Hi","conversation":"{{conversation}}"}"""), maxText: 100, maxSchemaBytes: 100)
            .ShouldBeOfType<AppAskRequestParse.Valid>().Request.Conversation.ShouldBe(conversation);

    [Fact]
    public void AnAskWithoutAConversation_ContinuesNone() =>
        AppAskRequest.Parse("""{"text":"Hi","conversation":null}"""u8, maxText: 100, maxSchemaBytes: 100)
            .ShouldBeOfType<AppAskRequestParse.Valid>().Request.Conversation.ShouldBeNull();

    // A conversation the app may not continue is one code whatever the reason, and asking it again cannot
    // change the answer: the app starts afresh.
    [Fact]
    public void AConversationThatCannotBeContinued_IsItsOwnCode_NotWorthAskingAgain()
    {
        var envelope = AppAsks.ConversationExpired;

        envelope.ErrorCode.ShouldBe(ToolError.Codes.ConversationExpired);
        envelope.Retryable.ShouldBeFalse();
        envelope.Hint.ShouldNotBeNull().ShouldContain("without the conversation");
    }

    // The schema goes to the agent as the page wrote it: strict mode's rules are the page's to meet, and
    // nothing here rewrites it.
    [Fact]
    public void AnAsksSchema_IsReadAsWritten()
    {
        var schema = AppAskRequest.Parse(
                """{"text":"Hi","schema":{"name":"meal_estimate","schema":{"type":"object","properties":{"grams":{"type":"number","minimum":0}},"required":["grams"],"additionalProperties":false}}}"""u8,
                maxText: 100, maxSchemaBytes: 1000)
            .ShouldBeOfType<AppAskRequestParse.Valid>().Request.Schema.ShouldNotBeNull();

        schema.Name.ShouldBe("meal_estimate");
        schema.Schema.GetRawText().ShouldBe(
            """{"type":"object","properties":{"grams":{"type":"number","minimum":0}},"required":["grams"],"additionalProperties":false}""");
    }

    [Fact]
    public void ASchemaAtTheBound_IsRead()
    {
        var schema = """{"type":"object"}""";

        AppAskRequest.Parse(Encoding.UTF8.GetBytes("""{"text":"Hi","schema":{"name":"n","schema":""" + schema + "}}"),
                maxText: 10, maxSchemaBytes: Encoding.UTF8.GetByteCount(schema))
            .ShouldBeOfType<AppAskRequestParse.Valid>();
    }

    [Theory]
    [InlineData("""{"text":""}""", "1 to 10 characters")]
    [InlineData("""{"text":"   "}""", "1 to 10 characters")]
    [InlineData("""{"text":"eleven char"}""", "1 to 10 characters")]
    [InlineData("""{}""", "text")]
    [InlineData("""{"text":null}""", "text")]
    [InlineData("""{"text":"hi","stream":true}""", "stream")]
    [InlineData("""{"text":"hi","conversation":5}""", "conversation")]
    [InlineData("""["hi"]""", "JSON object")]
    [InlineData("""not json""", "JSON object")]
    [InlineData("""{"text":"hi","schema":"meal"}""", "schema")]
    [InlineData("""{"text":"hi","schema":{"schema":{}}}""", "name")]
    [InlineData("""{"text":"hi","schema":{"name":"meal"}}""", "schema")]
    [InlineData("""{"text":"hi","schema":{"name":"meal","schema":{},"strict":false}}""", "strict")]
    [InlineData("""{"text":"hi","schema":{"name":"meal","schema":[]}}""", "a JSON object")]
    [InlineData("""{"text":"hi","schema":{"name":"meal","schema":"object"}}""", "a JSON object")]
    [InlineData("""{"text":"hi","schema":{"name":"meal","schema":null}}""", "a JSON object")]
    [InlineData("""{"text":"hi","schema":{"name":"meal estimate","schema":{}}}""", "letters, digits, _ and -")]
    [InlineData("""{"text":"hi","schema":{"name":"","schema":{}}}""", "letters, digits, _ and -")]
    [InlineData("""{"text":"hi","schema":{"name":"x123456789x123456789x123456789x123456789x123456789x123456789x1234","schema":{}}}""", "1 to 64")]
    [InlineData("""{"text":"hi","schema":{"name":"meal","schema":{"type":"object","description":"twenty-nine characters"}}}""", "over the 40 bytes")]
    public void AnAskOutsideItsShape_IsRefusedWhole(string body, string words)
    {
        AppAskRequest.Parse(Encoding.UTF8.GetBytes(body), maxText: 10, maxSchemaBytes: 40)
            .ShouldBeOfType<AppAskRequestParse.Refused>().Problem.ShouldContain(words);
    }

    [Fact]
    public void Instructions_AreTheFilesText()
    {
        AppAsks.ReadInstructions("instructions.md", "Answer in one word."u8.ToArray())
            .ShouldBeOfType<AppInstructionsRead.Read>().Text.ShouldBe("Answer in one word.");
    }

    public static TheoryData<byte[]?, string> UnreadableInstructions => new()
    {
        { null, "not a file of the app" },
        { new byte[AppAsks.MaxInstructionsBytes + 1].Select(_ => (byte)'x').ToArray(), $"over the {AppAsks.MaxInstructionsBytes} bytes" },
        { " \n\t"u8.ToArray(), "empty" },
        { [0xff, 0xfe, 0x41], "not UTF-8" }
    };

    [Theory]
    [MemberData(nameof(UnreadableInstructions))]
    public void Instructions_ThatAreNoFileOrOverTheBound_AreRefusedNamingWhy(byte[]? file, string words)
    {
        AppAsks.ReadInstructions("asks/estimate.md", file)
            .ShouldBeOfType<AppInstructionsRead.Refused>().Problem.ShouldContain(words);
    }

    [Fact]
    public void Instructions_AtTheBound_AreRead() =>
        AppAsks.ReadInstructions("i.md", Enumerable.Repeat((byte)'x', AppAsks.MaxInstructionsBytes).ToArray())
            .ShouldBeOfType<AppInstructionsRead.Read>();

    [Theory]
    [InlineData("""{"grams":12}""", """{"grams":12}""")]
    [InlineData(" \n{\"grams\":12}\n", """{"grams":12}""")]
    [InlineData("[1,2]", "[1,2]")]
    public void AReplyToASchema_IsItsJson(string reply, string value) =>
        AppAsks.ReadReply(reply).ShouldBeOfType<AppAskReplyRead.Parsed>().Value.GetRawText().ShouldBe(value);

    // Read whole or not at all: a reply in prose around its JSON, or fenced, is not what a strict format asked for.
    [Theory]
    [InlineData("Sure! {\"grams\":12}")]
    [InlineData("```json\n{\"grams\":12}\n```")]
    [InlineData("{\"grams\":12} and that is all")]
    [InlineData("{\"grams\":")]
    public void AReplyThatIsNotJsonWhole_IsUnparsed(string reply) =>
        AppAsks.ReadReply(reply).ShouldBeOfType<AppAskReplyRead.Unparsed>();

    // The reply's own words are kept for the author's preview alone: anywhere else they could carry what the
    // model read for one viewer to whoever reads the app's errors.
    [Fact]
    public void AnInvalidReply_IsItsOwnCode_NotWorthAskingAgainUnchanged_AndCarriesTheReplyOnlyWhenGiven()
    {
        var hidden = AppAsks.InvalidReply(reply: null);
        var shown = AppAsks.InvalidReply(reply: "Twelve grams.");

        hidden["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidReply);
        hidden["retryable"]!.GetValue<bool>().ShouldBeFalse();
        hidden["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        hidden.ContainsKey(AppAsks.ReplyField).ShouldBeFalse();
        shown[AppAsks.ReplyField]!.GetValue<string>().ShouldBe("Twelve grams.");
        shown["message"]!.GetValue<string>().ShouldBe(hidden["message"]!.GetValue<string>());
    }

    [Fact]
    public void AnInvalidReplysWords_AreClippedToTheBound() =>
        AppAsks.InvalidReply(new string('x', AppAsks.MaxReplyShown + 10))[AppAsks.ReplyField]!.GetValue<string>()
            .Length.ShouldBe(AppAsks.MaxReplyShown);

    private static readonly string[] _allTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];

    public static TheoryData<byte[], string> Images => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0, 1, 2], "image/jpeg" },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1], "image/png" },
        { [.. "RIFF"u8, 1, 0, 0, 0, .. "WEBP"u8, .. "VP8 "u8], "image/webp" },
        { [.. "GIF89a"u8, 1, 2], "image/gif" }
    };

    // An image's type is what its bytes are, whatever its name or the page said: the model is sent that type.
    [Theory]
    [MemberData(nameof(Images))]
    public void AnImage_IsTheTypeItsBytesAre_WhateverItIsCalled(byte[] bytes, string type)
    {
        var read = AppAskImages.Read([new AppAskUpload("photo.txt", bytes)], 4, 1024, _allTypes)
            .ShouldBeOfType<AppAskImagesRead.Valid>();

        var image = read.Images.ShouldHaveSingleItem();
        (image.FileName, image.MediaType).ShouldBe(("photo.txt", type));
        image.Bytes.ShouldBe(bytes);
    }

    // Anything past the limits, or whose bytes are no image the deployment takes, refuses the ask whole,
    // naming the limit, so the page can say what to change.
    [Theory]
    [InlineData("count", "At most 2 images")]
    [InlineData("empty", "empty")]
    [InlineData("size", "at most 8 bytes")]
    [InlineData("not an image", "JPEG, PNG, WebP or GIF")]
    [InlineData("not taken here", "JPEG or PNG")]
    public void ImagesPastTheLimits_OrNoImageTaken_AreRefusedNamingWhy(string what, string words)
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
        IReadOnlyList<AppAskUpload> uploads = what switch
        {
            "count" => [.. Enumerable.Range(0, 3).Select(i => new AppAskUpload($"{i}.jpg", jpeg))],
            "empty" => [new AppAskUpload("a.jpg", [])],
            "size" => [new AppAskUpload("a.jpg", [.. jpeg, 1, 2, 3, 4, 5])],
            "not an image" => [new AppAskUpload("a.jpg", [.. "%PDF-1.7"u8])],
            _ => [new AppAskUpload("a.gif", [.. "GIF89a"u8])]
        };
        string[] types = what == "not taken here" ? ["image/jpeg", "image/png"] : _allTypes;

        AppAskImages.Read(uploads, 2, 8, types).ShouldBeOfType<AppAskImagesRead.Refused>().Problem.ShouldContain(words);
    }

    [Fact]
    public void NoImages_AreRead() =>
        AppAskImages.Read([], 4, 1024, _allTypes).ShouldBeOfType<AppAskImagesRead.Valid>().Images.ShouldBeEmpty();
}