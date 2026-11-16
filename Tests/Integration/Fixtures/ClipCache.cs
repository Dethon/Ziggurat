using System.Security.Cryptography;
using System.Text;

namespace Tests.Integration.Fixtures;

// Test clips kept between runs. Each run made its own in a fresh directory nobody removed — ten
// megabytes a run, five gigabytes in /tmp before anyone looked — and spent ffmpeg's encoding on clips
// that come out the same every time. A clip is named by its recipe (the key: the tool's version and its
// arguments), so a changed recipe is a new clip, and written beside its final name and renamed into
// place, so two test processes making one clip at once each read a whole file.
public static class ClipCache
{
    public static async Task<byte[]> GetAsync(string directory, string key, string name, Func<string, Task> make)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(directory, $"{hash}-{name}");
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path);
        }

        var making = Path.Combine(directory, "making", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(making);
        try
        {
            var made = Path.Combine(making, name);
            await make(made);
            File.Move(made, path, overwrite: true);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            Directory.Delete(making, recursive: true);
        }
    }
}