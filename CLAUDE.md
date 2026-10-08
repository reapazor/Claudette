# Claudette

A .NET desktop app that wraps Claude Code in a native GUI: one tab per Claude Code session, with live usage-limit tracking.

[DESIGN.md](DESIGN.md) is the spec. `§n` always means a section of DESIGN.md.

## Working with the design

- Read the relevant DESIGN.md sections before building a feature.
- If the implementation has to differ from the design, update DESIGN.md in the same change. If the difference is a product decision (what the user sees or can do), ask first.
- Build in the order given in §17 Milestones.
- Record spike findings in DESIGN.md, replacing the spike note.

## Backlog

The backlog is the open GitHub issues on `reapazor/Claudette` assigned to `reapazor`. The `issues` skill (`.claude/skills/issues/SKILL.md`) lists them, plans one and builds it once the user says go. A pull request that resolves one says `Fixes #N`; don't comment on the issue itself.

## Stack

- .NET 10, C# with nullable reference types on and warnings treated as errors, `.editorconfig`'s code style included (unused usings, members and parameters, readonly fields, field naming). Package versions live in `Directory.Packages.props`.
- Avalonia 12 with MVVM (CommunityToolkit.Mvvm). Markdown is rendered with LiveMarkdown.Avalonia.
- Layout (§13):
  - `src/Claudette.Core`: sessions (with rewinding files, MCP servers' status and their requests for input), protocol (with typed views of the system messages Claudette reads, `Protocol/SystemNotices`, hook callbacks, protocol logs with secrets redacted, and Diagnostics counts), permission rules, folder trust (what a folder's own Claude Code configuration would run, `Claude/FolderTrust`), sign-in (`claude auth login`/`logout` and the sign-in control requests), install checks, settings and state stores and settings sync, transcripts and History (with the search through Claude's replies), the session library and leases, diffs and changed files (with reverting a hunk or file), external diff tools, git (project identity, working tree, worktrees: `Git/GitWorktrees`, and which to clean up: `Git/WorktreeCleanup`), check-in timing, Claude Code updates, source builds (copies to run from, noticing new builds, restart snapshots), Claudette's own updates (`Updates/`: versions, the GitHub release feed, the downloader; `IAppInstaller`), the composer's data (`Composer/`: slash commands, the `@` file index and matcher, attachments and large pastes, drafts and the stash: `DraftStore`), Perforce (`Perforce/`: workspace detection, the ticket keeper, changelists; the passwords' store is `Credentials/ICredentialStore`), the user environment (`Processes/UserEnvironment`: the environment of `claude` and the user's other programs, with the login shell's merged in; `ILoginShell`), project tools (`ProjectTools/`: the providers and detection, the Unreal, Unity and Godot providers, jobs, `claudette.json` actions and links, `.bat` quoting; `ISystemProcesses`, `IUnrealEngineRegistry`), Remote Control (`RemoteControl/`: the `remote_control` request and its replies, eligibility, the presence file; `ISleepBlocker`), starting at login (`LoginItems/`: which copy of Claudette the login entry starts, and the command it runs; `ILoginItems`), Claude's service status (`Status/`: the status page's summary, which services and incidents count, and which tab errors call for a check), scratch pads (`ScratchPads/`: which pad a folder's project has, its file and revisions, syncing it through the session library, conflict copies), and accessibility (`Accessibility/`: reduced motion, Claude Code's `prefersReducedMotion`, the zoom steps; `ISystemMotion`).
  - `src/Claudette.Usage`: plan usage parsing, the SQLite usage history (`UsageStore`, with its file and schema in `UsageSchema`), burn rate and projection, alerts, the polling schedule, the file machines share their plan usage in (`UsageSharing`). No UI.
  - `src/Claudette.Platform`: OS-specific code: the process monitor (Job Objects on Windows, `/proc`, `ps`), notifications and the Dock/taskbar badge, the icon's animation and the taskbar flash (WinRT toasts, `UNUserNotificationCenter`, `notify-send`), the Windows jump list, the single-instance pipe, the OS credential store for Perforce passwords (Credential Manager, the Keychain through Security.framework, `secret-tool`), processes by name for project tools (`Processes/SystemProcesses`), Unreal's registry entries (`ProjectTools/`), the installers for Claudette's own updates (`Updates/`: the MSIX update through `PackageManager`, and `update-helper.sh`, which swaps `Claudette.app` on macOS), reading the login shell's environment on macOS and Linux (`LoginShell/`, behind Core's `ILoginShell`), keeping the computer awake while tabs are connected to the Claude app (`Power/`: `SetThreadExecutionState`, `caffeinate`, `systemd-inhibit`; behind Core's `ISleepBlocker`), the login entries (`LoginItems/`: the Run key and the MSIX's startup task, a LaunchAgent, an XDG autostart file; behind Core's `ILoginItems`), and the OS's reduce-motion setting (`Accessibility/`: `SPI_GETCLIENTAREAANIMATION`, `accessibilityDisplayShouldReduceMotion`, GNOME's `enable-animations`; behind Core's `ISystemMotion`). No UI. Windows and macOS APIs are called through source-generated COM interop, P/Invoke and the Objective-C runtime, so the project stays a plain `net10.0` library.
  - `src/Claudette.App`: the Avalonia UI.
    - `ShellViewModel` holds the tab groups and History, with a partial for the command palette and the next tab waiting (`CommandPaletteViewModel` filters the palette), and partials for worktree tabs and the hosts it implements. Three parts are classes of their own: `SessionOpener` opens a session from History (this machine's copy or the library's, taking over or opening a copy, conflict copies) through `ISessionOpenerHost`, which the shell implements in `ShellViewModel.Hosts.cs`; `ShellLayout` (`shell.Layout`) holds the sidebar's width and collapse and the side panel's width every tab shares; and `GroupColors` chooses a new group's color and remembers a folder's.
    - `TabViewModel` is one session, split into partial files for the library, notifications, the composer (with `PromptRecall`), drafts, the stash and quoting, messages held until the turn ends, sign-in, folder trust, agents, running tasks, the working line, auto-continue, Ultracode and output styles, the mark and the reviewed icon over it (`TabViewModel.Mark.cs`; `Views/MarkIcon` draws both), worktrees and extra folders, the git branch on its row, the side panel's pages, the scratch pad, rewind and branch, find and export. The scratch pad is a `ScratchPadViewModel` that `Services/ScratchPadService` shares between the tabs of a project. Six areas are child view models the tab owns: `ProcessMonitorViewModel` (`tab.ProcessMonitor`), `ChangedFilesViewModel` (`tab.ChangedFiles`, its rows in `Files`), `ProjectToolsViewModel` (`tab.ProjectTools`, whose menu `ProjectMenu.Build` makes, and `ProjectRunsViewModel`, `tab.ProjectTools.Runs`: the runs of its jobs in `Items`, each a `ProjectRunViewModel` with its log, the running job and its process tree), `RemoteControlViewModel` (`tab.RemoteControl`), `PerforceViewModel` (`tab.Perforce`: ticket handling and the changelist) and `ContextViewModel` (`tab.Context`: the context indicator and ring, from `ContextIndicator.From`, and the token counts). Each gets what it needs from the tab through its own small host interface (`IProcessMonitorHost` and so on) over the `ITabAreaHost` they share, which the tab implements in `TabViewModel.Hosts.cs` (the runs get theirs from project tools, `IProjectRunsHost`); where one area needs another, such as the process monitor showing the project job's processes, it goes through the tab. `McpServersViewModel` (`tab.McpServers`, the MCP page) takes the session and a way to open links rather than a host. `SettingsViewModel` is the Settings window: the categories, the search box and the page shown, with partials for the selected tab's project group and the version at the sidebar's foot. Each category is a page in `ViewModels/Settings/` (`GeneralPage`, `ClaudeCodePage` and so on, on `SettingsPage`, sharing a `SettingsContext`), with its own settings, search entries and **Reset to defaults**, and a view in `Views/Settings/`. The project group's pages (`ProjectLinksPage`, `ProjectActionsPage`, `ProjectToolChoicesPage`) show `ProjectSettingsViewModel`. Keyboard and Quick suffixes record shortcuts through the context's `ShortcutRecorder`.
    - `Conversation/ConversationBuilder` turns session events into conversation items, with `HookRuns` (the hook run rows), `PendingPrompts` (the prompt and MCP input cards Claude Code can still withdraw) and `StreamingBlocks` (the reply and thinking rows still streaming); `PromptItems` are the permission, question and plan cards. `Conversation/AgentMap` and `AgentNode` are the agent map (§18), kept by `ConversationBuilder` from the same routing as the subagent groups, as is `Conversation/RunningTasks`, the tasks Claude Code keeps running in the background. `Conversation/TodoList` is the pinned to-do list and the Tasks page; `ConversationSearch` is find, `ConversationExport` the Markdown and HTML export, and `McpInputItems` the cards for MCP servers asking for input.
    - `Services/UsageTracker` and `Services/LibraryService` connect the usage engine and the session library to the tabs; `Services/RemoteControlService` holds what the tabs share about the Claude app (eligibility, the presence file, keeping the computer awake); `Services/ServiceStatusService` checks Claude's status page for the header's dot and banner; `Services/DraftService` keeps the tabs' drafts and the stash on disk; `Services/WorktreeCleanupService` removes worktree tabs' worktrees as Settings → General says.
    - `Views/TabView` is one tab: the conversation and its templates, find, the prompts over it and the side panel's width. `SidePanelView` (the side panel's pages, with `ScratchPadView` for the scratch pad's) and `ComposerView` (the composer, with `ComposerView.Assist.cs` for the autocomplete and attachments) sit inside it, `ScratchPadAdd` is the conversation's **Add to scratch pad** (the menu of selected text and the code blocks' button), and the styles all three share are in `Themes/ConversationStyles.axaml`. `SettingsWindow` shows one page view from `Views/Settings/` at a time. Shared UI plumbing lives beside the view models: `UiTicker` and `UiTimeout` (timers on the injected clock), `InlineConfirmation` (a step that asks first) and Core's `Formats` (times, durations and names as the UI writes them).
  - `tests/`: `Claudette.Core.Tests` (unit and protocol replay), `Claudette.Usage.Tests`, `Claudette.Platform.Tests`, `Claudette.App.Tests` (view models, and a RealCli test that drives a tab with the real `claude`), `Claudette.App.UiTests` (rendered views, headless, with Verify snapshots), `Claudette.IntegrationTests` (real processes, the recording tests and the Live suite).
  - `tools/`: `Claudette.FakeClaude` (the `fake-claude` test double), `Claudette.MockApi` (a mock Messages API), `Claudette.Fixtures` (record mode: a protocol log to a cleaned fixture) and `Claudette.Demo` (`claudette-demo`: demo projects, tabs and usage history to run Claudette on with `fake-claude`, and `screenshots.ps1`, which takes the README's screenshots in `docs/screenshots/` from it).
  - `compat/`: the compatibility surface list, check script and snapshots (§16).
  - `packaging/`: the MSIX and `.dmg` build scripts, manifest, `Info.plist`, entitlements and icons (`icon/build-icons.mjs` draws them all); `.github/workflows/package.yml` runs them (§2, "Packaging and signing").
- `Claudette.Core`, `Claudette.Usage` and `Claudette.Platform` must not reference Avalonia.
- Development happens on Windows, but the app must also run on macOS and Linux. Keep OS-specific code behind interfaces, in `Claudette.Platform`.
- In XAML:
  - Use the app's own color tokens from `App.axaml` (`MutedTextBrush`, `DividerBrush` and so on), not Fluent's internal resource names.
  - The Claude style (Settings → Appearance → Style) overrides surface, text, accent and caution tokens from `Themes/ClaudeColors.axaml`, and `Themes/AppColors` gives Fluent a matching palette. A new token of those kinds needs a Claude value there too, in light and dark. Its shapes are styles under the `claude` class (as Density's are under `compact`); put a view's own look in a style rather than in attributes, so the class can change it.
  - Reference `Application.Resources` from `Application.Styles` with `DynamicResource`, because styles load before resources.
  - Give icon-only buttons an `AutomationProperties.Name`.
- The visual reference is Claude Code's VS Code extension for the Standard style, and the Claude apps (iOS, claude.ai) for the Claude style (§3, "Visual style").

## Rules that keep the code testable (§15)

- Talk to Claude Code only through `IClaudeTransport`.
- Start processes only through `IProcessLauncher`.
- Use the injected `TimeProvider` for anything time-based. Never use `DateTime.Now`, `DateTime.UtcNow` or real delays in logic.
- Never hard-code paths to the app data folder, the session library or `~/.claude`. Inject them.
- Secrets (Perforce passwords) go only to the OS credential store (`ICredentialStore`) and to child processes on standard input (`ProcessRunner`'s `inputLine`). Never in arguments, settings, state or logs.
- The build enforces these: `src/BannedSymbols.txt` bans the clock, sleeps, timeouts without a `TimeProvider`, `Process.Start`, `new HttpClient` and `Environment.SetEnvironmentVariable` under `src/`. Don't add a `#pragma warning disable RS0030` unless the code is the one place that has to (like `ProcessLauncher`), and say why on the same line.

## Claude Code integration (§13, §16)

- Any new use of something from Claude Code goes in `compat/surface.yaml` in the same change. That includes flags, environment variables, message types or fields, control requests, settings keys, file paths and command output.
- Prefer documented behavior. If you rely on undocumented behavior, mark the entry `undocumented` and record a fallback.
- Always launch `claude` with an environment from `ClaudeEnvironment` (`ClaudeEnvironment.From`, starting from `UserEnvironment.GetAsync`), which removes the session variables Claude Code sets for its child processes (`ClaudeEnvironment.SessionVariables`, §13). Otherwise a `claude` started from inside a Claude Code session behaves as a child session. Don't strip by prefix: `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_USE_BEDROCK` and similar are user configuration and must pass through.
- Start the other programs Claudette runs for the user (git, `p4`, diff tools) through `UserEnvironment.ApplyAsync` (or `Apply`), so they get the same environment and `PATH` as `claude`, including the login shell's (§13, "Login shell environment").
- Read Claude Code's fields in Core: give a message or `system` subtype a typed view (as `SystemMessage.HookRun`) rather than reading `Raw` in the App.
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
- Tests never reach the network. Claudette's own requests (the update check and download) go through `AppServices.Http`; `TabTestHarness` gives it a handler that refuses every request unless a test passes a fake one. Never run a real install in tests: use a fake `IAppInstaller`.
- Tests that run the real `claude` binary are tagged `[Trait("Category", "RealCli")]` and must:
  - point it at `MockAnthropicApi` with `ANTHROPIC_BASE_URL` and a dummy `ANTHROPIC_API_KEY`,
  - set `CLAUDE_CONFIG_DIR` to a temporary folder,
  - set `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`,
  - never read or write the real `~/.claude`,
  - skip themselves (`Assert.SkipWhen`) when Claude Code isn't installed.
- Tests tagged `[Trait("Category", "Live")]` use real tokens. Only run them when asked to. They read `CLAUDETTE_LIVE_API_KEY` (a separate, spend-limited key) and `CLAUDETTE_LIVE_CONFIG_DIR` (a signed-in config folder) and skip without them.
- Never use a real Perforce server or real Perforce credentials: use `FakeP4` (`tests/Claudette.Core.Tests/Support/`). The macOS Keychain round trip runs only with `CLAUDETTE_TEST_KEYCHAIN=1`.
- UI snapshots: a changed view writes `*.received.txt` next to its `*.verified.txt` in `tests/Claudette.App.UiTests`. Read the difference; if the change is intended, rename the received file over the verified one. In a UI test, `await Verify(...)` resumes off the UI thread, so make it the last step.
- Test time-based behavior with `FakeTimeProvider`, not sleeps. To wait for real work (a process, a background thread, rendering), use `Waiting.UntilAsync` (`tests/Shared/`, in every test project) or `TabTestHarness.Eventually`, which go by the clock; never a loop count or a fixed delay. Where something must not happen, wait for the moment it would have; only when nothing marks that moment, use `Waiting.NeverAsync`.
- Recorded protocol fixtures live in `tests/Claudette.Core.Tests/Fixtures/protocol/<claude-version>/`. Remove paths, emails and account details before checking one in: record them with `ProtocolRecordingTests` (`CLAUDETTE_RECORD_FIXTURES=<folder>`, against the mock) or convert a protocol log with `tools/Claudette.Fixtures`, both of which clean them, then read the result before committing.
- xunit.v3 stays on 3.2.x until `Avalonia.Headless.XUnit` supports 4.x.

## Commands

```sh
dotnet build Claudette.slnx
dotnet test Claudette.slnx --filter "Category!=Live"                  # everything free (RealCli skips without Claude Code)
dotnet test Claudette.slnx --filter "Category!=RealCli&Category!=Live" # no Claude Code needed
dotnet test Claudette.slnx --filter "Category!=Live" --collect "Code Coverage;Format=cobertura" --results-directory TestResults  # coverage, locally only (not in CI)
dotnet run --project src/Claudette.App -- --folder <path>             # uses your real account: messages cost usage
```

Each project has a `packages.lock.json`, and CI restores in locked mode: after changing a package, restore and commit the lock files with it.

`global.json` pins the .NET SDK (10.0.400, or a later 10.0.4xx). If `dotnet` says it isn't found, don't work around it and don't change `global.json`:

- If `~/.dotnet` (`DOTNET_ROOT`) has it, use that `dotnet` by putting the folder first on `PATH`. A `dotnet` elsewhere on `PATH`, such as `C:\Program Files\dotnet`, only sees its own SDKs.
- Otherwise install it there with Microsoft's install script from `https://dot.net/v1/`: `dotnet-install.ps1 -JsonFile global.json -InstallDir ~/.dotnet` on Windows, `dotnet-install.sh --jsonfile global.json --install-dir ~/.dotnet` elsewhere. `.claude/hooks/session-start.sh` does the same for web sessions.

Run the app without using tokens by pointing it at the mock API:

```sh
dotnet run --project tools/Claudette.MockApi -- 8787    # in one terminal
# in another, with these set: ANTHROPIC_BASE_URL=http://127.0.0.1:8787  ANTHROPIC_API_KEY=sk-ant-mock
#                             CLAUDE_CONFIG_DIR=<a temp folder>         CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
#                             CLAUDETTE_HOME=<a temp folder>   (keeps Claudette's settings and saved tabs out of your profile)
dotnet run --project src/Claudette.App -- --folder <path>
```

A source build (anything run from a `bin` folder in this checkout, including `dotnet run`) copies itself to `builds` in the data folder and runs from there, so you can rebuild while it runs. It offers to restart into each new build, keeping every tab and draft, and takes its tabs back if the new build doesn't start (§9, "Working on Claudette"). `dotnet run` returns once the copy has started. Set `CLAUDETTE_RUN_IN_PLACE=1`, or attach a debugger, to run in place instead.

Stop the app by closing its window, not by killing the process: closing interrupts running turns and stops each tab's `claude` and everything it started. A killed app leaves them running; the Job Objects deliberately don't kill on close, so a user can keep a tab's processes (§4).

View model tests use `tests/Claudette.App.Tests/Support/TabTestHarness.cs`: a scripted Claude Code connection, a fake clock and an inline dispatcher.

For a populated window without an account (five tabs over three git projects, changed files, marks, and a usage header with history and a projection), `dotnet run --project tools/Claudette.Demo -- <folder>` writes the demo and prints the environment to start Claudette with; see the header of its `Program.cs`. On Windows, `tools/Claudette.Demo/screenshots.ps1 -Projects <short folder>` retakes the README's screenshots in each style and theme. The projects' paths show in the conversation, so give it one that says nothing about you, such as `C:\Demo`.

To see the usage header (meters, sparkline, projection) without a subscription, set Settings → Claude Code → Path to claude to `fake-claude` (built next to the integration tests) and run with `FAKE_CLAUDE_USAGE=demo`: its plan starts at 35% of the session and climbs 0.6% a minute. `FAKE_CLAUDE_USAGE=<path>` answers with a recorded `get_usage` response instead.

Prompts the mock understands: `WRITE_FILE <path>`, `EDIT_FILE <path>`, `RUN_BASH <command>`, `ASK_QUESTION`, `EXIT_PLAN` (in Plan mode), `SLOW`, `API_ERROR` (two 529s, then a reply), `SUBAGENTS` (a nested fan-out with a permission prompt inside a subagent), `LONG_AGENT` (a subagent to stop; in the background with `BACKGROUND`) and `AGENT_REPLY <text>`; anything else gets `pong`. The environment may set `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`, which passes through to `claude` as user configuration, so a RealCli test that needs nesting sets it.

`fake-claude`'s prompts: `ASK_PERMISSION`, `SLOW`, `CRASH`, `SPAWN [seconds] [busy]` (a child process for the process monitor), `SILENT` (waits for a check-in), `HANG`, `AUTH_FAIL`, `LIMIT [minutes]` (a turn stopped at the session limit, which resets in that many minutes), `RUN_BASH <cmd>` (calls back PreToolUse hooks) and `SUBAGENTS`; a message with images gets a reply naming them. It keeps its sign-in in `fake-claude-auth.json` in `CLAUDE_CONFIG_DIR`: run with `FAKE_CLAUDE_LOGGED_IN=0` to see the sign-in screen, and use `FAKE_CLAUDE_LOGIN=auto|code|fail|hang` to exercise sign-in without an account. The header of its `Program.cs` lists the rest. An API key has no plan limits, so the usage header stays empty against the mock; the usage tests cover it.

Compatibility check (§16): `node compat/check.mjs detect`, then `report` or `update-snapshots`. See the header of `compat/check.mjs`.

Protocol fixtures (§15): `CLAUDETTE_RECORD_FIXTURES=<folder> dotnet test tests/Claudette.IntegrationTests --filter "FullyQualifiedName~ProtocolRecordingTests"` records the scenarios against the mock; `dotnet run --project tools/Claudette.Fixtures -- <protocol.log> <fixture.jsonl> --root <project folder>` converts a log from Settings → Advanced → Log protocol traffic.
