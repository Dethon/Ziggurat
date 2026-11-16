using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

// Read as the app starts, before routing can move the address: correcting a removed space's slug
// navigates, and the query would go with it.
public sealed class LaunchLink(NavigationManager navigation) : ILaunchLink
{
    public LaunchTopic? Topic { get; private set; } = Read(navigation.Uri);

    public void Spend()
    {
        if (Topic is null)
        {
            return;
        }

        Topic = null;
        var path = navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0];
        navigation.NavigateTo($"/{path}", replace: true);
    }

    private static LaunchTopic? Read(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0])
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));

        return query.GetValueOrDefault("agent") is { Length: > 0 } agent
               && query.GetValueOrDefault("topic") is { Length: > 0 } topic
            ? new LaunchTopic(agent, topic)
            : null;
    }
}