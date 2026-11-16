using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Security.Sealing;
using Shouldly;

namespace Tests.Unit.Domain.Security.Sealing;

// Sealing and opening on their own: a sealed value opens only under the key and the associated data
// it was sealed with, and nothing else about it can change without its opening refusing. Remembered
// logins rest on this today and a household's secrets will, so a blob it lets through is one this key
// sealed for exactly this place.
public class SealingKeyTests
{
    private static readonly byte[] _value = "sk-or-v1-0123456789abcdef"u8.ToArray();
    private static readonly byte[] _place = "home/keys/openRouter"u8.ToArray();

    // A remembered login sealed by the browse server before sealing moved into Domain: key bytes 0..31,
    // sealed for the user id "user-fran". The tenant kit's move copies such records as they are, so
    // this exact blob must keep opening.
    private const string BeforeTheMoveKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private const string BeforeTheMoveBlob =
        "AvImA0QBABqZwbNSZABi8s/zuQjXmlfCvLMtAIDyumDBbfsrenslYQJoxK9w6tGsDBJfu2t2tSKi6qdgxe8XFtCr0hpLGJpvVtJx"
        + "CAYCV1P3pnXTQn1iMSjP8S25BocmYV6oBs2zo7qXqXQHtuD2yav4YVUc6GWQJpL0gzm0ufAe9TELzivABdrXyFRej1Uqw+bNJaQJ"
        + "8NR5BBmJshSHg9NdkqzQ7EPxt3OYedF9aBTiTC0QrKNmx6MnYRzhCTulM4b1w/5D1SSFgjhcbD3qk8kOPqMEHOTgIhmMzbk9J7+g"
        + "jsOYgxAwEN+Pybjlen1WfL3UD5C9S3/eXZ1puIWu36DPwLhwhyZm2zHEBlb2bnkLCp+SgvkLbb3Cz6/f1c7rGxHmsu8tm04n15Zk"
        + "+7BlGs7rvtbBTAxDid9QiIpKWTtTGgJG5lXbSTkr0BC3EEsEZ05ryjJaBoKPtdSfwEPnQIrtyKjB";

    private readonly SealingKey _key = SealingKey.Generate();

    [Fact]
    public void ASealedValue_OpensWithItsKeyAndAssociatedData()
    {
        var opened = _key.Open(_key.Seal(_value, _place), _place);

        opened.Refusal.ShouldBeNull();
        opened.IsOpened.ShouldBeTrue();
        opened.Value.ShouldBe(_value);
    }

    [Fact]
    public void ASealedValue_DoesNotOpenUnderOtherAssociatedData()
    {
        var sealedValue = _key.Seal(_value, _place);

        var opened = _key.Open(sealedValue, "away/keys/openRouter"u8);

        opened.Refusal.ShouldBe(SealRefusal.DoesNotOpen);
        opened.Value.ShouldBeNull();
    }

    [Fact]
    public void ASealedValue_DoesNotOpenUnderAnotherKey()
    {
        var sealedValue = _key.Seal(_value, _place);

        SealingKey.Generate().Open(sealedValue, _place).Refusal.ShouldBe(SealRefusal.DoesNotOpen);
    }

    // The version byte, the nonce, the tag and the ciphertext: a flip anywhere after the version
    // is somebody else's value, and the tag says so.
    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(29)]
    [InlineData(-1)]
    public void ATamperedValue_DoesNotOpen(int at)
    {
        var sealedValue = _key.Seal(_value, _place);
        sealedValue[at < 0 ? sealedValue.Length + at : at] ^= 0x01;

        _key.Open(sealedValue, _place).Refusal.ShouldBe(SealRefusal.DoesNotOpen);
    }

    [Fact]
    public void AValueOfAnUnknownVersion_IsNotSealed()
    {
        var sealedValue = _key.Seal(_value, _place);
        sealedValue[0] = 9;

        _key.Open(sealedValue, _place).Refusal.ShouldBe(SealRefusal.NotSealed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)]
    public void AValueTooShortToHoldANonceAndTag_IsNotSealed(int length)
    {
        var sealedValue = _key.Seal(_value, _place)[..length];

        _key.Open(sealedValue, _place).Refusal.ShouldBe(SealRefusal.NotSealed);
    }

    [Fact]
    public void AnEmptyValue_SealsAndOpens()
    {
        _key.Open(_key.Seal([], _place), _place).Value.ShouldBe([]);
    }

    // A fresh nonce each time: two seals of one value share nothing an observer could match.
    [Fact]
    public void SealingOneValueTwice_GivesTwoDifferentBlobs()
    {
        _key.Seal(_value, _place).ShouldNotBe(_key.Seal(_value, _place));
    }

    [Fact]
    public void TheSealedBytes_CarryNothingOfTheValueInTheClear()
    {
        Encoding.UTF8.GetString(_key.Seal(_value, _place)).ShouldNotContain("sk-or-v1");
    }

    [Fact]
    public void ALoginSealedBeforeTheMove_StillOpens()
    {
        var key = SealingKey.Parse(BeforeTheMoveKey, "Logins:StateKey").ShouldNotBeNull();

        var opened = key.Open(Convert.FromBase64String(BeforeTheMoveBlob), "user-fran"u8);

        opened.Refusal.ShouldBeNull();
        var record = JsonNode.Parse(opened.Value!)!;
        record["storageState"]!.GetValue<string>().ShouldContain("s3cr3t-session");
        record["logins"]![0]!["site"]!.GetValue<string>().ShouldBe("bank.example");
    }

    [Fact]
    public void AnExportedKey_ImportsToOneThatOpensWhatItSealed()
    {
        var sealedValue = _key.Seal(_value, _place);

        SealingKey.Import(_key.Export()).Open(sealedValue, _place).Value.ShouldBe(_value);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void ImportingAKeyOfTheWrongLength_Fails(int length)
    {
        Should.Throw<CryptographicException>(() => SealingKey.Import(new byte[length]));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void AMalformedConfiguredKey_FailsNamingTheSetting(string configured)
    {
        Should.Throw<InvalidOperationException>(() => SealingKey.Parse(configured, "Logins:StateKey"))
            .Message.ShouldContain("Logins:StateKey");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyConfiguredKey_IsNoKey(string? configured)
    {
        SealingKey.Parse(configured, "Logins:StateKey").ShouldBeNull();
    }

    [Fact]
    public void AConfiguredKey_IsTheBase64OfItsBytes()
    {
        var configured = Convert.ToBase64String(_key.Export());

        SealingKey.Parse($" {configured}\n", "Logins:StateKey")!.Open(_key.Seal(_value, _place), _place)
            .Value.ShouldBe(_value);
    }
}