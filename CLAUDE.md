# Claudette

A .NET desktop app that wraps Claude Code in a native GUI: one tab per Claude Code session, with live usage-limit tracking.

[DESIGN.md](DESIGN.md) is the spec. `§n` always means a section of DESIGN.md.

## Working with the design

- Read the relevant DESIGN.md sections before building a feature.
- If the implementation has to differ from the design, update DESIGN.md in the same change. If the difference is a product decision (what the user sees or can do), ask first.
- Build in the order given in §17 Milestones.
- Record spike findings in DESIGN.md, replacing the spike note.

## Stack

- .NET 10, C# with nullable reference types on and warnings treated as errors. Package versions live in `Directory.Packages.props`.
- Avalonia 12 with MVVM (CommunityToolkit.Mvvm). Markdown is rendered with LiveMarkdown.Avalonia.
- Layout (§13):
  - `src/Claudette.Core`: sessions, protocol, permission rules, sign-in, install checks, settings and state stores and settings sync, transcripts and History, the session library and leases, diffs and changed files, external diff tools, git (project identity, working tree), check-in timing. Later also updates.
  - `src/Claudette.Usage`: plan usage parsing, the SQLite usage history, burn rate and projection, alerts, the polling schedule. No UI.
  - `src/Claudette.Platform`: OS-specific code: the process monitor (Job Objects on Windows, `/proc`, `ps`), notifications and the Dock/taskbar badge (WinRT toasts, `UNUserNotificationCenter`, `notify-send`), the Windows jump list, and the single-instance pipe. No UI. Windows and macOS APIs are called through source-generated COM interop and the Objective-C runtime, so the project stays a plain `net10.0` library.
  - `src/Claudette.App`: the Avalonia UI.
    - `ShellViewModel` holds the tab groups and History.
    - `TabViewModel` is one session, split into partial files for the library, changed files and processes.
    - `Conversation/ConversationBuilder` turns session events into conversation items; `PromptItems` are the permission, question and plan cards.
    - `Services/UsageTracker` and `Services/LibraryService` connect the usage engine and the session library to the tabs.
  - `tests/`: `Claudette.Core.Tests` (unit and protocol replay), `Claudette.Usage.Tests`, `Claudette.Platform.Tests`, `Claudette.App.Tests`, `Claudette.IntegrationTests` (real processes).
  - `tools/`: `Claudette.FakeClaude` (the `fake-claude` test double) and `Claudette.MockApi` (a mock Messages API).
  - `compat/`: the compatibility surface list, check script and snapshots (§16).
  - `packaging/`: the MSIX and `.dmg` build scripts, manifest, `Info.plist`, entitlements and icons; `.github/workflows/package.yml` runs them (§2, "Packaging and signing").
- `Claudette.Core`, `Claudette.Usage` and `Claudette.Platform` must not reference Avalonia.
- Development happens on Windows, but the app must also run on macOS and Linux. Keep OS-specific code behind interfaces, in `Claudette.Platform`.
- In XAML:
  - Use the app's own color tokens from `App.axaml` (`MutedTextBrush`, `DividerBrush` and so on), not Fluent's internal resource names.
  - Reference `Application.Resources` from `Application.Styles` with `DynamicResource`, because styles load before resources.
  - Give icon-only buttons an `AutomationProperties.Name`.
- The visual reference is Claude Code's VS Code extension (§3, "Visual style").

## Rules that keep the code testable (§15)

- Talk to Claude Code only through `IClaudeTransport`.
- Start processes only through `IProcessLauncher`.
- Use the injected `TimeProvider` for anything time-based. Never use `DateTime.Now`, `DateTime.UtcNow` or real delays in logic.
- Never hard-code paths to the app data folder, the session library or `~/.claude`. Inject them.

## Claude Code integration (§13, §16)

- Any new use of something from Claude Code goes in `compat/surface.yaml` in the same change. That includes flags, environment variables, message types or fields, control requests, settings keys, file paths and command output.
- Prefer documented behavior. If you rely on undocumented behavior, mark the entry `undocumented` and record a fallback.
- Always launch `claude` through `ClaudeEnvironment.Create`, which removes the session variables Claude Code sets for its child processes (`ClaudeEnvironment.SessionVariables`, §13). Otherwise a `claude` started from inside a Claude Code session behaves as a child session. Don't strip by prefix: `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_USE_BEDROCK` and similar are user configuration and must pass through.
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

- Tests must never call the real model or use anyone's account or tokens. Use the fake transport, `fake-claude`, or the mock Messages API.
- Tests that run the real `claude` binary are tagged `[Trait("Category", "RealCli")]` and must:
  - point it at `MockAnthropicApi` with `ANTHROPIC_BASE_URL` and a dummy `ANTHROPIC_API_KEY`,
  - set `CLAUDE_CONFIG_DIR` to a temporary folder,
  - set `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`,
  - never read or write the real `~/.claude`,
  - skip themselves (`Assert.SkipWhen`) when Claude Code isn't installed.
- Tests tagged `[Trait("Category", "Live")]` use real tokens. Only run them when asked to.
- Test time-based behavior with `FakeTimeProvider`, not sleeps.
- Recorded protocol fixtures live in `tests/Claudette.Core.Tests/Fixtures/protocol/<claude-version>/`. Remove paths, emails and account details before checking one in.
- xunit.v3 stays on 3.2.x until `Avalonia.Headless.XUnit` supports 4.x.

## Commands

```sh
dotnet build Claudette.slnx
dotnet test Claudette.slnx --filter "Category!=Live"                  # everything free (RealCli skips without Claude Code)
dotnet test Claudette.slnx --filter "Category!=RealCli&Category!=Live" # no Claude Code needed
dotnet run --project src/Claudette.App -- --folder <path>             # uses your real account: messages cost usage
```

Run the app without using tokens by pointing it at the mock API:

```sh
dotnet run --project tools/Claudette.MockApi -- 8787    # in one terminal
# in another, with these set: ANTHROPIC_BASE_URL=http://127.0.0.1:8787  ANTHROPIC_API_KEY=sk-ant-mock
#                             CLAUDE_CONFIG_DIR=<a temp folder>         CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
#                             CLAUDETTE_HOME=<a temp folder>   (keeps Claudette's settings and saved tabs out of your profile)
dotnet run --project src/Claudette.App -- --folder <path>
```

Stop the app by closing its window, not by killing the process: closing interrupts running turns and stops each tab's `claude` and everything it started. A killed app leaves them running; the Job Objects deliberately don't kill on close, so a user can keep a tab's processes (§4).

View model tests use `tests/Claudette.App.Tests/Support/TabTestHarness.cs`: a scripted Claude Code connection, a fake clock and an inline dispatcher.

Prompts the mock understands: `WRITE_FILE <path>`, `EDIT_FILE <path>`, `RUN_BASH <command>`, `ASK_QUESTION`, `EXIT_PLAN` (in Plan mode), `SLOW`; anything else gets `pong`. An API key has no plan limits, so the usage header stays empty against the mock; the usage tests cover it.

Compatibility check (§16): `node compat/check.mjs detect`, then `report` or `update-snapshots`. See the header of `compat/check.mjs`.
