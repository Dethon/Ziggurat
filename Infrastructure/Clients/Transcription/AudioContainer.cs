namespace Infrastructure.Clients.Transcription;

// Which container a recording is actually in, decided by its leading bytes and by nothing anyone
// claimed. Telegram documents a voice note's mime type as optional and sender-supplied, and a
// browser's multipart part type is whatever that browser felt like writing — believing either would
// post Opus at whisper, which answers 400 to every one of them, on nothing but a sender's word.
public sealed record AudioContainer(string MediaType, bool NeedsDecoding)
{
    // Opus is the one whisper-server as Lemonade runs it cannot read: it decodes through miniaudio
    // plus stb_vorbis, and the image has no ffmpeg. Everything else here it decodes itself, so
    // decoding those locally would only add a way to get it wrong.
    public static AudioContainer OggOpus { get; } = new("audio/ogg; codecs=opus", NeedsDecoding: true);

    private static readonly AudioContainer Wav = new("audio/wav", NeedsDecoding: false);
    private static readonly AudioContainer Mpeg = new("audio/mpeg", NeedsDecoding: false);
    private static readonly AudioContainer Flac = new("audio/flac", NeedsDecoding: false);
    private static readonly AudioContainer OggVorbis = new("audio/ogg", NeedsDecoding: false);

    public static AudioContainer? Sniff(ReadOnlySpan<byte> audio) => true switch
    {
        _ when Starts(audio, "RIFF"u8) && Contains(audio[..Math.Min(audio.Length, 16)], "WAVE"u8) => Wav,
        _ when Starts(audio, "fLaC"u8) => Flac,
        _ when Starts(audio, "ID3"u8) || IsMpegFrame(audio) => Mpeg,
        _ when Starts(audio, "OggS"u8) => SniffOggCodec(audio),
        _ => null
    };

    // The codec marker sits in the first page's payload, past a segment table whose length varies,
    // so it is looked for rather than read at a fixed offset.
    private static AudioContainer? SniffOggCodec(ReadOnlySpan<byte> audio)
    {
        var head = audio[..Math.Min(audio.Length, 64)];
        return true switch
        {
            _ when Contains(head, "OpusHead"u8) => OggOpus,
            _ when Contains(head, "vorbis"u8) => OggVorbis,
            _ => null
        };
    }

    // An MP3 need not carry an ID3 tag; eleven set sync bits and a version that is not the reserved
    // one are the frame.
    private static bool IsMpegFrame(ReadOnlySpan<byte> audio) =>
        audio.Length >= 2 && audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0 && (audio[1] & 0x18) != 0x08;

    private static bool Starts(ReadOnlySpan<byte> audio, ReadOnlySpan<byte> marker) =>
        audio.Length >= marker.Length && audio[..marker.Length].SequenceEqual(marker);

    private static bool Contains(ReadOnlySpan<byte> audio, ReadOnlySpan<byte> marker) =>
        audio.IndexOf(marker) >= 0;
}