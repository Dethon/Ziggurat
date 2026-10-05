namespace Domain.Outposts;

// Where the agent addresses a machine that registered itself. Not a branch of the agent's own tree:
// an outpost is somebody's separate computer, so it gets an address of its own kind, and a machine
// that names itself after a mount — "vault" — is a different address rather than a rival claim on
// the vault's. One spelling for both ends that compose it: the outpost publishing its own mount
// point, and the agent naming a machine that registered and then did not answer.
public static class OutpostMountPoint
{
    public const string Scheme = "outpost:";

    public static string For(string name) => Scheme + name;

    // Case-insensitive, like every other mount point the registry matches.
    public static bool Addresses(string path) => path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
}