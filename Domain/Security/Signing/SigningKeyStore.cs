using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Security.Signing;

// An issuer's signing keys: the one it signs with now and the ones it signed with before that are
// still published, so a token signed a moment before a rotation keeps verifying.
//
// Kept as one file in the issuer's own volume, replaced whole by a rename, so a rotation is never
// half written. A rotation or retirement is a command run beside the running issuer, not inside it,
// so the store notices the file changing and signs with whatever it now says.
public sealed class SigningKeyStore : IVerificationKeys
{
    public const string FileName = "signing-keys.json";

    private readonly string? _path;
    private readonly Lock _loading = new();
    private volatile Loaded _loaded;

    private SigningKeyStore(string? path, Loaded loaded)
    {
        _path = path;
        _loaded = loaded;
    }

    public string CurrentKeyId => Current.Id;

    public JsonWebKeySet PublishedKeys => new([.. Keys.Select(k => k.PublicKey)]);

    // The last key written is the one signed with; every one before it is still published.
    internal SigningKey Current => Keys[^1];

    private IReadOnlyList<SigningKey> Keys
    {
        get
        {
            if (_path is null || Stamp.Of(_path) == _loaded.Stamp)
            {
                return _loaded.Keys;
            }

            lock (_loading)
            {
                if (Stamp.Of(_path) != _loaded.Stamp)
                {
                    _loaded = Load(_path);
                }

                return _loaded.Keys;
            }
        }
    }

    public static SigningKeyStore Open(SigningKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PrivateKey.Length > 0)
        {
            return new SigningKeyStore(null, new Loaded([SigningKey.Import(settings.PrivateKey)], default));
        }

        if (settings.KeyDirectory.Length == 0)
        {
            throw new InvalidOperationException(
                "an issuer signs with a key it keeps in its KeyDirectory, or with the PrivateKey it is given; neither is set");
        }

        var path = Path.Combine(settings.KeyDirectory, FileName);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(settings.KeyDirectory);
            Write(path, [SigningKey.Generate()]);
        }

        return new SigningKeyStore(path, Load(path));
    }

    public ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct) =>
        ValueTask.FromResult(Keys.FirstOrDefault(k => k.Id == keyId)?.VerificationKey);

    // A new key becomes the one signed with; every key before it stays published until retired.
    // Read from the file rather than from memory, since another process may have rotated since.
    public void Rotate()
    {
        var path = Writable();
        Write(path, [.. Load(path).Keys, SigningKey.Generate()]);
    }

    // Every key but the one signed with is unpublished. Run once every verifier's copy of the keys
    // and every token signed with the previous key has run out.
    public void Retire()
    {
        var path = Writable();
        Write(path, [Load(path).Keys[^1]]);
    }

    private string Writable() =>
        _path ?? throw new InvalidOperationException("a provided key is the only key; it is neither rotated nor retired here");

    private static Loaded Load(string path)
    {
        var stamp = Stamp.Of(path);
        var file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllBytes(path))
                   ?? throw new InvalidDataException($"{path} holds no signing keys");
        return file.Keys is { Count: > 0 } keys
            ? new Loaded([.. keys.Select(SigningKey.Import)], stamp)
            : throw new InvalidDataException($"{path} holds no signing keys");
    }

    // Written beside the file and renamed over it, readable by the issuer's user alone: it is the key.
    private static void Write(string path, IReadOnlyList<SigningKey> keys)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            JsonSerializer.Serialize(stream, new KeyFile([.. keys.Select(k => k.Export())]));
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record KeyFile([property: JsonPropertyName("keys")] IReadOnlyList<string> Keys);

    private sealed record Loaded(IReadOnlyList<SigningKey> Keys, Stamp Stamp);

    // What changes when the file is replaced; the length too, for a file system whose clock is coarse.
    private readonly record struct Stamp(DateTime WrittenAt, long Length)
    {
        public static Stamp Of(string path)
        {
            var file = new FileInfo(path);
            return new Stamp(file.LastWriteTimeUtc, file.Length);
        }
    }
}