using System.Diagnostics;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelVoice;

// Pins the config.json that DockerCompose/lemonade/entrypoint.sh writes for lemond, via the
// STT_CONFIG_ONLY seam (no server start, no model pull): the STT_BACKEND device mapping plus
// the whispercpp.args line that restores the Wyoming-era decode-quality flags (Silero VAD,
// Castilian initial prompt, beam size). The script is bind-mounted from the repo, so this
// tests the working tree, not whatever entrypoint the image was built with. Runs with
// --network none: VAD-model presence is controlled by seeding the file, and the download
// path degrades to no-VAD (fail-open) instead of hitting the network. LemonadeImageFixture
// provisions lemonade:latest (building it when missing), so the only skips left are a
// non-Linux host and an unusable docker.
public class LemonadeEntrypointConfigTests : IClassFixture<LemonadeImageFixture>, IDisposable
{
    private const string VadModelFile = "ggml-silero-v5.1.2.bin";

    private readonly string? _imageSkipReason;
    private readonly string _configDir;

    public LemonadeEntrypointConfigTests(LemonadeImageFixture image)
    {
        _imageSkipReason = image.SkipReason;
        _configDir = Path.Combine(Path.GetTempPath(), $"lemonade-entrypoint-{Guid.NewGuid()}");
        Directory.CreateDirectory(_configDir);
        // The image runs as UID 10001; the mount must be writable for config.json. Guarded because
        // File.SetUnixFileMode throws PlatformNotSupportedException on Windows — which would fail
        // the constructor (an error, not a skip) before RunEntrypoint's platform gate can fire.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_configDir, (UnixFileMode)0b111_111_111);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, true);
        }
    }

    private void SeedVadModel()
    {
        var vadDir = Path.Combine(_configDir, "vad");
        Directory.CreateDirectory(vadDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(vadDir, (UnixFileMode)0b111_111_111);
        }
        File.WriteAllBytes(Path.Combine(vadDir, VadModelFile), [1, 2, 3]);
    }

    private JsonObject RunEntrypoint(params (string Key, string Value)[] env)
    {
        var result = RunEntrypointRaw(env);
        result.Exit.ShouldBe(0, $"entrypoint failed: {result.StdErr}");

        var config = File.ReadAllText(Path.Combine(_configDir, "config.json"));
        return JsonNode.Parse(config)!.AsObject();
    }

    private (int Exit, string StdOut, string StdErr) RunEntrypointRaw(
        params (string Key, string Value)[] env)
    {
        // The entrypoint is a Linux shell run through a Linux container over a unix-mode bind
        // mount; only assert it on a Linux host rather than hard-failing elsewhere.
        Skip.IfNot(OperatingSystem.IsLinux(), "lemonade entrypoint test requires a Linux docker host");
        Skip.If(_imageSkipReason is not null, _imageSkipReason);

        var script = Path.Combine(RepoRoot(), "DockerCompose", "lemonade", "entrypoint.sh");
        List<string> args =
        [
            "run", "--rm", "--network", "none",
            "--entrypoint", "sh",
            "-e", "STT_CONFIG_ONLY=1",
            "-e", "LEMONADE_CONFIG_DIR=/cfg",
            "-v", $"{script}:/entrypoint.sh:ro",
            "-v", $"{_configDir}:/cfg"
        ];
        foreach (var (key, value) in env)
        {
            args.AddRange(["-e", $"{key}={value}"]);
        }
        args.AddRange([LemonadeImageFixture.Image, "/entrypoint.sh"]);

        return Run("docker", args);
    }

    // The pre-pull payload is echoed before the STT_CONFIG_ONLY seam precisely so it can be
    // asserted without starting the server or reaching a registry.
    private JsonObject PullPayload(params (string Key, string Value)[] env)
    {
        var result = RunEntrypointRaw(env);
        result.Exit.ShouldBe(0, $"entrypoint failed: {result.StdErr}");

        var line = result.StdOut
            .Split('\n')
            .Single(l => l.StartsWith("lemonade: stt pull payload=", StringComparison.Ordinal));
        return JsonNode.Parse(line["lemonade: stt pull payload=".Length..])!.AsObject();
    }

    private static string WhisperArgs(JsonObject config) =>
        config["whispercpp"]!["args"]!.GetValue<string>();

    private static string LlamaArgs(JsonObject config) =>
        config["llamacpp"]!["args"]!.GetValue<string>();

    [SkippableFact]
    public void Entrypoint_Defaults_RestoreVadPromptAndBeamSize()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        config["whispercpp"]!["backend"]!.GetValue<string>().ShouldBe("cpu");
        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldContain("--prompt \"Asistente de voz en español de España");
        // The prompt must stay free of meta-language: "p. ej. Valladolid." was observed being
        // emitted verbatim as a transcript on short, quiet audio.
        whisperArgs.ShouldNotContain("p. ej.");
        whisperArgs.ShouldContain($"--vad --vad-model /cfg/vad/{VadModelFile} --vad-threshold 0.6");
    }

    [SkippableFact]
    public void Entrypoint_Defaults_AddSuppressNstBestOfAndVadPadding()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(("STT_BACKEND", "cpu")));

        whisperArgs.ShouldContain("--suppress-nst");
        whisperArgs.ShouldContain("--best-of 5");
        whisperArgs.ShouldContain("--vad-speech-pad-ms 150");
        whisperArgs.ShouldContain("--vad-min-speech-duration-ms 150");
    }

    [SkippableFact]
    public void Entrypoint_EmptyDecodeKnobs_DisableTheirFlags()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_SUPPRESS_NST", ""),
            ("STT_BEST_OF", ""),
            ("STT_VAD_SPEECH_PAD_MS", ""),
            ("STT_VAD_MIN_SPEECH_MS", "")));

        whisperArgs.ShouldNotContain("--suppress-nst");
        whisperArgs.ShouldNotContain("--best-of");
        whisperArgs.ShouldNotContain("--vad-speech-pad-ms");
        whisperArgs.ShouldNotContain("--vad-min-speech-duration-ms");
        whisperArgs.ShouldContain("--vad --vad-model");
    }

    // The VAD padding flags are only legal when VAD itself is on, so they must live inside the
    // same branch that established the model is present.
    [SkippableFact]
    public void Entrypoint_VadDisabled_EmitsNoVadPaddingFlags()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_VAD_THRESHOLD", "")));

        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldContain("--suppress-nst");
    }

    [SkippableFact]
    public void Entrypoint_DecodeOverrides_PropagateToArgs()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_BEST_OF", "3"),
            ("STT_VAD_SPEECH_PAD_MS", "80"),
            ("STT_VAD_MIN_SPEECH_MS", "200")));

        whisperArgs.ShouldContain("--best-of 3");
        whisperArgs.ShouldContain("--vad-speech-pad-ms 80");
        whisperArgs.ShouldContain("--vad-min-speech-duration-ms 200");
    }

    [SkippableFact]
    public void Entrypoint_EmptyKnobs_DisableTheirFlags()
    {
        SeedVadModel();

        var config = RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_VAD_THRESHOLD", ""),
            ("STT_INITIAL_PROMPT", ""));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldNotContain("--prompt");
    }

    [SkippableFact]
    public void Entrypoint_Overrides_PropagateToArgs()
    {
        SeedVadModel();

        var config = RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_BEAM_SIZE", "3"),
            ("STT_VAD_THRESHOLD", "0.7"),
            ("STT_INITIAL_PROMPT", "hola caracola"));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 3");
        whisperArgs.ShouldContain("--vad-threshold 0.7");
        whisperArgs.ShouldContain("--prompt \"hola caracola\"");
    }

    [SkippableFact]
    public void Entrypoint_VadModelUnavailable_FailsOpenWithoutVad()
    {
        // No seeded model + --network none: the download can't succeed, so the entrypoint
        // must start whisper without VAD rather than crash-loop the container.
        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldContain("--prompt \"Asistente de voz");
    }

    // A built-in model is already in Lemonade's registry, so naming it is the whole request —
    // sending registration fields for one would claim ownership of a name we do not own.
    [SkippableFact]
    public void Entrypoint_BuiltInModel_PullsByNameAlone()
    {
        SeedVadModel();

        var payload = PullPayload(("STT_BACKEND", "cpu"), ("STT_MODEL", "Whisper-Large-v3-Turbo"));

        payload["model_name"]!.GetValue<string>().ShouldBe("Whisper-Large-v3-Turbo");
        payload.ContainsKey("recipe").ShouldBeFalse();
        payload.ContainsKey("checkpoints").ShouldBeFalse();
    }

    // A `user.` model is in no registry until we put it there, and /api/v1/pull doubles as the
    // registration endpoint: without recipe + checkpoints it answers a 400 demanding the very
    // `user.` namespace it was already given.
    [SkippableFact]
    public void Entrypoint_UserModel_RegistersItsCheckpointOnPull()
    {
        SeedVadModel();

        var payload = PullPayload(
            ("STT_BACKEND", "cpu"),
            ("STT_MODEL", "user.Whisper-Large-v3-Turbo-ES"),
            ("STT_MODEL_CHECKPOINT", "some-org/some-repo:ggml-model.bin"));

        payload["model_name"]!.GetValue<string>().ShouldBe("user.Whisper-Large-v3-Turbo-ES");
        payload["recipe"]!.GetValue<string>().ShouldBe("whispercpp");
        payload["checkpoints"]!["main"]!.GetValue<string>().ShouldBe("some-org/some-repo:ggml-model.bin");
    }

    // Config error, not a network blip: an unregisterable user model would start a container whose
    // every transcription 400s, so it fails at boot naming the variable that is missing.
    [SkippableFact]
    public void Entrypoint_UserModelWithoutCheckpoint_FailsFast()
    {
        SeedVadModel();

        var result = RunEntrypointRaw(
            ("STT_BACKEND", "cpu"), ("STT_MODEL", "user.Whisper-Large-v3-Turbo-ES"));

        result.Exit.ShouldNotBe(0);
        result.StdErr.ShouldContain("STT_MODEL_CHECKPOINT");
    }

    // Everything this container serves is pinned, not merely pre-pulled: Lemonade's eviction
    // score favours dropping fast-loading models, and an unpinned model that merely looks warmed
    // can be evicted — the next utterance, recall or reply then pays the several-second load the
    // warmup exists to remove. The pin payloads are echoed before the STT_CONFIG_ONLY seam like
    // the pull payload, so the set is assertable without a server or registry.
    [SkippableFact]
    public void Entrypoint_PinsEveryModelItServes()
    {
        SeedVadModel();

        var pins = PinPayloads(("STT_BACKEND", "cpu"));

        pins.Keys.ShouldBe(
            ["Whisper-Large-v3-Turbo", "Qwen3-Embedding-0.6B-GGUF", "kokoro-v1"], ignoreOrder: true);
        pins.Values.ShouldAllBe(pinned => pinned);
    }

    [SkippableFact]
    public void Entrypoint_ModelOverrides_PinTheOverriddenNames()
    {
        SeedVadModel();

        var pins = PinPayloads(
            ("STT_BACKEND", "cpu"),
            ("STT_MODEL", "Whisper-Medium"),
            ("EMBEDDING_MODEL", "some-embedding"),
            ("TTS_MODEL", "some-voice"));

        pins.Keys.ShouldBe(["Whisper-Medium", "some-embedding", "some-voice"], ignoreOrder: true);
    }

    private Dictionary<string, bool> PinPayloads(params (string Key, string Value)[] env)
    {
        var result = RunEntrypointRaw(env);
        result.Exit.ShouldBe(0, $"entrypoint failed: {result.StdErr}");

        return result.StdOut
            .Split('\n')
            .Where(l => l.StartsWith("lemonade: pin payload=", StringComparison.Ordinal))
            .Select(l => JsonNode.Parse(l["lemonade: pin payload=".Length..])!.AsObject())
            .ToDictionary(
                p => p["model_name"]!.GetValue<string>(),
                p => p["pinned"]!.GetValue<bool>());
    }

    // The context goes through llamacpp.args and NOT through the global ctx_size key, which looks
    // like the obvious place and is not: lemond auto-tunes it, and the boot log says so in as many
    // words — "Migrating config: ctx_size 4096 -> -1 (auto-tune enabled)", followed by "Auto-tune
    // ctx_size resolved to 32768". A recipe's args survive that migration untouched (whispercpp's
    // do), and lemond appends them AFTER its own flags, so a second --ctx-size wins on llama.cpp's
    // last-occurrence parsing. Why bother: auto-tune resolves to the loaded model's maximum, 32768
    // for Qwen3-Embedding-0.6B, and llama.cpp allocates that whole KV cache up front — measured on
    // prod, a model with under a gigabyte of weights held 1.9 GiB of the iGPU's 4 GiB carveout and
    // spilled 2.7 GiB into GTT, with whisper already resident. Nothing here embeds more than a
    // memory statement, a forget query or a three-user-turn recall window.
    [SkippableFact]
    public void Entrypoint_Defaults_PinTheEmbeddingContextSize()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        LlamaArgs(config).ShouldContain("--ctx-size 8192");
    }

    [SkippableFact]
    public void Entrypoint_CtxSizeOverride_PropagatesToConfig()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"), ("EMBEDDING_CTX_SIZE", "8192"));

        LlamaArgs(config).ShouldContain("--ctx-size 8192");
    }

    // Passing no --ctx-size at all is the only way back to lemond's auto-tune, since the flag it
    // emits itself is the one being overridden. An operator who wants the model's maximum again
    // needs a way to say so that does not mean editing a config lemond rewrites on every boot.
    [SkippableFact]
    public void Entrypoint_CtxSizeEmpty_LeavesAutoTuneAlone()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"), ("EMBEDDING_CTX_SIZE", ""));

        LlamaArgs(config).ShouldNotContain("--ctx-size");
    }

    // The global key is the trap this whole arrangement exists to avoid, so writing it would be
    // actively misleading: it survives into config.json just long enough to be migrated away.
    [SkippableFact]
    public void Entrypoint_WritesNoGlobalCtxSize()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        config.ContainsKey("ctx_size").ShouldBeFalse();
    }

    [SkippableFact]
    public void Entrypoint_GpuBackend_MapsToVulkan()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "gpu"));

        config["whispercpp"]!["backend"]!.GetValue<string>().ShouldBe("vulkan");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ziggurat.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Ziggurat.sln not found above test directory");
    }

    private static (int Exit, string StdOut, string StdErr) Run(string command, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000).ShouldBeTrue($"{command} timed out");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}