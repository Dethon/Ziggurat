namespace Domain.Tools.FileSystem;

// Which paths file_read treats as an image, decided by extension alone and aligned with the media
// types AttachmentKinds already classifies as images, so a picture read off a mount and a picture
// somebody attached are the same kind of thing to everything downstream.
//
// No magic-byte sniffing: a file named .md is read as text even if it happens to start with a PNG
// header, because naming it is how the model asked for a text read.
public static class ImageFileKinds
{
    public static string? MediaTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };
}