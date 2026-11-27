using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

public enum ActivityKind
{
    Reasoning,
    Words,
    Tool
}

// One thing a reply did on its way to its answer, as the page draws it: reasoning, words it said before
// carrying on (interim words), or a tool call with its answer.
public sealed record ActivityStep(ActivityKind Kind, string? Text = null, ToolCallStep? Tool = null);

// How the page draws a conversation's replies: everything a reply did between the person's message and
// its answer — reasoning, interim words, every tool call — is one group of activity, and the answer is
// its own bubble. A render-only projection of the stored messages, which stay one per provider message
// (what streams, what entry ids name, what rewrites read), so every index the page's actions take is an
// index into these rows, one per bubble as before.
//
// A run of assistant messages is one reply. Its answer is words nothing followed: words a call came
// after are interim, and a reply cut before it spoke again (a steer, a stop) is a group with no answer.
// A person's message ends a run, which is how a steer splits one reply's activity in two. An error is a
// row of its own. Words right after words with no tool call between them are two replies (a fire
// answered beside the person's turn), unless the second carries the first on (a Continue), when they are
// one answer, named as the reply it continues.
public static class TurnLayout
{
    public static IReadOnlyList<ChatMessageModel> Rows(IReadOnlyList<ChatMessageModel> messages)
    {
        var rows = new List<ChatMessageModel>(messages.Count);
        var run = new Run();
        foreach (var message in messages)
        {
            if (message is not { Role: "assistant", NoReply: false, IsError: false })
            {
                run.FlushInto(rows);
                rows.Add(message);
                continue;
            }

            if (run.StartsAnotherReply(message))
            {
                run.FlushInto(rows);
            }

            run.Add(message);
        }

        run.FlushInto(rows);
        return rows;
    }

    // The key a reply's group is remembered by while it streams and once it is kept: its first message's
    // provider id, which the stream and the kept bubble share.
    public static string KeyOf(ChatMessageModel row, int index) =>
        row.Activity is { Count: > 0 } && row.RunKey is { } key ? key : $"row:{index}";

    private sealed class Run
    {
        private readonly List<ChatMessageModel> _messages = [];
        private readonly List<Step> _steps = [];

        // Answers whose calls are not drawn yet: a turn's answers all come under one id, kept where the
        // first of them was drawn, ahead of the later calls they answer.
        private readonly Dictionary<string, ToolCallStep> _early = [];

        private sealed record Step(ActivityStep Activity, ChatMessageModel? Source, bool Interrupted);

        public bool StartsAnotherReply(ChatMessageModel message) =>
            !string.IsNullOrEmpty(message.Content)
            && !message.ContinuesPrevious
            && message.Steps?.Any(s => s.Tool is not null) != true
            && _steps.FindLastIndex(s => s.Activity.Kind == ActivityKind.Words) is var words and >= 0
            && !_steps.Skip(words + 1).Any(s => s.Activity.Kind == ActivityKind.Tool);

        public void Add(ChatMessageModel message)
        {
            _messages.Add(message);
            AddSteps(message.Steps);
            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                _steps.Add(new Step(new ActivityStep(ActivityKind.Reasoning, message.Reasoning), null, false));
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                AddWords(message);
            }

            foreach (var tool in message.Tools ?? [])
            {
                AddTool(tool);
            }

            AddSteps(message.StepsAfter);
        }

        // What followed a Continue is the rest of the words it continues: joined to them, moved to where
        // the rest was said, and named as the reply it continues.
        private void AddWords(ChatMessageModel message)
        {
            var previous = _steps.FindLastIndex(s => s.Activity.Kind == ActivityKind.Words);
            if (message.ContinuesPrevious && previous >= 0)
            {
                var continued = _steps[previous];
                _steps.RemoveAt(previous);
                _steps.Add(continued with
                {
                    Activity = continued.Activity with { Text = continued.Activity.Text + message.Content },
                    Interrupted = message.IsInterrupted
                });
                return;
            }

            _steps.Add(new Step(new ActivityStep(ActivityKind.Words, message.Content), message, message.IsInterrupted));
        }

        private void AddSteps(IReadOnlyList<TurnStep>? steps)
        {
            foreach (var step in steps ?? [])
            {
                if (step.Tool is { } tool)
                {
                    AddTool(tool);
                }
                else if (!string.IsNullOrWhiteSpace(step.Reasoning))
                {
                    _steps.Add(new Step(new ActivityStep(ActivityKind.Reasoning, step.Reasoning), null, false));
                }
            }
        }

        // A call's answer folds into the call wherever it was drawn. An answer met before its call waits for
        // it, so each call is drawn where it was made; one whose call never comes is a step of its own at the
        // end (FlushInto).
        private void AddTool(ToolCallStep tool)
        {
            var at = _steps.FindIndex(s => s.Activity.Tool?.CallId == tool.CallId);
            if (at < 0 && tool.Name is null)
            {
                _early[tool.CallId] = _early.TryGetValue(tool.CallId, out var held) ? held.Merge(tool) : tool;
                return;
            }

            if (at < 0)
            {
                var call = _early.Remove(tool.CallId, out var answer) ? tool.Merge(answer) : tool;
                _steps.Add(new Step(new ActivityStep(ActivityKind.Tool, Tool: call), null, false));
                return;
            }

            var existing = _steps[at];
            _steps[at] = existing with { Activity = existing.Activity with { Tool = existing.Activity.Tool!.Merge(tool) } };
        }

        public void FlushInto(List<ChatMessageModel> rows)
        {
            if (_messages.Count == 0)
            {
                return;
            }

            // Answers whose calls never came go before the reply's closing words, never after them.
            var closing = _steps is [.., { Activity.Kind: ActivityKind.Words }] ? 1 : 0;
            _steps.InsertRange(_steps.Count - closing,
                _early.Values.Select(answer => new Step(new ActivityStep(ActivityKind.Tool, Tool: answer), null, false)));
            _early.Clear();

            var answer = _steps is [.., { Activity.Kind: ActivityKind.Words } last] ? last : null;
            var activity = (answer is null ? _steps : _steps.Take(_steps.Count - 1))
                .Select(s => s.Activity)
                .ToList();
            var anchor = answer?.Source
                         ?? _messages.LastOrDefault(m => m.EntryId is not null)
                         ?? _messages[^1];

            rows.Add(anchor with
            {
                Content = answer?.Activity.Text ?? "",
                IsInterrupted = answer?.Interrupted ?? anchor.IsInterrupted,
                Reasoning = null,
                Tools = null,
                Steps = null,
                StepsAfter = null,
                Activity = activity,
                RunKey = _messages[0].MessageId ?? _messages[0].EntryId,
                Versions = _messages.Select(m => m.Versions).FirstOrDefault(v => v is not null),
                ToolsCalled = anchor.ToolsCalled ?? _messages.Select(m => m.ToolsCalled).LastOrDefault(t => t is not null),
                Attachments = _messages.SelectMany(m => m.Attachments ?? []).ToList() is { Count: > 0 } files ? files : null
            });

            _messages.Clear();
            _steps.Clear();
        }
    }
}