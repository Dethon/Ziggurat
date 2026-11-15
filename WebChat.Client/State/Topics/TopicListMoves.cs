using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public static class TopicListMoves
{
    // A conversation started while the list shows another person's is the starter's own, so the list
    // moves to theirs (user's decision, 2026-11-15) rather than leave it open with no row. Asked once
    // the server holds it: a save is not announced, so a list read before it would come back without
    // the new conversation.
    public static void ToTheStartersOwn(StoredTopic started, TopicsState state, IDispatcher dispatcher)
    {
        if (!state.Lists(started))
        {
            dispatcher.Dispatch(new ChooseTopicFilter(TopicFilter.Mine));
        }
    }
}