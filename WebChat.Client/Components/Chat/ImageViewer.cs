namespace WebChat.Client.Components.Chat;

// A picture as the viewer shows it: the file, its name, and the address a thumbnail already minted for it,
// if one did (the viewer mints one for a picture shown before its thumbnail loaded).
public sealed record ViewedPicture(string AttachmentId, string FileName, string? Url);

// The one picture viewer of the page (ImageViewerHost, in the layout): a thumbnail anywhere opens its
// message's pictures here, at the one it was, so the viewer is never trapped inside whatever drew the
// thumbnail.
public sealed class ImageViewer
{
    private IReadOnlyList<ViewedPicture> _pictures = [];
    private int _index;

    public event Action? Changed;

    public ViewedPicture? Current => _pictures.Count > 0 ? _pictures[_index] : null;

    public int Count => _pictures.Count;

    // Where the current picture is, counted from one.
    public int Position => _index + 1;

    public void Open(IReadOnlyList<ViewedPicture> pictures, int index)
    {
        _pictures = pictures;
        _index = index >= 0 && index < pictures.Count ? index : 0;
        Changed?.Invoke();
    }

    public void Step(int by)
    {
        if (_pictures.Count < 2)
        {
            return;
        }

        _index = ((_index + by) % _pictures.Count + _pictures.Count) % _pictures.Count;
        Changed?.Invoke();
    }

    public void Close()
    {
        _pictures = [];
        _index = 0;
        Changed?.Invoke();
    }
}