using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class MessageTruncatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghi", 3)]
    public void EstimateTokens_ReturnsCeilingOfCharsDividedByFour(string input, int expected)
    {
        MessageTruncator.EstimateTokens(input).ShouldBe(expected);
    }

    [Fact]
    public void EstimateMessageTokens_FunctionCall_CountsSerializedJsonPlusOverhead()
    {
        var call = new FunctionCallContent("call-1", "doStuff",
            new Dictionary<string, object?> { ["x"] = 1 });
        var msg = new ChatMessage(ChatRole.Assistant, [call]);

        // Production serializes {"name":"doStuff","arguments":{"x":1}} = 38 chars => ceil(38/4)=10
        // tokens, + 4 per-message overhead. Pinned (not re-serialized) so a change to the envelope
        // shape — e.g. dropping the name/arguments wrapper — shifts the count and trips this test.
        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(14);
    }

    [Fact]
    public void EstimateMessageTokens_FunctionResult_CountsSerializedResultPlusOverhead()
    {
        var result = new FunctionResultContent("call-1", "ok-result");
        var msg = new ChatMessage(ChatRole.Tool, [result]);

        // Serialized result is the quoted string "ok-result" = 11 chars => ceil(11/4)=3 tokens,
        // + 4 per-message overhead = 7. Pinned so a routing regression that lets the tool result
        // fall through to the generic-content overhead (4+4=8) is caught.
        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(7);
    }

    [Fact]
    public void EstimateMessageTokens_MultipleContents_SumsAllPlusSingleOverhead()
    {
        var msg = new ChatMessage(ChatRole.User,
            [new TextContent("abcd"), new TextContent("efgh")]); // 1 + 1 = 2 tokens
        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(2 + 4);
    }

    // Without an attachment case a 1 MB document counts as a fixed handful of tokens and
    // truncation goes blind on the very message most likely to overflow the window.
    [Fact]
    public void EstimateMessageTokens_ADocumentAttachment_CountsWithItsSize()
    {
        var small = new ChatMessage(ChatRole.User,
            [new DataContent(new byte[2_000], "application/pdf")]);
        var large = new ChatMessage(ChatRole.User,
            [new DataContent(new byte[200_000], "application/pdf")]);

        MessageTruncator.EstimateMessageTokens(small)
            .ShouldBeLessThan(MessageTruncator.EstimateMessageTokens(large));
        MessageTruncator.EstimateMessageTokens(large).ShouldBeGreaterThan(1_000);
    }

    // A provider resizes an image into its own tile scheme before billing, so the file's size
    // says almost nothing about what it costs.
    [Fact]
    public void EstimateMessageTokens_AnImageAttachment_CountsTheSameWhateverItsSize()
    {
        var small = new ChatMessage(ChatRole.User, [new DataContent(new byte[2_000], "image/png")]);
        var large = new ChatMessage(ChatRole.User, [new DataContent(new byte[2_000_000], "image/png")]);

        MessageTruncator.EstimateMessageTokens(small)
            .ShouldBe(MessageTruncator.EstimateMessageTokens(large));
        MessageTruncator.EstimateMessageTokens(small).ShouldBeGreaterThan(100);
    }

    // A document's file size tracks its images far more than its text, so an uncapped estimate
    // would put a 20 MB scan past the whole context window and make the truncator drop every
    // earlier message to make room for one attachment.
    [Fact]
    public void Truncate_AVeryLargeDocumentAttachment_DoesNotDropTheConversation()
    {
        var history = Enumerable.Range(0, 10)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"message {i}"))
            .ToList();
        history.Add(new ChatMessage(ChatRole.User,
            [new TextContent("what is in this?"), new DataContent(new byte[20 * 1024 * 1024], "application/pdf")]));

        var kept = MessageTruncator.Truncate(
            history, maxContextTokens: 800_000,
            out var dropped, out _, out _, out var overflow);

        overflow.ShouldBeFalse();
        dropped.ShouldBe(0);
        kept.Count.ShouldBe(history.Count);
    }

    [Fact]
    public void Truncate_NullMaxTokens_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "hi")
        };

        var result = MessageTruncator.Truncate(
            msgs, null, out var dropped, out var before, out var after, out var overflow);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(after);
        overflow.ShouldBeFalse();
    }

    [Fact]
    public void Truncate_UnderThreshold_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.User, "hi")
        };

        var result = MessageTruncator.Truncate(
            msgs, 10000, out var dropped, out var before, out var after, out var overflow);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(after);
        overflow.ShouldBeFalse();
    }

    [Fact]
    public void Truncate_EmptyList_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>();

        var result = MessageTruncator.Truncate(
            msgs, 100, out var dropped, out var before, out var after, out var overflow);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(0);
        after.ShouldBe(0);
        overflow.ShouldBeFalse();
    }

    [Fact]
    public void Truncate_OverThreshold_DropsOldestNonPinnedMessage()
    {
        // Build 4 messages so per-message text dominates.
        // Each "x" * 80 -> 20 tokens + 4 overhead = 24 tokens per message.
        var sys = new ChatMessage(ChatRole.System, new string('s', 80));
        var u1 = new ChatMessage(ChatRole.User, new string('a', 80));
        var a1 = new ChatMessage(ChatRole.Assistant, new string('b', 80));
        var u2 = new ChatMessage(ChatRole.User, new string('c', 80)); // last user (pinned)
        var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

        // total = 96. Threshold at 95% of 80 = 76. Need to drop until <= 76.
        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 80,
            out var dropped, out var before, out var after, out var overflow);

        dropped.ShouldBeGreaterThanOrEqualTo(1);
        result.ShouldContain(sys);                // system pinned
        result.ShouldContain(u2);                 // last user pinned
        result.ShouldNotContain(u1);              // oldest non-pinned dropped first
        after.ShouldBeLessThanOrEqualTo(76);
        before.ShouldBe(96);
        overflow.ShouldBeTrue();
    }

    [Fact]
    public void Truncate_AlwaysPreservesAllSystemMessages()
    {
        var sys1 = new ChatMessage(ChatRole.System, new string('a', 400));
        var sys2 = new ChatMessage(ChatRole.System, new string('b', 400));
        var u1 = new ChatMessage(ChatRole.User, new string('c', 80));
        var msgs = new List<ChatMessage> { sys1, sys2, u1 };

        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 50,
            out _, out _, out _, out _);

        result.ShouldContain(sys1);
        result.ShouldContain(sys2);
        result.ShouldContain(u1); // last user always preserved
    }

    [Fact]
    public void Truncate_StopsDroppingOnceUnderThreshold()
    {
        var sys = new ChatMessage(ChatRole.System, new string('s', 4));
        var u1 = new ChatMessage(ChatRole.User, new string('a', 80));
        var a1 = new ChatMessage(ChatRole.Assistant, new string('b', 80));
        var u2 = new ChatMessage(ChatRole.User, new string('c', 4));
        var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

        // Totals: sys=5, u1=24, a1=24, u2=5 → 58. Threshold floor(40*0.95)=38.
        // 58 > 38, drop u1 (oldest non-pinned) → 34, which is ≤ 38, stop.
        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 40,
            out var dropped, out _, out _, out _);

        dropped.ShouldBe(1);
        result.ShouldContain(a1); // not dropped — already under threshold
        result.ShouldNotContain(u1);
    }

    [Fact]
    public void Truncate_NeverSplitsToolCallResultPair()
    {
        // Without atomicity, dropping just the (oldest) assistant call could bring us under
        // threshold and leave its result stranded — an invalid request shape for OpenAI.
        // Inputs are sized so the BIG assistant call alone is enough to clear the threshold
        // when dropped, which without pair-grouping would strand the small tool result.
        var sys = new ChatMessage(ChatRole.System, new string('s', 4));
        var bigAssistant = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "doStuff",
                new Dictionary<string, object?> { ["padding"] = new string('p', 800) })]);
        var smallToolResult = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "ok")]);
        var lastUser = new ChatMessage(ChatRole.User, new string('u', 4));

        var msgs = new List<ChatMessage> { sys, bigAssistant, smallToolResult, lastUser };

        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 240,
            out var dropped, out _, out _, out _);

        // If bigAssistant is dropped, smallToolResult MUST also be dropped.
        var hasAssistant = result.Contains(bigAssistant);
        var hasResult = result.Contains(smallToolResult);
        hasAssistant.ShouldBe(hasResult);
        dropped.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Truncate_PinnedOnlyOverflow_FlagsOverflowEvenWithoutDrops()
    {
        // System + last-user alone exceed the threshold; nothing else can be dropped.
        var sys = new ChatMessage(ChatRole.System, new string('s', 800));
        var lastUser = new ChatMessage(ChatRole.User, new string('u', 800));
        var msgs = new List<ChatMessage> { sys, lastUser };

        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 50,
            out var dropped, out var before, out var after, out var overflow);

        dropped.ShouldBe(0);
        before.ShouldBe(after);
        overflow.ShouldBeTrue();
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Truncate_FixedOverhead_CountsTowardThreshold()
    {
        // Messages alone: sys=5, u1=24, a1=24, u2=5 → 58 (well under 95% of 80 = 76).
        // With 30 tokens of fixed overhead → 88, over threshold. u1 dropped → 64 ≤ 76.
        var sys = new ChatMessage(ChatRole.System, new string('s', 4));
        var u1 = new ChatMessage(ChatRole.User, new string('a', 80));
        var a1 = new ChatMessage(ChatRole.Assistant, new string('b', 80));
        var u2 = new ChatMessage(ChatRole.User, new string('c', 4));
        var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 80,
            out var dropped, out var before, out var after, out var overflow,
            fixedOverheadTokens: 30);

        overflow.ShouldBeTrue();
        before.ShouldBe(58 + 30);
        dropped.ShouldBe(1);
        after.ShouldBeLessThanOrEqualTo(76);
        result.ShouldContain(sys);
        result.ShouldContain(u2);
        result.ShouldContain(a1);
        result.ShouldNotContain(u1);
    }

    [Fact]
    public void EstimateOptionsOverheadTokens_NullOptions_ReturnsZero()
    {
        MessageTruncator.EstimateOptionsOverheadTokens(null).ShouldBe(0);
    }

    [Fact]
    public void EstimateOptionsOverheadTokens_Instructions_CountsAsTokens()
    {
        var options = new ChatOptions { Instructions = new string('x', 400) }; // 100 tokens

        var overhead = MessageTruncator.EstimateOptionsOverheadTokens(options);

        overhead.ShouldBe(100);
    }

    [Fact]
    public void EstimateOptionsOverheadTokens_FunctionTools_CountsNameDescriptionAndSchema()
    {
        var fn = AIFunctionFactory.Create(
            (string padding) => "ok",
            new AIFunctionFactoryOptions { Name = "doStuff", Description = "does the thing" });
        var options = new ChatOptions { Tools = [fn] };

        var overhead = MessageTruncator.EstimateOptionsOverheadTokens(options);

        // The JSON schema text is library-generated, so its token count stays dynamic — but it MUST
        // contribute (a dropped schema term silently shrinks the budget and overflows the model).
        // Name ("doStuff") => 2 tokens, description ("does the thing") => 4 tokens, + 4 per-tool
        // overhead are pinned, so dropping any term or drifting the overhead constant trips the test.
        var schemaTokens = MessageTruncator.EstimateTokens(fn.JsonSchema.GetRawText());
        schemaTokens.ShouldBeGreaterThan(0);
        overhead.ShouldBe(2 + 4 + schemaTokens + 4);
    }

    [Fact]
    public void EstimateOptionsOverheadTokens_InstructionsAndTools_Sums()
    {
        var fn = AIFunctionFactory.Create(
            (string p) => "ok",
            new AIFunctionFactoryOptions { Name = "f", Description = "d" });
        var options = new ChatOptions
        {
            Instructions = new string('x', 400),
            Tools = [fn]
        };

        var overhead = MessageTruncator.EstimateOptionsOverheadTokens(options);

        // Tool overhead is taken from the SUT itself (not re-derived from the formula) so this test
        // pins the SUM: instructions (400 chars => 100 tokens) + tool overhead. A regression that
        // combined the two with Math.Max instead of '+' would still pass each single-operand sibling
        // but fails here, since 100 + toolOverhead > max(100, toolOverhead) when both are non-zero.
        var toolOverhead = MessageTruncator.EstimateOptionsOverheadTokens(new ChatOptions { Tools = [fn] });
        overhead.ShouldBe(100 + toolOverhead);
    }
}