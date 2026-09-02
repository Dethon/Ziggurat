using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Containers;

namespace Tests.Integration.Fixtures;

// Shared bootstrap for a real Home Assistant container (ghcr.io/home-assistant/home-assistant:stable):
// writes a pre-seeded /config so the REST API is reachable without HA's interactive onboarding, mints
// an HS256 long-lived access token we sign ourselves, and provides a host-side readiness poll. Used by
// both HomeAssistantFixture (standalone HA client tests) and JonasMcpStackFixture (the agent benchmark,
// where the mcp-homeassistant server talks to a real HA over the benchmark network).
//
// The seeding consists of:
//   - configuration.yaml: minimal core + an `input_boolean.test_switch` so the call_service test has a
//     real entity to toggle, and a `script.echo` returning a response dict for the return_response path.
//   - .storage/auth: one owner user + groups + a `long_lived_access_token` refresh token whose `jwt_key`
//     we control. We sign an HS256 JWT with that key and use it as the Bearer token.
//   - .storage/onboarding: marks every onboarding step as done.
//   - .storage/core.config_entries: one Local Calendar entry (the integration has no YAML form), so
//     the calendar round trip — create over the websocket, list with uids, delete — runs against
//     the real component rather than a fake's reading of it.
internal static class HomeAssistantSeed
{
    public const string ContainerImage = "ghcr.io/home-assistant/home-assistant:stable";
    public const int Port = 8123;
    public const string TestEntityId = "input_boolean.test_switch";
    public const string CalendarEntityId = "calendar.alarms";

    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromMinutes(3);

