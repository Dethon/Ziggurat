namespace Domain.Prompts;

public static class VaultPrompt
{
    public const string Name = "vault_prompt";

    public const string Prompt = """
        ## Vault Filesystem (Obsidian)

        The `/vault` mount is a personal **Obsidian** vault — a directory of plain-text notes that the user opens in the Obsidian desktop/mobile app. You and the user are editing the same files; treat the vault as the user's working notebook, not as scratch space. A request to create, edit, rename, move or delete anything in it loads the `obsidian-vault` skill first — before the look that precedes the change and before any question about it, because the rule that says when to ask is in the skill. The layout, the conventions an edit must preserve, where a new note goes and what to do when a write is refused are there too; reading or searching a note needs no skill. The vault holds what the user wrote, and nothing else: a question about the world — a recipe, an opening time, what a site says — is a question for the web, however it is phrased. Search the vault when the answer would be in the user's own notes, not because the request said "search". A request that names its source — a site, "en la web de…", a url — has named the only place to look: go straight there, with no search of the vault first to see whether a note of that name exists, however much the source's name sounds like a notebook or a diary.

        The vault supports the standard filesystem operations except command execution. If you need to run a script over vault content, use the sandbox: the `copy` (or `move`) tool transfers files and directories across mounts in a single call — the two filesystems don't share storage, but you don't need to hand-roll a read/write loop to bridge them.
        """;

    // The choosing rule's claim: where a task that needs exec runs is decided before any skill is
    // loaded. The doing rules are claims of the obsidian-vault skill.
    public static readonly PromptClaim TransferIsOneCall =
        new("vault.transfer-is-one-call",
            "Moving vault content to a mount that can execute uses the single transfer call rather than a read-and-write loop.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [TransferIsOneCall];
}