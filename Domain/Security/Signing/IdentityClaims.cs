using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.DTOs.Channel;

namespace Domain.Security.Signing;

// Who a signed token says a turn or a call is for, and the envelope that bounds the saying: the one
// claim set both issuers build from. The accounts service signs it as an assertion; the agent signs
// the identity it verified again, once per server it calls, and as the delegation a deferred turn
// acts on — a delegation being these claims and a few of its own, so the record is open to a
// derived one and the signer and verifier carry whatever a derived record adds.
public record IdentityClaims
{
    // The household's guest: nobody's account, and still a signed claim. Account ids are lowercase
    // hex, so the marker can never be one. Its absence is not the guest — a missing or failing
    // token is nobody at all.
    public const string GuestSubject = "guest";

    // Nobody: what the agent signs a call as when no person or guest stands behind it — a fire nobody
    // vouched for, a turn whose channel named no account, its own channel plumbing. Only a call
    // token says it; an assertion that named nobody would vouch for nothing. Not hex either, and the
    // one subject that may leave the household out.
    public const string NobodySubject = "nobody";

    [JsonPropertyName("iss")]
    public string Issuer { get; init; } = "";

    [JsonPropertyName("aud")]
    public string Audience { get; init; } = "";

    [JsonPropertyName("sub")]
    public string Subject { get; init; } = "";

    [JsonPropertyName("tid")]
    public string TenantId { get; init; } = "";

    [JsonPropertyName("chn")]
    public string ChannelId { get; init; } = "";

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = [];

    [JsonPropertyName("iat")]
    [JsonConverter(typeof(NumericDateConverter))]
    public DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("nbf")]
    [JsonConverter(typeof(NumericDateConverter))]
    public DateTimeOffset NotBefore { get; init; }

    [JsonPropertyName("exp")]
    [JsonConverter(typeof(NumericDateConverter))]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("jti")]
    public string Id { get; init; } = "";

    [JsonIgnore]
    public bool IsGuest => Subject == GuestSubject;

    [JsonIgnore]
    public bool IsNobody => Subject == NobodySubject;

    // The person's account, as everything keyed by a person compares it; the guest and nobody have none.
    [JsonIgnore]
    public Account? Account => IsGuest || IsNobody ? null : new Account(TenantId, Subject);

    public static IdentityClaims Person(string userId, string tenantId, string channelId, IReadOnlyList<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (userId is GuestSubject or NobodySubject)
        {
            throw new ArgumentException("the guest and nobody are signed as themselves, not as a person", nameof(userId));
        }

        return new IdentityClaims { Subject = userId, TenantId = tenantId, ChannelId = channelId, Roles = roles };
    }

    public static IdentityClaims Guest(string tenantId, string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return new IdentityClaims { Subject = GuestSubject, TenantId = tenantId, ChannelId = channelId };
    }

    public static IdentityClaims Nobody(string channelId) =>
        new() { Subject = NobodySubject, ChannelId = channelId };

    // The same identity under a fresh envelope: a new id, valid from now for the lifetime given. How
    // the agent turns the assertion it verified into a call token for each server, and how an
    // issuer dates what it signs. Dated in whole seconds, as the token carries them, so what was
    // signed and what verifies are the same claims.
    public IdentityClaims Stamped(string issuer, string audience, DateTimeOffset now, TimeSpan lifetime)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        return this with
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = at,
            NotBefore = at,
            ExpiresAt = at + lifetime,
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
        };
    }

    // JWT's NumericDate: whole seconds since the epoch.
    private sealed class NumericDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToUnixTimeSeconds());
    }
}