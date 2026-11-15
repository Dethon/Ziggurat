namespace Domain.DTOs.WebChat;

// What a push says and where a tap on it leads. Url is the space's page, which the service worker
// also compares with the page on screen to stay quiet there; Topic names the conversation a reply
// finished in, so a tap opens it rather than whatever the app had open before.
public sealed record PushNotice(string Title, string Body, string Url, PushTopic? Topic = null);

// A topic is listed per agent, so the app needs both to find it.
public sealed record PushTopic(string AgentId, string TopicId);