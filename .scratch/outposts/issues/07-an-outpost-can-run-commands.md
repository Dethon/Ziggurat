# 07 — An outpost can run commands

**What to build:** A flag that lets the agent run commands on the machine an outpost is serving,
in the working directory it was given.

It is off unless asked for. Exposing a person's files should not imply exposing a shell on their
computer, and the safe thing has to be the thing that happens when someone runs the binary
without reading anything.

There is a trap here worth knowing before starting: a backend advertises an operation by
overriding it, and the tool registrar reflects over the type to find out. A constructor argument
therefore cannot switch exec off — a backend that overrides exec advertises exec no matter what it
was handed. The outpost has to register one of two backend types depending on the flag.

**Blocked by:** 02 — A mount declares whether it accepts landings. 05 — The outpost serves a
machine's filesystem.

**Status:** done

- [x] An exec flag, off by default.
- [x] With it off, the mount advertises no exec capability at all, and the model is not offered
      the tool for that mount.
- [x] With it on, commands run in the outpost's working directory, and the working directory is
      answered in virtual coordinates like any other path the caller did not name.
- [x] An exec-enabled outpost that is jailed refuses to run anything outside its working
      directory, on the same rule as every other operation.
- [x] An outpost declares that it is not a landing target, whether or not it can execute, so
      attachments keep going to the sandbox. Verified with a registry holding both.
- [x] The generated mount description says whether commands can be run, because it is generated
      from the same flag.
