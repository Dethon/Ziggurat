using Domain.Security.Signing;

namespace Tests;

// The signing keys every test and eval stack runs its issuers with, so a fixture, the services it
// starts and a test's own verifier agree without provisioning anything, and a run is the same run
// twice. One per issuer, so a token one of them signed is a foreign key to whoever trusts the other.
//
// Private keys in the clear, which is why DeploymentConformanceTests holds every file that ships to
// containing neither: a deployment generates its own on first start, in each issuer's volume.
internal static class TestSigningKeys
{
    // P-256, PKCS#8 DER in base64: the form SigningKeySettings.PrivateKey takes, one line so a
    // fixture can hand it to a container as an environment variable.
    public const string Accounts =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgBCZSOm1f3AjmrWnVbvDZPW8Zag1gmQec8hiBgBEM40OhRANCAARQRoZZmdkVQDGlBipqXgA4RxbqgKsBBiu4w6IMAtBXlgw+cViKQ2Pnh4YGm3R0yLTkaXgN0P/F0XUoiFRjME45";

    public const string Agent =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgdO0FHUNxusFT/zJ60N9Ny1pL0lI43u5/R9KP+XPHRwyhRANCAAS3NnkHOTeDIP4Dlh6RXvZHAReP33+y4ZcW7ymXmgFYdKP0v/46sJAgVrNM6plCnOsklSVLJf/o7eS0WnxOqgm0";

    public static IReadOnlyList<string> All { get; } = [Accounts, Agent];

    public static SigningKeySettings Settings(string privateKey) => new() { PrivateKey = privateKey };

    public static SigningKeyStore Store(string privateKey) => SigningKeyStore.Open(Settings(privateKey));
}