// The encoder half of a dictation, in its own file because a worklet cannot be inlined in a
// classic script: the browser fetches this URL and runs it on the audio thread.
//
// It takes the mono frames the graph hands it at whatever rate the device runs at, resamples them
// to the 16 kHz whisper wants, and posts them on as 16-bit little-endian samples. That is the
// format whisper as lemonade runs it accepts, and MediaRecorder cannot produce it — its output is
// Opus in a container whisper answers 400 to.
//
// The rate conversion is done here rather than by asking for a 16 kHz AudioContext, because that
// puts a resampler we neither wrote nor can see inside the capture path, and what it does with a
// phone's microphone is not what it does with a laptop's. This one is the same on every device.
const TARGET_RATE = 16000;

// A 32-tap windowed sinc, evaluated at 128 fractional positions between input samples so any
// device rate lands on a kernel rather than on the nearest sample. At 48 kHz in that costs about a
// million multiplies a second, which is nothing next to running the page at all.
const TAPS = 32;
const HALF = TAPS / 2;
const PHASES = 128;

// 128 ms of output per message, rather than a message per 128-frame render quantum. The main
// thread is running Blazor and a repainting level meter, and on a phone it is the scarce one.
const BATCH = 2048;

const sinc = x => (x === 0 ? 1 : Math.sin(Math.PI * x) / (Math.PI * x));

// Everything above half the output rate has to go before the rate drops, or it folds back down on
// top of the speech — 12 kHz reappearing at 4 kHz, which is heard as the recording being garbled
// rather than as a tone being present. The cutoff is expressed against the input rate and backed
// off a tenth, so the filter has somewhere to roll off in instead of being asked to fall vertically
// at the band edge.
const buildKernels = ratio => Array.from({ length: PHASES }, (_, phase) => {
    const cutoff = Math.min(0.45, 0.45 / ratio);
    const frac = phase / PHASES;
    const row = new Float32Array(TAPS);
    let sum = 0;
    for (let j = 0; j < TAPS; j++) {
        const blackman = 0.42
            - 0.5 * Math.cos((2 * Math.PI * j) / (TAPS - 1))
            + 0.08 * Math.cos((4 * Math.PI * j) / (TAPS - 1));
        row[j] = 2 * cutoff * sinc(2 * cutoff * (j - HALF + 1 - frac)) * blackman;
        sum += row[j];
    }
    // Unity at DC, so resampling changes the rate and not the level.
    for (let j = 0; j < TAPS; j++) row[j] /= sum;
    return row;
});

class DictationEncoder extends AudioWorkletProcessor {
    constructor() {
        super();
        // sampleRate is the graph's own rate, which is the device's.
        this._ratio = sampleRate / TARGET_RATE;
        this._kernels = buildKernels(this._ratio);
        // The kernel of an output sample reaches HALF samples either side of it, so each block
        // needs the tail of the one before it and leaves its own tail to the one after.
        this._history = new Float32Array(TAPS);
        this._window = new Float32Array(TAPS + 128);
        this._seen = 0;
        this._next = 0;
        this._out = new Int16Array(BATCH);
        this._filled = 0;
        this._finished = false;

        // The last batch is still here when the recording ends — often the end of the last word —
        // so the main thread asks for it before it takes the graph down.
        this.port.onmessage = event => {
            if (event.data === 'flush') {
                this._post();
                this._finished = true;
                this.port.postMessage('flushed');
            }
        };
    }

    process(inputs) {
        const channel = inputs[0] && inputs[0][0];
        if (this._finished) {
            return false;
        }
        if (!channel || channel.length === 0) {
            return true;
        }

        const count = channel.length;
        if (this._window.length < TAPS + count) {
            this._window = new Float32Array(TAPS + count);
        }
        this._window.set(this._history, 0);
        this._window.set(channel, TAPS);

        // _window[i] is the input sample at absolute index base + i, and an output sample can only
        // be produced once every tap its kernel reaches is in hand — so the last HALF samples of
        // this block wait for the next one.
        const base = this._seen - TAPS;
        const last = this._seen + count - HALF;
        while (Math.floor(this._next) < last) {
            const whole = Math.floor(this._next);
            const kernel = this._kernels[Math.floor((this._next - whole) * PHASES)];
            const start = whole - HALF + 1 - base;
            let sum = 0;
            for (let j = 0; j < TAPS; j++) {
                sum += this._window[start + j] * kernel[j];
            }
            // Clamped before scaling: a sample past ±1 wraps to the opposite rail otherwise, which
            // is heard as a click exactly where the speaker was loudest.
            const clamped = Math.max(-1, Math.min(1, sum));
            this._out[this._filled++] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff;
            if (this._filled === BATCH) {
                this._post();
            }
            this._next += this._ratio;
        }

        this._seen += count;
        this._history.set(this._window.subarray(count, count + TAPS));
        return true;
    }

    _post() {
        if (this._filled === 0) {
            return;
        }
        const chunk = this._out.slice(0, this._filled);
        this._filled = 0;
        this.port.postMessage(chunk, [chunk.buffer]);
    }
}

registerProcessor('dictation-encoder', DictationEncoder);
