// ===================================
// Dictation: one run of the microphone that ends as words
// ===================================
//
// The browser owns all of it — permission, the microphone, the encoder, the gesture thresholds,
// the clock, the level meter and the upload — and calls into .NET only at decisions: started,
// latched, discarded, transcript, failed. The encoded audio never enters the WASM heap; it is
// posted from here with a ticket .NET hands over, and only the words come back.
//
// Geometry mirrors the hearth sheet's proven numbers: 8 px before a direction is committed, latch
// at 56 px of upward travel, discard at 96 px toward the textarea, 24 px of tap slop.

window.dictation = {
    _ref: null,
    _mic: null,
    // The cap and the floor are always .NET's: register hands them over before any listener
    // exists (falling back to AttachmentLimits' named defaults there), so no number is compiled
    // into this file to drift from the one the server holds.
    _limits: null,
    _unavailable: false,
    _stamped: false,

    // The live recording, or null between dictations.
    _run: null,
    _lastPointerAt: 0,

    // The last few dictations, in the browser's own words. A phone that stops recording is the one
    // place none of this can be watched from: there is no console to open and no cable attached, and
    // reasoning about it from a desktop suite that passes has already produced three wrong fixes.
    // Kept always, because the run that matters is over by the time anyone thinks to ask for it.
    _trace: [],
    // Read here rather than at registration, because by then the app has routed: picking a space
    // and opening a topic are navigations, and each one leaves the address the app was opened with
    // behind. This runs while the page is still the one that was asked for.
    _tracing: new URLSearchParams(location.search).has('dictation-trace'),

    // The cap and the floor are the server's, and they can arrive after the microphone is
    // registered — the limits call needs a live connection and the first render does not wait for
    // one. Whatever answers last wins, so the browser never keeps a stale cap of its own.
    configure: function (limits) {
        if (limits) this._limits = limits;
    },

    register: function (mic, ref, limits) {
        if (this._mic === mic && this._ref) {
            this._limits = limits || this._limits;
            return;
        }
        this._mic = mic;
        this._ref = ref;
        this._limits = limits || this._limits;
        this._note('registered', this._tracing ? 'tracing' : '');
        // Which script this is, in the server's own words for the file. Without it a fix that
        // failed and a fix that never reached the phone read identically — which is how five
        // rounds of "still broken" were once spent on builds nobody could tell apart.
        if (!this._stamped) {
            this._stamped = true;
            fetch('dictation.js', { method: 'HEAD' })
                .then(r => this._note('script',
                    r.headers.get('etag') || r.headers.get('last-modified') || 'unstamped'))
                .catch(() => this._note('script', 'unreachable'));
        }

        mic.addEventListener('pointerdown', this._onDown);
        mic.addEventListener('keydown', this._onKeyDown);
        // A keyboard activation arrives as a click with no pointer behind it (detail 0). That is
        // the one click that starts a dictation: a pointer tap is a mis-tap, and its own handler
        // says so.
        mic.addEventListener('click', this._onClick);
        document.addEventListener('keydown', this._onEscape);
        document.addEventListener('visibilitychange', this._onVisibility);
    },

    dispose: function () {
        this._discard();
        if (this._mic) {
            this._mic.removeEventListener('pointerdown', this._onDown);
            this._mic.removeEventListener('keydown', this._onKeyDown);
            this._mic.removeEventListener('click', this._onClick);
        }
        document.removeEventListener('keydown', this._onEscape);
        document.removeEventListener('visibilitychange', this._onVisibility);
        this._mic = null;
        this._ref = null;
    },

    // Called from .NET: the latched stop button, and the trash button / Escape / a topic change.
    stop: function () { this._finish(); },
    discard: function () { this._discard(); },

    isRecording: function () { return !!this._run && !this._run.ending; },

    // ---- gesture ----

    _onDown: function (e) {
        const d = window.dictation;
        d._lastPointerAt = performance.now();
        if (d._unavailable || d._run) return;
        if (e.button !== undefined && e.button !== 0) return;
        d._mic.setPointerCapture && d._mic.setPointerCapture(e.pointerId);
        d._start(false, { x: e.clientX, y: e.clientY, pointerId: e.pointerId });
        document.addEventListener('pointermove', d._onMove, { passive: false });
        document.addEventListener('pointerup', d._onUp);
        document.addEventListener('pointercancel', d._onCancel);
        e.preventDefault();
    },

    _onMove: function (e) {
        const d = window.dictation;
        const run = d._run;
        if (!run || run.latched || run.ending || !run.press) return;
        const dx = e.clientX - run.press.x;
        const dy = e.clientY - run.press.y;
        if (run.axis === null) {
            const COMMIT = 8;
            if (Math.abs(dx) < COMMIT && Math.abs(dy) < COMMIT) return;
            run.axis = Math.abs(dy) >= Math.abs(dx) ? 'y' : 'x';
        }
        const DISCARD = 96;
        const LATCH = 56;
        if (run.axis === 'x') {
            // The microphone sits on the right of the composer, so "toward the textarea" is left.
            // The hint travels and fades with the finger, so the distance left to go is visible.
            d._setStripVar('--dictation-travel', Math.min(1, Math.max(0, -dx / DISCARD)));
            if (-dx >= DISCARD) d._discard();
        } else {
            // The hint above the microphone rises with the finger for the same reason.
            d._setVar('.dictation-lift', '--dictation-lift', Math.min(1, Math.max(0, -dy / LATCH)));
            if (dy <= -LATCH) d._latch();
        }
        e.preventDefault();
    },

    _onUp: function () {
        const d = window.dictation;
        d._releasePointer();
        const run = d._run;
        if (!run || run.latched || run.ending) return;
        // Below the tap slop a drifting press is still a hold, so only the clock decides whether
        // this was a mis-tap.
        if (performance.now() - run.startedAt < d._limits.minMs) {
            d._discard();
            d._invoke('MisTapped', 'Hold the microphone to record.');
            return;
        }
        d._finish();
    },

    _onCancel: function () {
        window.dictation._releasePointer();
        window.dictation._discard();
    },

    _releasePointer: function () {
        document.removeEventListener('pointermove', this._onMove);
        document.removeEventListener('pointerup', this._onUp);
        document.removeEventListener('pointercancel', this._onCancel);
    },

    // Enter and Space are how a keyboard presses a button, and nobody should have to hold a key
    // down: both start a latched dictation straight away.
    _onKeyDown: function (e) {
        const d = window.dictation;
        if (e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Spacebar') return;
        e.preventDefault();
        if (d._unavailable || d._run) return;
        d._start(true, null);
    },

    _onClick: function (e) {
        const d = window.dictation;
        // Only a keyboard activation, which arrives with no pointer behind it. A touch's
        // compatibility click can carry a detail of 0 on some builds, so the clock decides too:
        // a click moments after a real press is that press's own, and _onUp already judged it.
        if (e.detail !== 0) return;
        if (performance.now() - d._lastPointerAt < 700) return;
        if (d._unavailable || d._run) return;
        d._start(true, null);
    },

    _onEscape: function (e) {
        if (e.key === 'Escape' && window.dictation._run) {
            window.dictation._discard();
        }
    },

    // A background tab must never be quietly holding the microphone open.
    _onVisibility: function () {
        if (document.hidden && window.dictation._run) {
            window.dictation._discard();
        }
    },

    // ---- the recording itself ----

    _start: function (latched, press) {
        const run = {
            latched: latched,
            press: press,
            axis: null,
            startedAt: performance.now(),
            chunks: [],
            samples: 0,
            ending: false,
            ticket: null,
            stream: null,
            ctx: null,
            encoder: null,
            drained: null,
            meter: 0,
            meterAt: 0
        };
        this._run = run;
        this._note('press', latched ? 'latched' : 'held');
        // Minted while the microphone opens rather than after it: the two round trips overlap, and
        // a dictation short enough to be a mis-tap costs the request nothing because it is thrown
        // away before it is used.
        run.ticket = this._ref
            ? this._ref.invokeMethodAsync('MintTicketAsync')
                .then(ticket => {
                    // The ticket carries the rules, from the same live call: a first dictation
                    // started before the connection was up would otherwise obey a compiled-in cap.
                    if (ticket) {
                        this.configure({ maxMs: ticket.maxMs, minMs: ticket.minMs });
                        this._armCap(run);
                    }
                    return ticket;
                })
                // A server that answers with a refusal is not a server that could not be
                // reached, and the two are diagnosed in different places — so its own words
                // are kept rather than flattened into "I could not reach the server".
                .catch(err => {
                    run.ticketRefusal = err && err.message ? err.message : null;
                    return null;
                })
            : null;

        this._open(run).then(() => {
            if (this._run !== run || run.ending) return;
            // How the recording can end, as it stands now rather than as the press left it: opening
            // the microphone takes as long as the device takes, and a finger can travel the whole
            // way up inside that. Reported as the press, this arrives behind the latch and undoes
            // it — leaving a recording that says "slide to cancel" to a finger that is no longer
            // there, with no way out but reloading the page.
            this._invoke(run.latched ? 'Latched' : 'Started');
            this._startClock(run);
        }).catch(err => {
            if (this._run === run) this._run = null;
            // Whatever the open got as far as acquiring is still live here, and this is the last
            // place holding it: the microphone may well have been granted before the graph fell
            // over, and the context that fell over is holding an output stream of its own because
            // the chain ends at the destination. Dropped on the floor they are unreachable — no
            // later press, no discard and no visibility change can ever close them, and the next
            // attempt simply acquires another pair.
            this._teardown(run);
            // A discard while the microphone was still opening is not a failure to open it: the
            // press was already answered, by a mis-tap hint or by nothing at all.
            if (!run.ending) this._refuse(err);
        });
    },

    _open: async function (run) {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.AudioContext) {
            const error = new Error('unsupported');
            error.unsupported = true;
            throw error;
        }

        // Each step of the open is marked as it is passed, so a run that never comes back says where
        // it stopped. Which of these is the last line is the whole diagnosis: the microphone itself,
        // the graph the browser builds around it, or the worklet fetched over the network.
        this._note('opening');
        run.stream = await navigator.mediaDevices.getUserMedia(this._constraints());
        this._watchTracks(run);
        if (run.ending || this._run !== run) {
            this._teardown(run);
            return;
        }

        // The graph runs at the device's own rate. Asking for 16 kHz instead puts a resampler we
        // did not write inside the capture path, and what it does with a phone's microphone is not
        // what it does with a laptop's; the worklet converts to 16 kHz afterwards, identically
        // everywhere.
        const ctx = new AudioContext();
        run.ctx = ctx;
        // A context created outside a gesture starts suspended, and a suspended graph never pulls
        // the worklet — the recording would be silence of exactly the right length.
        if (ctx.state === 'suspended') {
            await ctx.resume();
        }
        this._note('context', ctx.state + ' at ' + Math.round(ctx.sampleRate) + ' Hz');
        // A graph a phone suspends mid-run stops pulling the worklet without a word to anything
        // else; the close at the end of every run writes a line too, which is this handler
        // reporting for duty on runs where nothing went wrong.
        ctx.onstatechange = () => this._note('context', ctx.state);
        await ctx.audioWorklet.addModule('dictation-encoder.js');
        this._note('worklet');
        if (run.ending || this._run !== run) {
            this._teardown(run);
            return;
        }

        const source = ctx.createMediaStreamSource(run.stream);
        const analyser = ctx.createAnalyser();
        analyser.fftSize = 256;
        const encoder = new AudioWorkletNode(ctx, 'dictation-encoder');
        encoder.port.onmessage = e => {
            // The encoder answers a flush with a word rather than samples.
            if (typeof e.data === 'string') {
                if (run.drained) run.drained();
                return;
            }
            run.chunks.push(e.data);
            run.samples += e.data.length;
        };
        // The chain has to arrive somewhere the context renders, and the only such place is the
        // context's own destination: Android will not run a graph that reaches no output at all,
        // and the whole recording — worklet and level meter alike — is then never pulled and hears
        // silence. So the chain ends at a gain of zero rather than at the microphone being played
        // back into the room. Nothing here may be left as a leaf, however tempting: a desktop
        // renders one anyway, which is exactly why the mistake survives every test we can run.
        const silence = ctx.createGain();
        silence.gain.value = 0;
        source.connect(analyser);
        analyser.connect(encoder);
        encoder.connect(silence);
        silence.connect(ctx.destination);
        run.analyser = analyser;
        run.encoder = encoder;
        this._note('graph');
    },

    // The processed path — echo cancellation on — is the only path, decided on evidence from the
    // phone that kept wedging: Android's raw capture path came up born-dead (zeros on a healthy
    // graph, cleared only by reboot) while the processed path recorded fine through the very same
    // wedge. Echo cancellation is what selects that path; noise suppression only rode along on
    // it, and live it behaved as the gate the old raw-path rationale warned about — opening and
    // closing on the speaker mid-sentence until the transcription came back nonsense — so it is
    // asked off while the path stays. Automatic gain is its own decision: asked for as false, an
    // Android phone spoken to normally returned peaks some 20 dB below speech.
    _constraints: function () {
        return {
            audio: {
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: false,
                autoGainControl: true
            }
        };
    },

    // The platform taking the capture away mid-run — another app, a route change, an audio server
    // dying — arrives only as these events, and only ever on a phone. A run that goes quiet at one
    // second looks identical to one that heard silence unless the moment is stamped here. The
    // settings ride along because the wedge investigation turns on which capture path a grant
    // actually landed on, and the label alone does not say.
    _watchTracks: function (run) {
        run.stream.getAudioTracks().forEach(track => {
            track.onmute = () => this._note('track', 'mute');
            track.onunmute = () => this._note('track', 'unmute');
            track.onended = () => this._note('track', 'ended');
        });
        this._note('microphone', run.stream.getAudioTracks()
            .map(t => {
                const s = t.getSettings ? t.getSettings() : {};
                return (t.label || 'unnamed') + (t.muted ? ' [muted]' : '')
                    + ' (ec ' + (s.echoCancellation ? 'on' : 'off')
                    + ', ns ' + (s.noiseSuppression ? 'on' : 'off')
                    + ', agc ' + (s.autoGainControl ? 'on' : 'off') + ')';
            })
            .join(', '));
    },

    _latch: function () {
        const run = this._run;
        if (!run || run.latched) return;
        run.latched = true;
        run.press = null;
        this._setStripVar('--dictation-travel', 0);
        this._releasePointer();
        this._invoke('Latched');
    },

    // ---- ending ----

    _finish: function () {
        const run = this._run;
        if (!run || run.ending) return;
        run.ending = true;
        this._stopClock(run);
        // The microphone closes now — the indicator going out is what answers letting go — but the
        // graph stays up a moment longer, until the encoder has handed over the batch it was still
        // filling.
        this._stopTracks(run);
        this._clearHints();
        this._run = null;
        this._invoke('Ended');
        this._transcribe(run);
    },

    _discard: function () {
        const run = this._run;
        if (!run) return;
        run.ending = true;
        this._stopClock(run);
        this._teardown(run);
        this._run = null;
        this._releasePointer();
        this._invoke('Discarded');
    },

    // The encoder posts in batches rather than once per render quantum, so up to an eighth of a
    // second is still on the audio thread when a dictation ends — which is usually the end of the
    // last word. Ask for it, and do not wait forever: a graph that has already gone away answers
    // nothing, and what was collected is still a recording.
    _drain: function (run) {
        if (!run.encoder) return Promise.resolve();
        return new Promise(resolve => {
            const timer = setTimeout(resolve, 300);
            run.drained = () => { clearTimeout(timer); resolve(); };
            try {
                run.encoder.port.postMessage('flush');
            } catch (e) {
                run.drained();
            }
        });
    },

    _transcribe: async function (run) {
        try {
            await this._drain(run);
            this._closeContext(run);
            // An empty recording is not a transcript that could not be made out, and sending it
            // asks whisper to account for audio nothing ever heard — which comes back as the
            // person's own words being blamed. Opening the microphone takes as long as the device
            // takes, and a deliberate hold can be over before the graph exists at all.
            // How much sound the run actually collected, which is the difference between a
            // microphone that was never opened, one that was opened and heard nothing, and one that
            // worked — three failures that look identical from the outside.
            // The count alone cannot tell speech from a dead input: zeros fill batches at exactly
            // the rate a voice does. The peak and the last audible moment are what separate a
            // microphone that was never opened, one that went quiet mid-run, and one that worked.
            const peak = this._peak(run);
            this._note('recorded', run.samples + ' samples in ' + run.chunks.length + ' batches, ' +
                (peak > 0 ? 'peak ' + Math.round(20 * Math.log10(peak / 32768)) + ' dB' : 'all zeros') +
                ', ' + (run.heardAt ? 'last sound at ' + run.heardAt + 'ms' : 'no sound ever heard'));
            if (run.samples === 0) {
                this._invoke('Failed', run.encoder
                    ? 'The microphone recorded no sound at all.'
                    : 'The microphone had not finished opening, so nothing was recorded.');
                return;
            }
            // -60 dBFS over a whole run is not a quiet room: automatic gain lifts any live
            // microphone's floor far above it, and the wedged phone's own traces measured -78 dB
            // and flat zeros. Uploading it asks whisper to account for silence — which comes back
            // as a transcription error, inviting a retry into the same wall. The words instead
            // name the one cure that has actually worked.
            if (peak < 33) {
                this._invoke('Failed',
                    'The microphone stayed open but heard pure silence — the device’s audio '
                    + 'input looks stuck. Other apps may still work; restarting the device clears it.');
                return;
            }
            const ticket = await run.ticket;
            if (!ticket) {
                this._invoke('Failed', run.ticketRefusal
                    ? 'The server would not take that recording: ' + run.ticketRefusal
                    : 'I could not reach the server to turn that into words.');
                return;
            }

            const body = new FormData();
            body.append('file', this._wav(run), 'dictation.wav');
            const response = await fetch(ticket.url, {
                method: 'POST',
                headers: { 'X-Dictation-Ticket': ticket.token },
                body: body
            });

            if (!response.ok) {
                const said = (await response.text()).trim();
                this._invoke('Failed', said || 'I could not turn that recording into words.');
                return;
            }

            const answer = await response.json();
            const text = (answer && answer.text ? answer.text : '').trim();
            if (!text) {
                this._invoke('Failed', 'I could not make out anything in that recording.');
                return;
            }
            this._invoke('Transcribed', text);
        } catch (err) {
            this._invoke('Failed', 'I could not turn that recording into words.');
        } finally {
            // Whatever the upload did, the graph does not outlive it.
            this._closeContext(run);
        }
    },

    // 16 kHz mono s16le in a RIFF wrapper — the one shape whisper as lemonade runs it accepts.
    _wav: function (run) {
        const bytes = run.samples * 2;
        const buffer = new ArrayBuffer(44 + bytes);
        const view = new DataView(buffer);
        const ascii = (offset, text) => {
            for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
        };
        ascii(0, 'RIFF');
        view.setUint32(4, 36 + bytes, true);
        ascii(8, 'WAVE');
        ascii(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);            // PCM
        view.setUint16(22, 1, true);            // mono
        view.setUint32(24, 16000, true);
        view.setUint32(28, 16000 * 2, true);    // byte rate
        view.setUint16(32, 2, true);            // block align
        view.setUint16(34, 16, true);           // bits per sample
        ascii(36, 'data');
        view.setUint32(40, bytes, true);

        const gain = this._gain(run);
        let offset = 44;
        for (const chunk of run.chunks) {
            for (let i = 0; i < chunk.length; i++, offset += 2) {
                const lifted = Math.round(chunk[i] * gain);
                view.setInt16(offset, Math.max(-32768, Math.min(32767, lifted)), true);
            }
        }
        return new Blob([buffer], { type: 'audio/wav' });
    },

    // A floor under the capture side's own automatic gain, for a phone held at arm's length. Only
    // ever a lift and capped, so a recording of a quiet room stays a recording of a quiet room
    // rather than being pulled up to full scale as noise.
    _peak: function (run) {
        return run.chunks.reduce(
            (loudest, chunk) => chunk.reduce((most, s) => Math.max(most, Math.abs(s)), loudest), 0);
    },

    _gain: function (run) {
        const peak = this._peak(run);
        return peak === 0 ? 1 : Math.max(1, Math.min(8, 29000 / peak));
    },

    _teardown: function (run) {
        // The clock first, and the cap with it. A dictation whose graph never came up has usually
        // armed one already — the ticket that carries the number is minted alongside the microphone
        // rather than after it — and an alarm set for a recording that no longer exists still goes
        // off. It ends "the recording", meaning whichever one is live by then.
        this._stopClock(run);
        this._stopTracks(run);
        this._closeContext(run);
        // The hints belong to whichever dictation is on screen now, not to the one being torn down.
        // A run that ends late — a slow open finishing after its own discard, a failure arriving
        // after the next press — would otherwise blank a strip a newer recording is already drawing.
        if (this._run === null || this._run === run) this._clearHints();
    },

    _closeContext: function (run) {
        if (run.ctx) {
            try { run.ctx.close(); } catch (e) { /* already closed */ }
            run.ctx = null;
        }
    },

    _clearHints: function () {
        this._setStripVar('--dictation-travel', 0);
        this._setStripVar('--dictation-level', 0);
    },

    _stopTracks: function (run) {
        if (run.stream) {
            run.stream.getTracks().forEach(track => track.stop());
            run.stream = null;
        }
    },

    // ---- what is on screen while it runs ----

    _startClock: function (run) {
        this._armCap(run);
        const paint = () => {
            if (this._run !== run || run.ending) return;
            const elapsed = performance.now() - run.startedAt;
            // Read even with no strip on screen yet: the end-of-run silence verdict turns on
            // whether anything was ever heard, and that must not depend on a render having landed.
            const level = this._level(run).toFixed(3);
            const strip = document.querySelector('.dictation-strip');
            if (strip) {
                const timer = strip.querySelector('.dictation-timer');
                if (timer) timer.textContent = this._clock(elapsed);
                strip.style.setProperty('--dictation-level', level);
            }
            run.frame = requestAnimationFrame(paint);
        };
        run.frame = requestAnimationFrame(paint);
    },

    // A pocketed phone must not record indefinitely: the cap stops the dictation and transcribes
    // what it has rather than throwing it away. Armed twice — once when the microphone opens, and
    // again when the ticket brings the server's own number — so it fires against the elapsed time
    // rather than restarting the clock.
    _armCap: function (run) {
        if (this._run !== run || run.ending) return;
        if (run.cap) clearTimeout(run.cap);
        const left = Math.max(0, this._limits.maxMs - (performance.now() - run.startedAt));
        // The cap belongs to the run that armed it and to no other. _finish ends whatever is live,
        // so a timer that outlived its own dictation would cut the next one short — and stopping
        // the clock on every exit is the other half of the same promise, not a substitute for this.
        run.cap = setTimeout(() => {
            if (this._run === run) this._finish();
        }, left);
    },

    _stopClock: function (run) {
        if (run.cap) { clearTimeout(run.cap); run.cap = null; }
        if (run.frame) { cancelAnimationFrame(run.frame); run.frame = null; }
    },

    // Root-mean-square of the live window, so a muted or misrouted input device is visible while
    // someone is still speaking rather than after the transcript comes back empty.
    //
    // Read at full precision rather than through getByteTimeDomainData, whose eight bits cannot
    // represent anything below about -42 dBFS at all.
    _rms: function (run) {
        if (!run.analyser) return 0;
        const samples = new Float32Array(run.analyser.fftSize);
        run.analyser.getFloatTimeDomainData(samples);
        return Math.sqrt(samples.reduce((sum, s) => sum + s * s, 0) / samples.length);
    },

    // Speech is not a steady tone: syllables and the gaps between them swing the raw needle across
    // its whole range several times a second, which reads as flicker rather than as a voice. The
    // bar follows the reading through a one-pole filter instead, and rises about four times faster
    // than it falls, so a word still lands promptly while the silence after it decays away.
    //
    // Timed off the frame's own interval rather than a per-frame fraction, so the meter settles at
    // the same rate on a 120 Hz phone as on a 60 Hz laptop, and a frame dropped while the browser
    // is busy does not leave the bar behind.
    _level: function (run) {
        const now = performance.now();
        const rms = this._rms(run);
        // -50 dBFS: ten decibels under the meter's own floor and far above digital silence, so a
        // capture that dies mid-run leaves behind the moment the sound stopped rather than a guess.
        if (rms > 0.003) run.heardAt = Math.round(now - run.startedAt);
        const target = this._meter(rms);
        const dt = run.meterAt ? Math.min(now - run.meterAt, 250) : 0;
        run.meterAt = now;
        const tau = target > run.meter ? 110 : 420;
        run.meter += (target - run.meter) * (1 - Math.exp(-dt / tau));
        return run.meter;
    },

    // The needle reads in decibels over a 60 dB range, not in amplitude. A working but quiet
    // microphone sits at a hundredth of full scale, and on a linear meter that is a needle that
    // never leaves the peg — indistinguishable from one that is hearing nothing, which is the one
    // distinction the meter exists to draw.
    _meter: function (rms) {
        if (!(rms > 0)) return 0;
        return Math.min(1, Math.max(0, (20 * Math.log10(rms) + 60) / 60));
    },

    _clock: function (ms) {
        const total = Math.floor(ms / 1000);
        const minutes = Math.floor(total / 60);
        const seconds = total % 60;
        return (minutes < 10 ? '0' : '') + minutes + ':' + (seconds < 10 ? '0' : '') + seconds;
    },

    _setStripVar: function (name, value) {
        this._setVar('.dictation-strip', name, value);
    },

    _setVar: function (selector, name, value) {
        const element = document.querySelector(selector);
        if (element) element.style.setProperty(name, value);
    },

    // ---- refusals ----

    // Only two failures are still true on the next press, and only they may turn the control off
    // for the rest of the page: a permission that was refused, and a browser with nothing to record
    // with — the one _open throws itself, a page away from any device.
    //
    // Everything else is the device of the moment and recovers with it. NotFoundError used to be
    // read as a browser that cannot record, but it is a phone that has no input to give right now:
    // an audio server that has just come back, a headset that has just gone away, an input a call is
    // holding. Latched on one of those, the microphone stays dead until the page is reloaded, which
    // nobody thinks to do — least of all on a phone, where the app is reopened rather than loaded.
    _refuse: function (err) {
        const denied = err && (err.name === 'NotAllowedError' || err.name === 'SecurityError');
        if (denied || (err && err.unsupported)) {
            this._unavailable = true;
            this._invoke('Unavailable', denied
                ? 'I cannot use the microphone here: permission was refused.'
                : 'This browser will not let me record here.');
            return;
        }
        this._invoke('Failed', 'I could not start recording' + this._named(err) + '.');
    },

    // One sentence used to cover a device that would not start, a platform that aborted the start,
    // a worklet that would not load and a graph in the wrong state — four faults reported as one
    // and diagnosable as none. On a phone the words on screen are the whole instrument, so the
    // failure names itself there rather than in a console nobody can open.
    _named: function (err) {
        if (!err) return '';
        const said = err.message && err.message.length <= 80 ? err.message : '';
        const parts = [err.name, said].filter(Boolean);
        return parts.length ? ' (' + parts.join(': ') + ')' : '';
    },

    // ---- the trace ----

    // Bounded, because it is kept for every dictation of a session that may last days on a phone
    // that is never reloaded. Forty lines is several whole runs, which is as far back as any of
    // this is worth reading.
    _note: function (event, detail) {
        this._trace.push({
            at: Math.round(performance.now()),
            event: event,
            detail: detail || ''
        });
        if (this._trace.length > 40) this._trace.splice(0, this._trace.length - 40);
        this._show();
    },

    // Readable from the console where there is one, and off the page where there is not.
    diagnostics: function () {
        return this._trace
            .map(note => note.at + 'ms ' + note.event + (note.detail ? ': ' + note.detail : ''))
            .join('\n');
    },

    // Written straight to the DOM, like the clock and the level meter and for the same reason: this
    // belongs to the browser. Going through .NET would put the trace in the composer, where the
    // component's own lifetime decides how long it survives — and a diagnostic that a re-render can
    // silently drop is worse than none, because its absence would be read as nothing having gone
    // wrong. The panel is this file's own, so it lasts exactly as long as the page does.
    _show: function () {
        if (!this._tracing) return;
        let panel = document.querySelector('.dictation-trace');
        if (!panel) {
            panel = document.createElement('pre');
            panel.className = 'dictation-trace';
            panel.setAttribute('data-testid', 'dictation-trace');
            // Selectable and scrollable, over everything, and out of the way of the composer it is
            // reporting on. A phone reads this by pressing and holding it, so it must be text.
            panel.style.cssText =
                'position:fixed;left:0;top:0;right:0;max-height:45vh;overflow:auto;z-index:9999;' +
                'margin:0;padding:8px;white-space:pre-wrap;word-break:break-word;' +
                'font:12px/1.35 monospace;background:rgba(0,0,0,.85);color:#0f0;user-select:text';
            document.body.appendChild(panel);
        }
        panel.textContent = this.diagnostics();
    },

    _invoke: function (method, argument) {
        if (!this._ref) return;
        if (method === 'Failed' || method === 'Unavailable') this._note('refused', argument);
        const call = argument === undefined
            ? this._ref.invokeMethodAsync(method)
            : this._ref.invokeMethodAsync(method, argument);
        call.catch(() => { /* the page is going away */ });
    }
};
