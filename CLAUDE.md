# Claudette

A .NET desktop app that wraps Claude Code in a native GUI: one tab per Claude Code session, with live usage-limit tracking.

[DESIGN.md](DESIGN.md) is the spec. `§n` always means a section of DESIGN.md.

## Working with the design

- Read the relevant DESIGN.md sections before building a feature.
- If the implementation has to differ from the design, update DESIGN.md in the same change. If the difference is a product decision (what the user sees or can do), ask first.
- Build in the order given in §17 Milestones.
- Record spike findings in DESIGN.md, replacing the spike note.

## Stack

- .NET 10, C# with nullable reference types on and warnings treated as errors.
- Avalonia UI with MVVM (CommunityToolkit.Mvvm).
- Projects (§13):
  - `Claudette.Core`: sessions, protocol, sign-in, updates, session library.
  - `Claudette.Usage`: usage sampling, burn rate, history.
  - `Claudette.Platform`: notifications, window chrome, process monitor, external diff.
  - `Claudette.App`: the Avalonia UI.
- `Claudette.Core` and `Claudette.Usage` must not reference Avalonia.
- Development happens on Windows, but the app must also run on macOS and Linux. Keep OS-specific code in `Claudette.Platform`, behind interfaces.

## Rules that keep the code testable (§15)

- Talk to Claude Code only through `IClaudeTransport`.
- Start processes only through `IProcessLauncher`.
- Use the injected `TimeProvider` for anything time-based. Never use `DateTime.Now`, `DateTime.UtcNow` or real delays in logic.
- Never hard-code paths to the app data folder, the session library or `~/.claude`. Inject them.

## Claude Code integration (§13, §16)

- Any new use of something from Claude Code goes in `compat/surface.yaml` in the same change. That includes flags, environment variables, message types or fields, control requests, settings keys, file paths and command output.
- Prefer documented behavior. If you rely on undocumented behavior, mark the entry `undocumented` and record a fallback.
- Always launch `claude` with a clean environment: remove inherited `CLAUDECODE`, `CLAUDE_CODE_*` and `CLAUDE_AGENT_SDK_*` variables (§13). This applies in the app and in tests. Otherwise a `claude` started from inside a Claude Code session behaves as a child session.
- Parse tolerantly:
  - Ignore unknown fields.
  - Skip unknown message types.
  - Map unknown enum values to `Unknown`.
  - Never let one bad line end a session.
- Detect features from the `capabilities` list on `system/init`, not by comparing version numbers.
- References:
  - Claude Code docs: https://code.claude.com/docs. Add `.md` to any page URL for Markdown; https://code.claude.com/docs/llms.txt lists every page.
  - The Agent SDK TypeScript reference (`/docs/en/agent-sdk/typescript`) is the most complete description of messages and control methods.
  - The Python Agent SDK source (github.com/anthropics/claude-agent-sdk-python) shows the control message wire format.

## Spikes

`spikes/` holds the throwaway Node scripts from the protocol spikes: a mock Messages API, a stream-json driver and scenario files. See [spikes/README.md](spikes/README.md).

- Use them as a working reference for the wire protocol, and to check how a new Claude Code version behaves.
- They aren't part of the app or the build. Don't extend them into app code; the .NET test harness replaces them.
- `scenarios/10-real-usage.json` uses real tokens on the user's account. Only run it when asked to.

## Tests

- Tests must never call the real model or use anyone's account or tokens. Use the fake transport, `fake-claude`, or the mock model server.
- Tests that run the real `claude` binary must:
  - set `CLAUDE_CONFIG_DIR` to a temporary folder,
  - set `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`,
  - never read or write the real `~/.claude`.
- Tests tagged `Live` use real tokens. Only run them when asked to.
- Test time-based behavior with `FakeTimeProvider`, not sleeps.

## Commands

<!-- TODO: add build, run and test commands (including how to exclude Live tests) when the solution is scaffolded. -->
