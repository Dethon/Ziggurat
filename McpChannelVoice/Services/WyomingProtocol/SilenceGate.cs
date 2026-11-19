namespace McpChannelVoice.Services.WyomingProtocol;

// Server-side end-of-utterance detection for local-wake-word satellites.
//
// A WakeStreamingSatellite streams mic audio open-endedly after the wake word
// fires and only stops when it receives a Transcript back. There is no
// audio-stop to lean on, so the hub must decide when the speaker has finished:
// once speech has been observed, a run of trailing silence ends the utterance.
// What counts as speech vs silence is delegated to AdaptiveLevelTracker so a
// noisy room (TV, ducked music) raises the bar instead of pinning the capture
// open. A max-utterance cap bounds runaway streams; speech shorter than
// minSpeech is treated as noise and never ends the turn on its own.
public sealed class SilenceGate(
    AdaptiveLevelTracker tracker,
    TimeSpan trailingSilence,
    TimeSpan maxUtterance,
    TimeSpan minSpeech,
    TimeSpan noSpeechTimeout = default)
{
    private TimeSpan _elapsed;
    private TimeSpan _speechElapsed;
    private TimeSpan _trailingSilence;
    private double _trailingEnergyMs;
    private bool _speechStarted;
    private double _peakRms;
    // The trailing run as it stood at the terminal decision. Everyone who reads the run reads it
    // strictly after that decision (the host publishes EndpointTailMs once speaker verification has
    // finished), and Feed keeps accepting frames until the satellite gets its closing transcript, so
    // the live counters would otherwise report the tail plus an arbitrary read delay.
    private TimeSpan? _endTrailingSilence;
    private double _endTrailingRms;

    public enum Decision
    {
        Continue,
        EndUtterance,
        NoSpeech
    }

    public TimeSpan SpeechElapsed => _speechElapsed;

    // Whether a long enough silence from here would end the utterance: speech at or under minSpeech
    // never ends a turn on its own, so a pause before it is not a pause worth acting on.
    public bool HasEndableSpeech => _speechElapsed > minSpeech;

    public double PeakRms => _peakRms;

    public double FloorRms => tracker.FloorRms;

    public double MeasuredFloorRms => tracker.MeasuredFloorRms;

    // Mean RMS of the trailing run — the demote check's background reference. Published with capture
    // stats so the prominence margin can be tuned from field data. Frozen at the terminal decision.
    public double TrailingRms => _endTrailingSilence is null ? LiveTrailingRms : _endTrailingRms;

    private double LiveTrailingRms => _trailingSilence > TimeSpan.Zero
        ? Math.Sqrt(_trailingEnergyMs / _trailingSilence.TotalMilliseconds)
        : 0;

    // The silence run since the last speech frame, frozen once a terminal decision is reached. At
    // EndUtterance this IS the endpointing tail — dead air the user waits through after they stop
    // talking — so the host can publish it instead of leaving the largest unattributed span of the
    // turn invisible, and can rewind the speech-end anchor by it. Audio-domain time (summed PCM frame
    // durations), so it is exact and immune to scheduling jitter; the freeze is what keeps it that
    // way, because frames keep arriving between the decision and anyone reading this.
    public TimeSpan TrailingSilence => _endTrailingSilence ?? _trailingSilence;

    public string? EndReason { get; private set; }

    public double LastChunkRms { get; private set; }
    public bool LastChunkWasSpeech { get; private set; }

    public Decision Process(ReadOnlySpan<byte> pcm, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var duration = DurationOf(pcm.Length, sampleRateHz, sampleWidthBytes, channels);
        _elapsed += duration;

        var rms = Rms(pcm, sampleWidthBytes);
        _peakRms = Math.Max(_peakRms, rms);

        var isSpeech = tracker.IsSpeech(rms, duration.TotalMilliseconds);
        LastChunkRms = rms;
        LastChunkWasSpeech = isSpeech;

        if (isSpeech)
        {
            _speechStarted = true;
            _speechElapsed += duration;
            _trailingSilence = TimeSpan.Zero;
            _trailingEnergyMs = 0;
        }
        else if (_speechStarted)
        {
            _trailingSilence += duration;
            _trailingEnergyMs += rms * rms * duration.TotalMilliseconds;
            if (_speechElapsed > minSpeech && _trailingSilence >= trailingSilence)
            {
                // A floor seeded during a background lull lets resumed TV latch as speech
                // until the min-window converges; the capture then ends here full of TV
                // audio. Such pseudo-speech never stands above the converged floor, while
                // real speech that latched against a converged floor clears it by
                // construction — so demote the capture to no-speech instead of dispatching
                // background. Only gates with a no-speech window may emit NoSpeech (the
                // segmenting gate inside SegmentedSpeechToText must keep slicing on
                // EndUtterance).
                FreezeTrailingRun();
                if (noSpeechTimeout > TimeSpan.Zero && !tracker.SpeechProminent)
                {
                    EndReason = "no_speech";
                    return Decision.NoSpeech;
                }
                EndReason = "trailing_silence";
                return Decision.EndUtterance;
            }
        }

        // The no-speech window expires unless MEANINGFUL speech (> minSpeech) has begun. Gating on
        // _speechElapsed rather than _speechStarted is deliberate: a sub-minSpeech blip (echo tail,
        // a cough) is noise by this gate's own definition and must not latch the window shut — else
        // the capture would hang open until the maxUtterance cap instead of timing out here.
        if (_speechElapsed <= minSpeech && noSpeechTimeout > TimeSpan.Zero && _elapsed >= noSpeechTimeout)
        {
            FreezeTrailingRun();
            EndReason = "no_speech";
            return Decision.NoSpeech;
        }

        if (_elapsed >= maxUtterance)
        {
            FreezeTrailingRun();
            EndReason = "max_utterance";
            return Decision.EndUtterance;
        }
        return Decision.Continue;
    }

    // Idempotent: the terminal conditions stay true for every later frame, so a second freeze would
    // simply re-import the drift this exists to exclude.
    private void FreezeTrailingRun()
    {
        if (_endTrailingSilence is null)
        {
            _endTrailingRms = LiveTrailingRms;
            _endTrailingSilence = _trailingSilence;
        }
    }

    // Deliberately does NOT reset the tracker: SegmentedSpeechToText resets the gate per
    // phrase segment, and the learned noise floor must survive segment boundaries.
    public void Reset()
    {
        _elapsed = TimeSpan.Zero;
        _speechElapsed = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        _trailingEnergyMs = 0;
        _speechStarted = false;
        _peakRms = 0;
        _endTrailingSilence = null;
        _endTrailingRms = 0;
        EndReason = null;
    }

    private static TimeSpan DurationOf(int byteCount, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var bytesPerSecond = sampleRateHz * sampleWidthBytes * channels;
        return bytesPerSecond == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)byteCount / bytesPerSecond);
    }

    private static double Rms(ReadOnlySpan<byte> pcm, int sampleWidthBytes)
    {
        if (sampleWidthBytes != 2 || pcm.Length < 2)
        {
            return 0;
        }

        var samples = pcm.Length / 2;
        double sumSquares = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / samples);
    }
}