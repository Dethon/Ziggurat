using Microsoft.Extensions.Configuration;

namespace Tests.Eval;

// The suite spends money against a real provider, so it is armed deliberately and never by being
// present. Unlike the LLM category — which runs whenever a key is configured — an eval run must
// be asked for: a routine `dotnet test` that quietly billed somebody would be a bug in the suite.
public static class EvalGate
{
    public const string Variable = "ZIGGURAT_EVAL";

    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets(typeof(EvalGate).Assembly)
        .Build();

    public static bool IsArmed =>
        Environment.GetEnvironmentVariable(Variable) is "1" or "true";

    public static string Reason =>
        $"The behavioural eval is opt-in: set {Variable}=1 and filter on Category=Eval. " +
        "It drives a real model and costs money.";

    // From user secrets, exactly as the LLM-category tests read it: a machine with no key cannot
    // run this suite, and saying so is more useful than a provider error three layers down.
    public static string? ApiKey => _configuration["openRouter:apiKey"];
}