using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// The bridge carries a file whole, in the agent's memory, so a command is held to a ceiling: a
// file past it is refused as too large in either direction — never buffered and then refused, and
// never written in part.
public class VfsBridgeCeilingTests
{
    private readonly VfsBridge _bridge = new(
        new FakeTimeProvider(DateTimeOffset.UtcNow), new VfsBridgeSettings { MaxFileBytes = 8 });

    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string>
    {
        ["fits.md"] = "1234567\n",
        ["film.bin"] = "0123456789abcdef"
    });

    private VfsCall Mint() => _bridge.Mint(BridgeFixtures.Registry((_vault, "/vault", null)), _ => true, null);

    [Fact]
    public async Task AFileAtTheCeiling_IsRead()
    {
        var answer = await Mint().ReadAsync("/vault/fits.md", CancellationToken.None);

        answer.ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value.Length.ShouldBe(8);
    }

    [Fact]
    public async Task AFilePastTheCeiling_IsRefusedAsTooLarge()
    {
        var answer = await Mint().ReadAsync("/vault/film.bin", CancellationToken.None);

        var refused = answer.ShouldBeOfType<BridgeAnswer<byte[]>.Refused>();
        refused.Errno.ShouldBe(Errnos.TooLarge);
        refused.Error.ShouldNotBeNull().Message.ShouldContain("8 bytes");
    }

    [Fact]
    public async Task AWritePastTheCeiling_IsRefusedAndWritesNothing()
    {
        var call = Mint();

        var answer = await call.WriteAsync("/vault/fits.md", "0123456789"u8.ToArray(), isNew: false, CancellationToken.None);

        answer.ShouldBeOfType<BridgeAnswer<bool>.Refused>().Errno.ShouldBe(Errnos.TooLarge);
        _vault.Files["fits.md"].ShouldBe("1234567\n");
        call.Changes.ShouldHaveSingleItem().Status.ShouldBe(global::Domain.DTOs.FileSystem.VfsChange.Statuses.Refused);
    }
}