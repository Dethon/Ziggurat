using System.Reactive.Disposables;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// The moments a page may have missed what the hub told about the topic it shows: opening it, since a push
// reaches a page only from when it arrived, and coming back live on it (a phone asleep), which opens
// nothing. Shared so whatever catches up on a topic asks at the same moments.
public static class ShownTopicCatchUp
{
    public static IDisposable Subscribe(
        Dispatcher dispatcher, TopicsStore topics, ConnectionStore connection, Action<string, string> catchUp) =>
        new CompositeDisposable(
            dispatcher.RegisterHandler<SelectTopic>(a =>
            {
                if (a.TopicId is { } topicId)
                {
                    catchUp(topicId, nameof(SelectTopic));
                }
            }),
            connection.BecameLiveAgain.Subscribe(_ =>
            {
                if (topics.State.SelectedTopicId is { } topicId)
                {
                    catchUp(topicId, nameof(ConnectionStore.BecameLiveAgain));
                }
            }));
}