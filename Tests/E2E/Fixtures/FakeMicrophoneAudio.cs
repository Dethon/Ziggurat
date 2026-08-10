namespace Tests.E2E.Fixtures;

// What Chromium's fake microphone hears. Its own default is a beep, which proves only that bytes
// moved; this is a signal with a known shape, so a test can ask whether the recording that came out
// the far end is still the sound that went in.
//
// Two tones. One sits in the middle of the speech band and must survive. The other sits above the
// band 16 kHz sampling can represent, and must not: dropping to 16 kHz without filtering first
// folds it back down to 4 kHz, on top of the speech, which is heard as the recording being garbled
// rather than as a tone being present.
public static class FakeMicrophoneAudio
{
    public const int CaptureRate = 48_000;
    public const int TranscriptionRate = 16_000;

    public const double SpeechToneHz = 1_000;
    public const double AboveTheBandToneHz = 12_000;

    // Where the 12 kHz tone lands if it is decimated rather than resampled: |12000 - 16000|.
    public const double AliasHz = 4_000;

    private const double SpeechAmplitude = 0.5;
    private const double AboveTheBandAmplitude = 0.35;

    // Chromium loops the file for as long as the page records. Two seconds is a whole number of
    // cycles of both tones at 48 kHz (periods of 48 and 4 samples), so the loop point is seamless
    // and no click enters the recording to muddy the measurement.
    private const int Seconds = 2;

    private static readonly Lazy<string> Written = new(Write);

    public static string WriteToTempFile() => Written.Value;

    private static string Write()
    {
        var count = CaptureRate * Seconds;
        var samples = Enumerable.Range(0, count).Select(i =>
        {
            var t = (double)i / CaptureRate;
            var value = SpeechAmplitude * Math.Sin(2 * Math.PI * SpeechToneHz * t)
                        + AboveTheBandAmplitude * Math.Sin(2 * Math.PI * AboveTheBandToneHz * t);
            return (short)(value * short.MaxValue);
        }).ToArray();

        var path = Path.Combine(Path.GetTempPath(), "ziggurat-fake-microphone.wav");
        File.WriteAllBytes(path, Wav(samples, CaptureRate));
        return path;
    }

    private static byte[] Wav(IReadOnlyList<short> samples, int rate)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var bytes = samples.Count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + bytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(bytes);
        samples.ToList().ForEach(writer.Write);
        writer.Flush();
        return stream.ToArray();
    }

    // Goertzel: the energy at one frequency, without paying for a whole transform. The recurrence
    // carries two samples of state, which is why this is a loop rather than a fold.
    public static double MagnitudeAt(IReadOnlyList<double> samples, double hz, int rate)
    {
        var k = 2 * Math.Cos(2 * Math.PI * hz / rate);
        double previous = 0;
        double beforeThat = 0;
        foreach (var sample in samples)
        {
            var current = sample + k * previous - beforeThat;
            beforeThat = previous;
            previous = current;
        }

        var power = (previous * previous) + (beforeThat * beforeThat) - (k * previous * beforeThat);
        return Math.Sqrt(Math.Max(0, power)) / samples.Count;
    }

    // The samples of a 16-bit mono WAV, as values in [-1, 1].
    public static IReadOnlyList<double> Samples(byte[] wav) =>
        Enumerable.Range(0, (wav.Length - 44) / 2)
            .Select(i => BitConverter.ToInt16(wav, 44 + i * 2) / 32768.0)
            .ToArray();
}