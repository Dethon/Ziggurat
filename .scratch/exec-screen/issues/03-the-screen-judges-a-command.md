# 03 — The screen judges a command

Status: resolved

Spec: `../spec.md` (Decisions: State, Questions, Rule, No verdict, Settings, Metric). Blocked by: 01.

Add a Domain `ExecScreen` over `IJudge`:

- It takes the reach, the command, the working directory and the recent request text.
- It answers `Run` or `Ask(codes)`.
- It applies the rule per reach, with the probe's wording and bars.
- It enforces the deadline with a linked CTS and discards late answers, as `SkillPreloader` does.
- It maps every `AbsenceReason` to "no verdict".

Add `ExecScreenSettings` bound from `execScreen` in `Agent/appsettings.json`, then register the
screen in `Agent/Modules/InjectorModule.cs` beside `SkillPreloader`.

It publishes `ExecScreenEvent`, and `MetricsCollectorService` collects it. Update
`.claude/rules/observability.md` if it lists the events.

Unit tests use a fake `IJudge`: each reach against each flag, each absence reason, a late answer
discarded, `enabled: false` sending nothing, and the state's shape, including the three-message
window, the cap and no tool output.
