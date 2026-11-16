using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public sealed class BufferRebuildUtilityTests
{
    #region RebuildFromBuffer

    public static IEnumerable<object[]> RebuildBasicStateCases =>
    [
        ["Empty buffer", new List<ChatStreamMessage>(),
            new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
            {
                completed.ShouldBeEmpty();
                streaming.Role.ShouldBe("assistant");
                streaming.Content.ShouldBeEmpty();
            })],
        ["Single streaming turn (no complete flag)", new List<ChatStreamMessage>
        {
            new() { Content = "Hello", MessageId = "msg-1" },
            new() { Content = " world", MessageId = "msg-1" }
        }, new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
        {
            completed.ShouldBeEmpty();
            streaming.Content.ShouldBe("Hello world");
        })],
        ["Single complete turn finalizes", new List<ChatStreamMessage>
        {
            new() { Content = "First turn", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" }
        }, new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
        {
            completed.Count.ShouldBe(1);
            completed[0].Content.ShouldBe("First turn");
            streaming.Content.ShouldBeEmpty();
        })],
        ["Complete turn followed by partial second turn", new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        }, new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
        {
            completed.Count.ShouldBe(1);
            completed[0].Content.ShouldBe("First");
            streaming.Content.ShouldBe("Second");
        })],
        ["Groups by MessageId preserving order", new List<ChatStreamMessage>
        {
            new() { Content = "A1", MessageId = "msg-a" },
            new() { Content = "A2", MessageId = "msg-a" },
            new() { IsComplete = true, MessageId = "msg-a" },
            new() { Content = "B1", MessageId = "msg-b" },
            new() { Content = "B2", MessageId = "msg-b" }
        }, new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
        {
            completed.Count.ShouldBe(1);
            completed[0].Content.ShouldBe("A1A2");
            streaming.Content.ShouldBe("B1B2");
        })],
        ["Skips empty completed turns", new List<ChatStreamMessage>
        {
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second turn", MessageId = "msg-2" }
        }, new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
        {
            completed.ShouldBeEmpty();
            streaming.Content.ShouldBe("Second turn");
        })]
    ];

    [Theory]
    [MemberData(nameof(RebuildBasicStateCases))]
    public void RebuildFromBuffer_BasicStates(
        string _, List<ChatStreamMessage> buffer, Action<List<ChatMessageModel>, ChatMessageModel> assert)
    {
        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        assert(completedTurns, streamingMessage);
    }

    public static IEnumerable<object[]> NonContentAccumulationCases =>
    [
        ["Reasoning accumulates alongside content", new List<ChatStreamMessage>
        {
            new() { Reasoning = "Thinking...", MessageId = "msg-1" },
            new() { Content = "Answer", MessageId = "msg-1" }
        }, new Action<ChatMessageModel>(streaming =>
        {
            streaming.Reasoning.ShouldBe("Thinking...");
            streaming.Content.ShouldBe("Answer");
        })],
        ["ToolCalls accumulate with newline separator", new List<ChatStreamMessage>
        {
            new() { ToolCalls = "tool_1", MessageId = "msg-1" },
            new() { ToolCalls = "tool_2", MessageId = "msg-1" }
        }, new Action<ChatMessageModel>(streaming => streaming.ToolCalls.ShouldBe("tool_1\ntool_2"))]
    ];

    [Theory]
    [MemberData(nameof(NonContentAccumulationCases))]
    public void RebuildFromBuffer_AccumulatesNonContentFields(
        string _, List<ChatStreamMessage> buffer, Action<ChatMessageModel> assert)
    {
        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        assert(streamingMessage);
    }

    public static IEnumerable<object?[]> TimestampCases
    {
        get
        {
            var ts1 = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var ts2 = new DateTimeOffset(2025, 6, 15, 12, 0, 5, TimeSpan.Zero);
            yield return
            [
                "First chunk has timestamp, second does not -> carried forward",
                new List<ChatStreamMessage>
                {
                    new() { Content = "Hello", MessageId = "msg-1", Timestamp = ts1 },
                    new() { Content = " world", MessageId = "msg-1" }
                },
                (DateTimeOffset?)ts1
            ];
            yield return
            [
                "Both chunks have timestamps -> last wins",
                new List<ChatStreamMessage>
                {
                    new() { Content = "Hello", MessageId = "msg-1", Timestamp = ts1 },
                    new() { Content = " world", MessageId = "msg-1", Timestamp = ts2 }
                },
                (DateTimeOffset?)ts2
            ];
            yield return
            [
                "No timestamps -> remains null",
                new List<ChatStreamMessage>
                {
                    new() { Content = "Hello", MessageId = "msg-1" }
                },
                (DateTimeOffset?)null
            ];
        }
    }

    [Theory]
    [MemberData(nameof(TimestampCases))]
    public void RebuildFromBuffer_StreamingMessageTimestamp(
        string _, List<ChatStreamMessage> buffer, DateTimeOffset? expected)
    {
        var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        streamingMessage.Timestamp.ShouldBe(expected);
    }

    [Fact]
    public void RebuildFromBuffer_WithUserMessage_IncludesInCompletedTurns()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Hello from user", UserMessage = new UserMessageInfo("alice", null) },
            new() { Content = "Hi there!", MessageId = "msg-1" }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(1);
        completedTurns[0].Role.ShouldBe("user");
        completedTurns[0].Content.ShouldBe("Hello from user");
        completedTurns[0].SenderId.ShouldBe("alice");
        streamingMessage.Content.ShouldBe("Hi there!");
    }

    [Fact]
    public void RebuildFromBuffer_WithMixedMessages_PreservesChronologicalOrder()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "User msg 1", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1 },
            new() { Content = "Assistant response 1", MessageId = "msg-1", SequenceNumber = 2 },
            new() { IsComplete = true, MessageId = "msg-1", SequenceNumber = 3 },
            new() { Content = "User msg 2", UserMessage = new UserMessageInfo("bob", null), SequenceNumber = 4 },
            new() { Content = "Assistant response 2", MessageId = "msg-2", SequenceNumber = 5 }
        };

        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns.Count.ShouldBe(3);
        completedTurns[0].Role.ShouldBe("user");
        completedTurns[0].Content.ShouldBe("User msg 1");
        completedTurns[1].Role.ShouldBe("assistant");
        completedTurns[1].Content.ShouldBe("Assistant response 1");
        completedTurns[2].Role.ShouldBe("user");
        completedTurns[2].Content.ShouldBe("User msg 2");
        streamingMessage.Content.ShouldBe("Assistant response 2");
    }

    public static IEnumerable<object?[]> MessageIdPropagationCases =>
    [
        ["Assistant turn propagates MessageId", new List<ChatStreamMessage>
        {
            new() { Content = "First", MessageId = "msg-1" },
            new() { IsComplete = true, MessageId = "msg-1" },
            new() { Content = "Second", MessageId = "msg-2" }
        }, (string?)"msg-1"],
        ["User message has null MessageId", new List<ChatStreamMessage>
        {
            new() { Content = "Hello", UserMessage = new UserMessageInfo("alice", null) }
        }, (string?)null]
    ];

    [Theory]
    [MemberData(nameof(MessageIdPropagationCases))]
    public void RebuildFromBuffer_MessageIdPropagation(
        string _, List<ChatStreamMessage> buffer, string? expectedFirstCompletedId)
    {
        var (completedTurns, _) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completedTurns[0].MessageId.ShouldBe(expectedFirstCompletedId);
    }

    #endregion

    #region Same-MessageId IsComplete Split

    // The interleaved-messageId race (see memory: project_webchat_interleaved_messageid_bubble_loss)
    // makes these three scenarios behaviourally distinct; they are kept as separate cases so a
    // regression in any one variant is pinpointed individually.
    public static IEnumerable<object[]> SameMessageIdIsCompleteSplitCases =>
    [
        [
            "Same MessageId, all IsComplete -> single completed message",
            new List<ChatStreamMessage>
            {
                new() { Reasoning = "Thinking...", MessageId = "msg-1", SequenceNumber = 1 },
                new() { ToolCalls = "search(query)", MessageId = "msg-1", IsComplete = true, SequenceNumber = 2 },
                new()
                {
                    Content = "Here is the answer", MessageId = "msg-1", IsComplete = true, SequenceNumber = 3
                }
            },
            new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
            {
                completed.Count.ShouldBe(1);
                completed[0].Reasoning.ShouldBe("Thinking...");
                completed[0].ToolCalls.ShouldBe("search(query)");
                completed[0].Content.ShouldBe("Here is the answer");
                completed[0].MessageId.ShouldBe("msg-1");
                streaming.HasContent.ShouldBeFalse();
            })
        ],
        [
            "Same MessageId, last chunk not complete -> stays streaming",
            new List<ChatStreamMessage>
            {
                new() { ToolCalls = "tool1", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1 },
                new() { Content = "partial answer", MessageId = "msg-1", SequenceNumber = 2 }
            },
            new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
            {
                completed.ShouldBeEmpty();
                streaming.ToolCalls.ShouldBe("tool1");
                streaming.Content.ShouldBe("partial answer");
                streaming.MessageId.ShouldBe("msg-1");
            })
        ],
        [
            "Different MessageIds, both complete -> two completed messages (unchanged behaviour)",
            new List<ChatStreamMessage>
            {
                new()
                {
                    Reasoning = "R1", ToolCalls = "TC1", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1
                },
                new()
                {
                    Reasoning = "R2", Content = "Answer", MessageId = "msg-2", IsComplete = true, SequenceNumber = 2
                }
            },
            new Action<List<ChatMessageModel>, ChatMessageModel>((completed, streaming) =>
            {
                completed.Count.ShouldBe(2);
                completed[0].Reasoning.ShouldBe("R1");
                completed[0].ToolCalls.ShouldBe("TC1");
                completed[0].MessageId.ShouldBe("msg-1");
                completed[1].Reasoning.ShouldBe("R2");
                completed[1].Content.ShouldBe("Answer");
                completed[1].MessageId.ShouldBe("msg-2");
                streaming.HasContent.ShouldBeFalse();
            })
        ]
    ];

    [Theory]
    [MemberData(nameof(SameMessageIdIsCompleteSplitCases))]
    public void RebuildFromBuffer_SameMessageIdIsCompleteSplit(
        string _, List<ChatStreamMessage> buffer, Action<List<ChatMessageModel>, ChatMessageModel> assert)
    {
        var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        assert(completedTurns, streamingMessage);
    }

    #endregion

    #region ResumeFromBuffer

    public static IEnumerable<object[]> ResumeEmptySideCases =>
    [
        [
            "Empty buffer + non-empty history -> history unchanged, no streaming",
            new List<ChatStreamMessage>(),
            new List<ChatMessageModel>
            {
                new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
                new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
            },
            new Action<BufferResumeResult>(result =>
            {
                result.MergedMessages.Count.ShouldBe(2);
                result.MergedMessages[0].Content.ShouldBe("Q1");
                result.MergedMessages[1].Content.ShouldBe("A1");
                result.StreamingMessage.HasContent.ShouldBeFalse();
            })
        ],
        [
            "Empty history + buffer -> buffer turns surface as merged/streaming",
            new List<ChatStreamMessage>
            {
                new() { Content = "First", MessageId = "msg-1", IsComplete = true, SequenceNumber = 1 },
                new() { Content = "Second", MessageId = "msg-2", SequenceNumber = 2 }
            },
            new List<ChatMessageModel>(),
            new Action<BufferResumeResult>(result =>
            {
                result.MergedMessages.Count.ShouldBe(1);
                result.MergedMessages[0].Content.ShouldBe("First");
                result.StreamingMessage.Content.ShouldBe("Second");
            })
        ]
    ];

    [Theory]
    [MemberData(nameof(ResumeEmptySideCases))]
    public void ResumeFromBuffer_EmptySideBehaviour(
        string _,
        List<ChatStreamMessage> buffer,
        List<ChatMessageModel> history,
        Action<BufferResumeResult> assert)
    {
        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

        assert(result);
    }

    // The prompt a stream is resumed under is drawn from the stream's state, which carries no time;
    // the buffered user message beside it does. A service's turn ("You finished logging in") drew
    // its notice with no time until a reload read it from history.
    [Fact]
    public void ResumeFromBuffer_TheCurrentPrompt_KeepsTheTimeAndNameItWasSentWith()
    {
        var sentAt = new DateTimeOffset(2026, 11, 3, 17, 18, 0, TimeSpan.Zero);
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "[login finished]", UserMessage = new UserMessageInfo("login", sentAt, "Login"), SequenceNumber = 1 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, [], "[login finished]", "login");

        var prompt = result.MergedMessages.ShouldHaveSingleItem();
        prompt.Timestamp.ShouldBe(sentAt);
        prompt.SenderName.ShouldBe("Login");
    }

    // Two people sending in a shared topic at once put both their messages on one stream, and the
    // stream's state names one prompt — whichever send came last. A page resuming it drew only that
    // one, so Ana, whose own message it was, never saw Carlos's until a reload.
    [Fact]
    public void ResumeFromBuffer_EveryMessageTheStreamCarried_IsDrawnOnceAndInItsOrder()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Yo pongo el vino.", UserMessage = new UserMessageInfo("carlos", null, "Carlos") },
            new() { Content = "Yo traigo el postre.", UserMessage = new UserMessageInfo("ana", null, "Ana") },
            new() { Content = "Apuntado el vino.", MessageId = "m1", SequenceNumber = 1 },
            new() { Content = "Apuntado el postre.", MessageId = "m2", SequenceNumber = 2 }
        };
        List<ChatMessageModel> page =
        [
            new() { Role = "user", Content = "Yo traigo el postre.", SenderId = "ana" }
        ];

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, page, "Yo traigo el postre.", "ana");

        result.MergedMessages.Where(m => m.Role == "user").Select(m => (m.SenderId, m.Content)).ShouldBe(
            [("ana", "Yo traigo el postre."), ("carlos", "Yo pongo el vino.")]);
    }

    // The same words sent again are a message of their own: the page shows the first "ok", the stream
    // carries the second.
    [Fact]
    public void ResumeFromBuffer_TheSameWordsSentAgain_AreDrawnAgain()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "ok", UserMessage = new UserMessageInfo("carlos", null, "Carlos", "second") }
        };
        List<ChatMessageModel> page = [new() { Role = "user", Content = "ok", SenderId = "carlos", CorrelationId = "first" }];

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, page, "ok", "carlos");

        result.MergedMessages.Count(m => m.Role == "user").ShouldBe(2);
    }

    public static IEnumerable<object[]> AnchorPositioningCases
    {
        get
        {
            var twoTurnHistory = new List<ChatMessageModel>
            {
                new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
                new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
            };
            yield return
            [
                "Interleaves new message between two anchors",
                new List<ChatMessageModel>
                {
                    new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
                    new() { Role = "assistant", Content = "A1", MessageId = "msg-2" },
                    new() { Role = "user", Content = "Q2", MessageId = "msg-3" },
                    new() { Role = "assistant", Content = "A2", MessageId = "msg-4" }
                },
                new List<ChatStreamMessage>
                {
                    new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
                    new() { Content = "New message", IsComplete = true, SequenceNumber = 2 },
                    new() { MessageId = "msg-4", Content = "A2", IsComplete = true, SequenceNumber = 3 }
                },
                new Action<BufferResumeResult>(result =>
                {
                    result.MergedMessages.Count.ShouldBe(5);
                    result.MergedMessages[0].Content.ShouldBe("Q1");
                    result.MergedMessages[1].Content.ShouldBe("A1");
                    result.MergedMessages[2].Content.ShouldBe("New message");
                    result.MergedMessages[3].Content.ShouldBe("Q2");
                    result.MergedMessages[4].Content.ShouldBe("A2");
                })
            ];
            yield return
            [
                "Leading new messages appear before first anchor",
                twoTurnHistory,
                new List<ChatStreamMessage>
                {
                    new() { Content = "Leading new", IsComplete = true, SequenceNumber = 1 },
                    new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 2 }
                },
                new Action<BufferResumeResult>(result =>
                {
                    result.MergedMessages.Count.ShouldBe(3);
                    result.MergedMessages[0].Content.ShouldBe("Q1");
                    result.MergedMessages[1].Content.ShouldBe("Leading new");
                    result.MergedMessages[2].Content.ShouldBe("A1");
                })
            ];
            yield return
            [
                "Trailing new messages appear after last anchor",
                twoTurnHistory,
                new List<ChatStreamMessage>
                {
                    new() { MessageId = "msg-2", Content = "A1", IsComplete = true, SequenceNumber = 1 },
                    new() { Content = "Trailing new", IsComplete = true, SequenceNumber = 2 }
                },
                new Action<BufferResumeResult>(result =>
                {
                    result.MergedMessages.Count.ShouldBe(3);
                    result.MergedMessages[0].Content.ShouldBe("Q1");
                    result.MergedMessages[1].Content.ShouldBe("A1");
                    result.MergedMessages[2].Content.ShouldBe("Trailing new");
                })
            ];
            yield return
            [
                "No anchors -> appends all new messages at end",
                twoTurnHistory,
                new List<ChatStreamMessage>
                {
                    new() { Content = "New1", IsComplete = true, SequenceNumber = 1 },
                    new() { Content = "New2", IsComplete = true, SequenceNumber = 2 }
                },
                new Action<BufferResumeResult>(result =>
                {
                    result.MergedMessages.Count.ShouldBe(4);
                    result.MergedMessages[2].Content.ShouldBe("New1");
                    result.MergedMessages[3].Content.ShouldBe("New2");
                })
            ];
        }
    }

    [Theory]
    [MemberData(nameof(AnchorPositioningCases))]
    public void ResumeFromBuffer_AnchorPositioning(
        string _,
        List<ChatMessageModel> history,
        List<ChatStreamMessage> buffer,
        Action<BufferResumeResult> assert)
    {
        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

        assert(result);
    }

    [Fact]
    public void ResumeFromBuffer_MergesReasoningIntoAnchor()
    {
        var history = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "A1", MessageId = "msg-1" }
        };

        var buffer = new List<ChatStreamMessage>
        {
            new()
            {
                MessageId = "msg-1", Content = "A1", Reasoning = "Thought process", IsComplete = true,
                SequenceNumber = 1
            }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

        result.MergedMessages.Count.ShouldBe(1);
        result.MergedMessages[0].Content.ShouldBe("A1");
        result.MergedMessages[0].Reasoning.ShouldBe("Thought process");
    }

    [Fact]
    public void ResumeFromBuffer_StripsStreamingMessageContentAgainstHistory()
    {
        var history = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Already known content", MessageId = "msg-1" }
        };

        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "Already known content", MessageId = "msg-1", SequenceNumber = 1 }
        };

        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, null, null);

        result.StreamingMessage.Content.ShouldBeEmpty();
    }

    public static IEnumerable<object?[]> PromptHandlingCases =>
    [
        [
            "Adds current prompt when missing from history",
            new List<ChatMessageModel>
            {
                new() { Role = "assistant", Content = "Previous", MessageId = "msg-1" }
            },
            new List<ChatStreamMessage>
            {
                new() { MessageId = "msg-1", Content = "Previous", IsComplete = true, SequenceNumber = 1 }
            },
            "New question",
            (string?)"alice",
            new Action<BufferResumeResult>(result =>
            {
                result.MergedMessages.Count.ShouldBe(2);
                result.MergedMessages[1].Role.ShouldBe("user");
                result.MergedMessages[1].Content.ShouldBe("New question");
                result.MergedMessages[1].SenderId.ShouldBe("alice");
            })
        ],
        [
            "Does not duplicate prompt already present in history",
            new List<ChatMessageModel> { new() { Role = "user", Content = "Same prompt" } },
            new List<ChatStreamMessage>(),
            "Same prompt",
            (string?)null,
            new Action<BufferResumeResult>(result =>
            {
                var promptCount = result.MergedMessages
                    .Count(m => m is { Role: "user", Content: "Same prompt" });
                promptCount.ShouldBe(1);
            })
        ],
        [
            "Excludes current prompt that appears as buffered user message",
            new List<ChatMessageModel>(),
            new List<ChatStreamMessage>
            {
                new()
                {
                    Content = "User's question", UserMessage = new UserMessageInfo("Bob", null), SequenceNumber = 1
                },
                new() { Content = "Response", MessageId = "msg-1", SequenceNumber = 2 }
            },
            "User's question",
            (string?)"Bob",
            new Action<BufferResumeResult>(result =>
            {
                var promptCount = result.MergedMessages
                    .Count(m => m is { Role: "user", Content: "User's question" });
                promptCount.ShouldBe(1);
            })
        ],
        [
            "Current prompt is added before unanchored buffer content",
            new List<ChatMessageModel>
            {
                new() { Role = "user", Content = "Q1", MessageId = "msg-1" },
                new() { Role = "assistant", Content = "A1", MessageId = "msg-2" }
            },
            new List<ChatStreamMessage>
            {
                new()
                {
                    Content = "Response to new question", MessageId = "msg-3", IsComplete = true, SequenceNumber = 1
                },
                new() { Content = "Still responding", MessageId = "msg-4", SequenceNumber = 2 }
            },
            "New question",
            (string?)"alice",
            new Action<BufferResumeResult>(result =>
            {
                result.MergedMessages.Count.ShouldBe(4);
                result.MergedMessages[0].Content.ShouldBe("Q1");
                result.MergedMessages[1].Content.ShouldBe("A1");
                result.MergedMessages[2].Role.ShouldBe("user");
                result.MergedMessages[2].Content.ShouldBe("New question");
                result.MergedMessages[3].Content.ShouldBe("Response to new question");
                result.StreamingMessage.Content.ShouldBe("Still responding");
            })
        ]
    ];

    [Theory]
    [MemberData(nameof(PromptHandlingCases))]
    public void ResumeFromBuffer_PromptHandling(
        string _,
        List<ChatMessageModel> history,
        List<ChatStreamMessage> buffer,
        string? currentPrompt,
        string? currentSenderId,
        Action<BufferResumeResult> assert)
    {
        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, currentPrompt, currentSenderId);

        assert(result);
    }

    public static IEnumerable<object[]> UserMessageDedupCases =>
    [
        [
            // Multi-turn topic reconnects with buffer containing previous user messages.
            // The first user message is in both history (with ID) and buffer (without ID);
            // it should NOT be duplicated in the merged result.
            "Clean history, buffered user messages not duplicated across reconnect",
            new List<ChatMessageModel>
            {
                new() { Role = "user", Content = "First question", MessageId = "user-1" },
                new() { Role = "assistant", Content = "First answer", MessageId = "asst-1" }
            },
            new List<ChatStreamMessage>
            {
                new()
                {
                    Content = "First question", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1
                },
                new() { Content = "First answer", MessageId = "asst-1", IsComplete = true, SequenceNumber = 2 },
                new()
                {
                    Content = "Second question", UserMessage = new UserMessageInfo("alice", null),
                    SequenceNumber = 3
                },
                new() { Content = "Streaming response", MessageId = "asst-2", SequenceNumber = 4 }
            },
            "Second question",
            "alice",
            new Action<BufferResumeResult>(result =>
            {
                var firstQuestionCount = result.MergedMessages
                    .Count(m => m is { Role: "user", Content: "First question" });
                firstQuestionCount.ShouldBe(1);
                var secondQuestionCount = result.MergedMessages
                    .Count(m => m is { Role: "user", Content: "Second question" });
                secondQuestionCount.ShouldBe(1);
                result.MergedMessages.Count.ShouldBe(3);
                result.StreamingMessage.Content.ShouldBe("Streaming response");
            })
        ],
        [
            // Dirty history (e.g. left over from an earlier bad merge): buffer user messages
            // must not push the count higher.
            "Dirty history, buffer user messages don't make the duplicate count grow",
            new List<ChatMessageModel>
            {
                new() { Role = "user", Content = "Hello", MessageId = "user-1" },
                new() { Role = "user", Content = "Hello" }, // leftover from previous bad merge
                new() { Role = "assistant", Content = "Hi there", MessageId = "asst-1" }
            },
            new List<ChatStreamMessage>
            {
                new() { Content = "Hello", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 1 },
                new() { Content = "Hi there", MessageId = "asst-1", IsComplete = true, SequenceNumber = 2 },
                new()
                {
                    Content = "New question", UserMessage = new UserMessageInfo("alice", null), SequenceNumber = 3
                },
                new() { Content = "Response", MessageId = "asst-2", SequenceNumber = 4 }
            },
            "New question",
            "alice",
            new Action<BufferResumeResult>(result =>
            {
                var helloCount = result.MergedMessages
                    .Count(m => m is { Role: "user", Content: "Hello" });
                helloCount.ShouldBeLessThanOrEqualTo(2);
            })
        ]
    ];

    [Theory]
    [MemberData(nameof(UserMessageDedupCases))]
    public void ResumeFromBuffer_DoesNotDuplicateUserMessages(
        string _,
        List<ChatMessageModel> history,
        List<ChatStreamMessage> buffer,
        string currentPrompt,
        string currentSenderId,
        Action<BufferResumeResult> assert)
    {
        var result = BufferRebuildUtility.ResumeFromBuffer(buffer, history, currentPrompt, currentSenderId);

        assert(result);
    }

    #endregion

    // Another person's message in a shared topic arrives on the stream with the name they sent it
    // under, which is how the page draws them.
    [Fact]
    public void RebuildFromBuffer_AUserMessage_KeepsItsSendersName()
    {
        var buffer = new List<ChatStreamMessage>
        {
            new() { Content = "hola", UserMessage = new UserMessageInfo("u-2", null, "Marta"), SequenceNumber = 1 }
        };

        var (completed, _) = BufferRebuildUtility.RebuildFromBuffer(buffer);

        completed.Single().SenderName.ShouldBe("Marta");
    }
}