    // Writes configuration.yaml + .storage/{auth,onboarding} into configDir and returns the
    // long-lived access token (Bearer JWT) for the seeded owner user.
    public static string WriteConfig(string configDir)
    {
        Directory.CreateDirectory(Path.Combine(configDir, ".storage"));

        File.WriteAllText(Path.Combine(configDir, "configuration.yaml"),
            """
            homeassistant:
              name: Test
              unit_system: metric
              time_zone: UTC

            http:
              server_host: 0.0.0.0

            api:

            input_boolean:
              test_switch:
                name: Test Switch
                initial: false

            script:
              echo:
                sequence:
                  - variables:
                      out:
                        echoed: "{{ value | default('echo-default') }}"
                  - stop: ""
                    response_variable: out
                fields:
                  value:
                    required: false
            """);

        // Random IDs so each run is independent. HA's auth schema accepts arbitrary hex.
        var userId = RandomHex(16);
        var refreshTokenId = RandomHex(16);
        var refreshTokenHash = RandomHex(32);
        var jwtKey = RandomHex(32);
        var tokenVersion = RandomHex(16);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");

        var auth = new
        {
            version = 1,
            minor_version = 1,
            key = "auth",
            data = new
            {
                users = new[]
                {
                    new
                    {
                        id = userId,
                        group_ids = new[] { "system-admin" },
                        is_owner = true,
                        is_active = true,
                        name = "Test",
                        system_generated = false,
                        local_only = false
                    }
                },
                groups = new[]
                {
                    new { id = "system-admin", name = "Admin" },
                    new { id = "system-users", name = "Users" },
                    new { id = "system-read-only", name = "Read-only" }
                },
                credentials = Array.Empty<object>(),
                refresh_tokens = new object[]
                {
                    new
                    {
                        id = refreshTokenId,
                        user_id = userId,
                        client_id = (string?)null,
                        client_name = "Test LLAT",
                        client_icon = (string?)null,
                        token_type = "long_lived_access_token",
                        created_at = createdAt,
                        access_token_expiration = 315360000.0,
                        token = refreshTokenHash,
                        jwt_key = jwtKey,
                        last_used_at = (string?)null,
                        last_used_ip = (string?)null,
                        credential_id = (string?)null,
                        version = tokenVersion
                    }
                }
            }
        };

        var onboarding = new
        {
            version = 4,
            minor_version = 1,
            key = "onboarding",
            data = new
            {
                done = new[] { "user", "core_config", "integration", "analytics" }
            }
        };

        var configEntries = new
        {
            version = 1,
            minor_version = 5,
            key = "core.config_entries",
            data = new
            {
                entries = new[]
                {
                    new
                    {
                        created_at = createdAt,
                        data = new { calendar_name = "Alarms" },
                        disabled_by = (string?)null,
                        discovery_keys = new { },
                        domain = "local_calendar",
                        entry_id = RandomHex(13),
                        minor_version = 1,
                        modified_at = createdAt,
                        options = new { },
                        pref_disable_new_entities = false,
                        pref_disable_polling = false,
                        source = "user",
                        subentries = Array.Empty<object>(),
                        title = "Alarms",
                        unique_id = (string?)null,
                        version = 1
                    }
                }
            }
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(Path.Combine(configDir, ".storage", "auth"), JsonSerializer.Serialize(auth, jsonOpts));
        File.WriteAllText(Path.Combine(configDir, ".storage", "onboarding"), JsonSerializer.Serialize(onboarding, jsonOpts));
        File.WriteAllText(Path.Combine(configDir, ".storage", "core.config_entries"), JsonSerializer.Serialize(configEntries, jsonOpts));

        return CreateAccessToken(refreshTokenId, jwtKey);
    }

    // HA serves `/api/` long before YAML-loaded integrations finish registering. Wait until both the
    // API responds 200 *and* `/api/services` lists the seeded `input_boolean` domain so callers don't
    // race the integration loader. `baseUrl` is the host-reachable URL (no trailing slash).
    public static async Task WaitForApiReadyAsync(
        IContainer container, string baseUrl, string token, TimeSpan? timeout = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deadline = DateTime.UtcNow + (timeout ?? DefaultReadyTimeout);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var apiResponse = await http.GetAsync("api/");
                if (apiResponse.StatusCode == HttpStatusCode.OK)
                {
                    using var servicesResponse = await http.GetAsync("api/services");
                    if (servicesResponse.IsSuccessStatusCode)
                    {
                        var json = await servicesResponse.Content.ReadAsStringAsync();
                        if (!json.Contains("\"input_boolean\""))
                        {
                            lastError = new InvalidOperationException("input_boolean domain not yet registered");
                        }
                        else
                        {
                            // The config-entry integrations load after the YAML ones; the calendar
                            // is the last thing a test here waits on.
                            using var calendarResponse = await http.GetAsync($"api/states/{CalendarEntityId}");
                            if (calendarResponse.IsSuccessStatusCode)
                            {
                                return;
                            }
                            lastError = new InvalidOperationException($"{CalendarEntityId} not yet registered");
                        }
                    }
                }
                else
                {
                    lastError = new InvalidOperationException($"HA /api/ returned {(int)apiResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(1500);
        }

        var logs = await container.GetLogsAsync();
        throw new TimeoutException(
            $"Home Assistant did not become ready within {timeout ?? DefaultReadyTimeout}. " +
            $"Last error: {lastError?.Message}.\n" +
            $"--- container stdout (tail) ---\n{Tail(logs.Stdout, 4000)}\n" +
            $"--- container stderr (tail) ---\n{Tail(logs.Stderr, 4000)}");
    }

    private static string Tail(string s, int chars) => s.Length <= chars ? s : s[^chars..];

    // HA's long-lived access tokens are HS256 JWTs with `iss = refresh_token.id`, signed by
    // `refresh_token.jwt_key`. See homeassistant/auth/__init__.py.
    private static string CreateAccessToken(string refreshTokenId, string jwtKey)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = now + 315_360_000L;
        var headerJson = """{"typ":"JWT","alg":"HS256"}""";
        var payloadJson = $$"""{"iss":"{{refreshTokenId}}","iat":{{now}},"exp":{{exp}}}""";

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{headerB64}.{payloadB64}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwtKey));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RandomHex(int byteLength)
    {
        var buf = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}