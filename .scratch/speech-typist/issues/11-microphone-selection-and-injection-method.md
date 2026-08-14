# 11 — Microphone selection and injection method

**What to build:** two escape hatches for when the defaults are wrong on your machine.

**Name the microphone.** By default the system default capture device is used, so one microphone needs
no configuration at all. A config key holding a case-insensitive name fragment overrides it, so
plugging in a webcam cannot silently change which microphone you dictate through. A named device that
is not present is reported clearly rather than falling back silently to whatever is.

**Choose how the text arrives.** Synthetic Unicode key events stay the default. A config switch
selects clipboard paste instead — set the clipboard, paste, restore the previous contents — for the
applications that mishandle synthetic input. This is a switch you flip, never auto-detection: an
application deciding for itself which method it gets is a source of surprise, and the whole point of
the escape hatch is that you know when you are using it.

**Blocked by:** 04.

**Status:** ready-for-agent

- [ ] With no microphone configured, the system default is captured
- [ ] A name fragment in config selects a different capture device
- [ ] A configured device that is not present is reported clearly and does not silently fall back
- [ ] Synthetic Unicode key events remain the default injection method
- [ ] The config switch selects clipboard paste, which restores the previous clipboard contents
- [ ] The method is never chosen automatically per application
- [ ] Device resolution from a name fragment and injection-method selection are tested without
      Windows; the two real implementations are verified by hand
