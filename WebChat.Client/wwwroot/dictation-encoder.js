// The encoder half of a dictation, in its own file because a worklet cannot be inlined in a
// classic script: the browser fetches this URL and runs it on the audio thread.
//
// It does one thing — take the mono frames the graph hands it and post them on as 16-bit
// little-endian samples. The rate is not converted here: the AudioContext is created at 16 kHz, so
// the browser has already resampled whatever the microphone actually runs at. That is the format
// whisper as lemonade runs it accepts, and MediaRecorder cannot produce it — its output is Opus in
// a container whisper answers 400 to.
class DictationEncoder extends AudioWorkletProcessor {
    process(inputs) {
        const channel = inputs[0] && inputs[0][0];
        if (!channel || channel.length === 0) {
            return true;
        }

        const samples = new Int16Array(channel.length);
        for (let i = 0; i < channel.length; i++) {
            // Clamped before scaling: a sample past ±1 wraps to the opposite rail otherwise, which
            // is heard as a click exactly where the speaker was loudest.
            const s = Math.max(-1, Math.min(1, channel[i]));
            samples[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
        }

        this.port.postMessage(samples, [samples.buffer]);
        return true;
    }
}

registerProcessor('dictation-encoder', DictationEncoder);
