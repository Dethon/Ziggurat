namespace Domain.Prompts;

// The vault's standing prose (Sections/vault_prompt.md), served by mcp-vault.
public static class VaultPrompt
{
    public const string Name = "vault_prompt";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Prompt = _document.Text;

    public static readonly PromptClaim TransferIsOneCall =
        _document.Claim("vault.transfer-is-one-call");

    public static readonly PromptClaim ScriptsRunInPlace =
        _document.Claim("vault.scripts-run-in-place");
}