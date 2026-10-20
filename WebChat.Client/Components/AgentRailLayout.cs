namespace WebChat.Client.Components;

// The desktop rail is a fixed 320px: three segments fit with a name beside each monogram, five with
// the name under it. Past five the rows are evened out, since a lone agent wrapped onto a row of its
// own reads as a different control from the ones above it.
public sealed record AgentRailLayout(int Columns, bool Stacked)
{
    private const int _besideMax = 3;
    private const int _stackedMax = 5;

    public static AgentRailLayout For(int agents)
    {
        if (agents <= _besideMax)
        {
            return new AgentRailLayout(Math.Max(agents, 1), Stacked: false);
        }

        var rows = (agents + _stackedMax - 1) / _stackedMax;
        return new AgentRailLayout((agents + rows - 1) / rows, Stacked: true);
    }
}