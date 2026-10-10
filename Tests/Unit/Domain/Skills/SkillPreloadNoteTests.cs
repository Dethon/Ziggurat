using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// What a model is sent for a preload: one user-role note after the request, never the pair the
// thread records. A call the model did not make reads to it as its own turn already under way —
// Haiku 5.5 then skipped the word a spoken reply opens slow work with on every run, and
// gpt-6-luna on six in ten (2026-10-10) — and it is the one assistant item no provider issued,
// which is what each provider refused in its own way.
public class SkillPreloadNoteTests
{
    private static readonly JsonObject _index = new()
    {
        ["filePath"] = "/ha/setup-index.md",
        ["content"] = "1: ## Current Home Assistant setup\n2: \n3: ### salón — `climate.salon_(aire-salon)`"
    };

    private static SkillPreload HomePreload => new(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
    {
        Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", _index)]
    };

    private static List<ChatMessage> Turn(IEnumerable<ChatMessage> preload) =>
        [new(ChatRole.User, "enciende el aire del salón"), .. preload];

    // A thread as Redis hands it back: each message through the store's own serializer.
    private static List<ChatMessage> Stored(IEnumerable<ChatMessage> messages) =>
        [.. messages.Select(m => JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(m))!)];

    private static IReadOnlyList<string> Texts(ChatMessage message) =>
        [.. message.Contents.Select(c => c.ShouldBeOfType<TextContent>().Text)];

    [Fact]
    public void APreloadedSkillWithItsRead_IsSentAsOneUserNoteAfterTheRequest()
    {
        var thread = Turn(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc"));

        var sent = SkillPreloadNote.AsSent(thread);

        sent.Select(m => m.Role.Value).ShouldBe(["user", "user"]);
        sent[0].ShouldBeSameAs(thread[0]);
        var parts = Texts(sent[1]);
        parts.Count.ShouldBe(2);
        parts[0].ShouldBe(
            "[Loaded for this request before your first call, by the host: the `home-assistant` skill. Do not load it again.]\n\n"
            + SkillLoadTool.Wrapped(TestSkills.HomeWithIndex));
        parts[1].ShouldStartWith(
            "[Read for this request before your first call, by the host: `/ha/setup-index.md`. This is what `file_read` returned.]\n\n{");
        JsonNode.Parse(parts[1][parts[1].IndexOf('{')..])!["content"]!.GetValue<string>()
            .ShouldBe(_index["content"]!.GetValue<string>());
        // Read as the model reads it: the room's accent and the backticks are not escapes.
        parts[1].ShouldContain("### salón — `climate.salon_(aire-salon)`");
    }

    [Fact]
    public void TheNote_CarriesNothingThatDecoratesAPersonsMessage()
    {
        var sent = SkillPreloadNote.AsSent(Turn(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc")));

        sent[1].AdditionalProperties.ShouldBeNull();
        sent[1].AuthorName.ShouldBeNull();
        sent.SelectMany(m => m.Contents).ShouldAllBe(c => c is TextContent);
    }

    // The provider's cache is a byte prefix: the note a later turn sends, built from what the
    // store read back, has to be the note the turn itself sent from the objects it had in hand.
    [Fact]
    public void TheNote_IsTheSameBytes_OnceTheThreadHasBeenStoredAndReadBack()
    {
        var thread = Turn(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc"));

        var live = SkillPreloadNote.AsSent(thread);
        var later = SkillPreloadNote.AsSent(Stored(thread));

        later.Select(m => m.Role.Value).ShouldBe(["user", "user"]);
        Texts(later[1]).ShouldBe(Texts(live[1]));
    }

    // Threads stored before this shape hold the pair under two earlier id schemes, and the last
    // of them with the reasoning the host wrote in front of its calls. None is migrated: each is
    // the same record, and the model is sent the same note for it.
    [Theory]
    [InlineData("preload-1a2b3c4d-1", "preload-1a2b3c4d-read-1")]
    [InlineData("1-preload-1a2b3c4d", "2-preload-1a2b3c4d")]
    public void APairStoredByAnEarlierBuild_IsSentAsTheSameNote(string loadId, string readId)
    {
        var reasoning = new TextReasoningContent("The request needs the home-assistant skill: loading it.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "host-reasoning-preload-1a2b3c4d" }
        };
        List<ChatMessage> pair =
        [
            new(ChatRole.Assistant,
            [
                reasoning,
                new FunctionCallContent(loadId, SkillLoadTool.Name, new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = "home-assistant" }),
                new FunctionCallContent(readId, SkillLoadTool.ReadToolName, new Dictionary<string, object?> { [VfsFileReadTool.FilePathParameter] = "/ha/setup-index.md" })
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent(loadId, SkillLoadTool.Wrapped(TestSkills.HomeWithIndex)),
                new FunctionResultContent(readId, _index.DeepClone())
            ])
        ];
        var expected = Texts(SkillPreloadNote.AsSent(Turn(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc")))[1]);

        var sent = SkillPreloadNote.AsSent(Stored(Turn(pair)));

        sent.Select(m => m.Role.Value).ShouldBe(["user", "user"]);
        Texts(sent[1]).ShouldBe(expected);
    }

    [Fact]
    public void ALaterTurn_KeepsTheNoteWhereThePreloadWasRecorded()
    {
        List<ChatMessage> thread =
        [
            .. Turn(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc")),
            new(ChatRole.Assistant, "Hecho."),
            new(ChatRole.User, "y ahora apágalo")
        ];

        var sent = SkillPreloadNote.AsSent(thread);

        sent.Select(m => m.Role.Value).ShouldBe(["user", "user", "assistant", "user"]);
        sent[1].Text.ShouldContain("the `home-assistant` skill");
        sent[2].ShouldBeSameAs(thread[3]);
        sent[3].ShouldBeSameAs(thread[4]);
    }

    // A load the model made is its own turn, and stays the call and the result it left.
    [Fact]
    public void AModelsOwnLoadAndRead_AreLeftAsTheCallsItMade()
    {
        var load = new FunctionCallContent("call_00_SCPBlKINIJOAjSnSzf8W3355", SkillLoadTool.Name,
            new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = "home-assistant" });
        var read = new FunctionCallContent("toolu_01FcAJJFvL7xTsCojgL1CckL", SkillLoadTool.ReadToolName,
            new Dictionary<string, object?> { [VfsFileReadTool.FilePathParameter] = "/ha/setup-index.md" });
        List<ChatMessage> thread =
        [
            new(ChatRole.User, "enciende el aire del salón"),
            new(ChatRole.Assistant, [load, read]),
            new(ChatRole.Tool, [new FunctionResultContent(load.CallId, "body"), new FunctionResultContent(read.CallId, _index.DeepClone())])
        ];

        SkillPreloadNote.AsSent(thread).ShouldBe(thread);
    }

    // Half a pair is not a preload the model can be told about: the truncator drops a call and
    // its results together, so a call left without its result names nothing that is still here.
    [Fact]
    public void ACallWhoseResultIsGone_IsNotSentAtAll()
    {
        var pair = SkillLoadTool.AsPreloaded(HomePreload, "preload-abc");
        var thread = Turn([pair[0]]);

        SkillPreloadNote.AsSent(thread).Select(m => m.Role.Value).ShouldBe(["user"]);
    }
}