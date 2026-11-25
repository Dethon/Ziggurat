namespace WebChat.Client.Components.Admin;

// What the features page says the features with a catch do: Sugary's log is each person's, a timer
// rings only where the household runs a voice server, and apps are the operator's to turn on.
public static class FeatureHints
{
    public const string Timers =
        "Agents start countdown timers that ring on the household's voice satellites; a household with no voice server gets them as a message when they are due.";

    public const string Sugary =
        "Agents read and write each person's own Sugary log of meals and doses; the pantry and recipes are the household's.";

    public const string Apps =
        "Agents build small web apps for the household's people. Only the deployment's operator turns it on or off.";
}