# Claudette Design

Claudette is a .NET desktop client that wraps [Claude Code](https://code.claude.com/docs) in a friendly, native-feeling interface. It runs several Claude Code sessions side by side in tabs and makes it obvious how fast you are using up your plan's limits.

Claudette does not replace Claude Code. It runs the real `claude` CLI as a child process for each tab, and draws the conversation with native controls instead of a terminal.

## 1. Goals

- **Nicer than a terminal.** Show messages, tool calls, diffs and permission prompts as real UI, using each platform's native look and window behavior.
- **Many sessions at once.** Each tab is an independent Claude Code session with its own working folder.
- **Token burn awareness.** Always show how much of the current session, weekly and model-specific limits you've used, how fast you're using them, and when you'll run out at the current rate.
- **Easy to stop.** It should always be one click (or one key) to stop what Claude is doing in the current tab.
- **Pick up where you left off.** Tabs come back when Claudette restarts, and a session library in a synced folder lets another machine continue the same sessions.

### Non-goals (v1)

- Calling the Claude API directly. All model work goes through Claude Code.
- Being a code editor or IDE. Files open in the user's own editor.
- Replacing Claude Code's configuration. Claudette reads and respects `~/.claude` settings, `CLAUDE.md`, MCP servers and hooks, but doesn't try to manage them.
- Linux polish. It should build and run on Linux, but only Windows and macOS are tested and packaged.

## 2. Platform & Tech Stack

| Area | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS) | |
| UI | Avalonia | One codebase for Windows, macOS and Linux. |
| Look & feel | Fluent theme on Windows, macOS-style theme on macOS | Follows the OS light/dark setting and accent color. Mica backdrop on Windows 11; native title bar, traffic lights and menu bar on macOS. |
| Pattern | MVVM with CommunityToolkit.Mvvm | |
| Markdown | Markdown.Avalonia (or similar) | For assistant messages. Code blocks need syntax highlighting. |
| Diffs | AvaloniaEdit with a diff renderer | |
| Dependency | Claude Code CLI | Must already be installed. Claudette finds `claude` on `PATH` (or a path set in Settings), checks its version on launch against a minimum supported version, and shows a setup screen if it is missing or too old. Sign-in is handled inside Claudette (see [§11](#11-sign-in)). |
| Packaging | Windows: MSIX or installer. macOS: signed, notarized `.app` in a `.dmg`. | |

## 3. Main Window

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ● ● ●  Claudette                                                           │
│ ┌ Session ███████████░░░░░░░ 62%  resets 2h 14m ┐  Weekly ███░░░ 38%       │
│ └ ╱╱╱ at this rate: limit hit in 1h 05m !       ┘  Fable  █░░░░░ 12%       │
├────────────────────────────────────────────────────────────────────────────┤
│ ▾ api [● refactor auth ×] [◌ fix login bug ×] [+] │ ▾ docs [! intro ×] [+] │
├──────────────────────────────────────────────────────┬─────────────────────┤
│                                                      │ Changed files       │
│  You: refactor the auth middleware…                  │  M src/auth.cs  +12 │
│                                                      │  A src/token.cs +40 │
│  Claude: I'll start by reading…                      │                     │
│   ┌ Read  src/auth.cs ─────────────────────────┐     │                     │
│   └────────────────────────────────────────────┘     │                     │
│   ┌ Edit  src/auth.cs  (+12 −3)  [view diff] ──┐     │                     │
│   └────────────────────────────────────────────┘     │                     │
│   ┌ Allow Bash: dotnet test? ──────────────────┐     │                     │
│   │ [Allow]  [Always allow]  [Deny]            │     │                     │
│   └────────────────────────────────────────────┘     │                     │
├──────────────────────────────────────────────────────┴─────────────────────┤
│ ~/src/api  Opus ▾  Effort: High ▾  Mode: Default ▾  Ctx 41%  1.2M tok      │
│ ┌─────────────────────────────────────────────────────────────┐            │
│ │ Message Claude…                                             │ [ ■ Stop ] │
│ └─────────────────────────────────────────────────────────────┘            │
│ Suffixes ▾  [Clarify first ×] [Test it (kept) ×]                           │
└────────────────────────────────────────────────────────────────────────────┘
```

From top to bottom:

1. **Usage header.** Always visible. Session usage is the most prominent item; weekly limits are smaller. See [§6](#6-token-burn-awareness).
2. **Tab strip.** One tab per session, with a status icon, grouped by working folder. See [§4](#4-tabs--sessions).
3. **Conversation.** The selected tab's conversation. See [§5](#5-conversation-view).
4. **Side panel (collapsible).** Files changed in this tab ([§8](#8-file-changes--diff-view)), and optionally its running processes ([§4](#process-monitor)).
5. **Composer.** Where you type to the selected tab, plus the Stop button and per-tab controls.

## 4. Tabs & Sessions

- A tab is one Claude Code session, run as its own `claude` process.
- Every tab has a **working folder**, picked when the tab is opened, usually from the recent folders list ([Opening a tab](#opening-a-tab)). Several tabs can use the same folder.
- **Naming**
  - By default a tab uses the name Claude Code gives the session, the same name it shows as the terminal tab title (see [session naming](#integration-with-claude-code)).
  - Until Claude Code has named it, the tab shows the folder name.
  - The user can rename a tab (double-click, or right-click → Rename). A user-chosen name is never overwritten by Claude Code. "Reset name" goes back to the automatic name.
- **Status icon** on each tab:
  - Idle
  - Working (animated)
  - Needs input: a permission prompt or question is waiting (highlighted, so it stands out from any tab)
  - Finished while in the background (unread dot)
  - Error or process exited
- **Model and effort** are easy to see for every tab. The tab shows a small badge (for example `Opus · High`), and the composer bar shows the same thing in full for the selected tab. See [Model & effort](#model--effort).
- The tooltip shows the full folder path, model, effort level, session start time and tokens used.
- **Token stats per tab.** Each tab keeps a running count of the tokens it has used:
  - Input, output, cache write and cache read tokens, split by model when the session used more than one.
  - Two time spans: **this session window** (since the current 5-hour window started, which is the part that counts against the session limit) and **all time** for this tab's session.
  - An estimated cost, labeled as an estimate (Claude Code computes it at list price; it is not your bill).
  - Shown in short form in the composer bar (for example `1.2M tok`). Click it for a popover with the full breakdown and a small per-turn chart.
  - Saved with the tab, so the counts survive app restarts and session resumes.
  - The same numbers feed the "which tab is burning the most" view in the Usage panel ([§6](#6-token-burn-awareness)).
- **Grouped by folder.** Tabs that share a working folder sit together in a group, like browser tab groups:
  - Each group has a label with the folder name and a color. The color is picked automatically and can be changed. If two folders have the same name, the label adds the parent folder (`work/api`, `personal/api`).
  - Hovering the label shows the full path. The group's `+` opens a new tab in the same folder.
  - A group can be collapsed to just its label. A collapsed group still shows the most urgent status of its tabs, such as "needs input".
  - Tabs can be dragged to reorder them within their group, and groups can be dragged to reorder them. A tab can't be dragged into another group, because its folder is fixed.
  - A group with a single tab still gets a label, so the strip always looks the same.
- Closing a tab that is working asks for confirmation, then stops the process. Right-clicking a group label gives **Close group**.
- **Pinned tabs** come back every time Claudette launches, resuming their sessions.
  - Pin or unpin from the tab's right-click menu. A pinned tab shows a pin icon and sits at the start of its folder group.
  - **Close group** and **Close other tabs** skip pinned tabs.
  - Closing a pinned tab asks *"This tab is pinned. Close and unpin it?"*
  - Unpinned tabs aren't restored unless **Also restore unpinned tabs** is on in Settings. See [§9](#restore-on-launch) for what's restored.
- Keyboard: `Ctrl/Cmd+T` new tab, `Ctrl/Cmd+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle, `Ctrl/Cmd+1…9` jump to a tab.

### Opening a tab

`Ctrl/Cmd+T`, or the `+` at the end of the tab strip, opens the **New tab** picker. A group's own `+` skips the picker and opens a tab in that group's folder.

The picker shows:

- A **search box** that filters the list by folder name or path as you type.
- **Favorites**: folders starred by the user, always at the top.
- **Recent folders**, most recently used first. Each entry shows:
  - The folder name and its path, shortened with `~`.
  - The current git branch, if it's a repo.
  - When it was last used, and how many tabs are open in it now.
- **Browse…**, which opens the native folder picker for any other folder.
- **Open from History…**, which opens History ([§9](#history)) to resume a past session instead of starting a new one.

Using the picker:

- Arrow keys and `Enter` pick an entry, and `1`–`9` pick one of the first nine.
- Right-click an entry for **Add to favorites** / **Remove from favorites**, **Remove from recents**, **Reveal in Finder/Explorer** and **Copy path**.
- A folder that no longer exists is shown dimmed, marked "not found", and can be removed.

**How the list is kept.**

- A folder is added (or moved to the top) whenever a tab opens in it.
- The list keeps the 20 most recent folders by default; the number can be changed in Settings. Favorites don't count toward the limit and are never dropped.
- Recent folders and favorites belong to this machine, because paths differ between machines. They aren't part of settings sync.

**Other ways in.**

- Dragging a folder from Finder or Explorer onto the tab strip opens a tab there.
- On macOS, **File → Open Recent** and the Dock icon's menu list recent folders. On Windows, the taskbar jump list does the same.
- Choosing any of these opens a new tab in that folder.

### Process monitor

An optional view of the processes each tab has started, such as test runs, dev servers, builds and MCP servers, with their CPU and memory use. It's off by default and turned on in Settings → Processes. Each tab can also show or hide it.

- **Summary.** When it's on, the composer bar shows a compact summary for the tab, for example `3 procs · 42% CPU · 1.1 GB`. The tab itself gets a small activity icon while any child process is using noticeable CPU.
- **Processes panel.** A second page of the side panel, next to Changed files. It shows a tree of the tab's processes, starting from its `claude` process, with these columns:
  - Name and PID.
  - CPU %, following the platform's convention: on Windows, 100% means all cores, as in Task Manager; on macOS, 100% means one core, as in Activity Monitor.
  - Memory (working set / resident size).
  - Running time.
  - **Command line.** Can be hidden in Settings, because command lines sometimes contain tokens or passwords. It's truncated in the table; hover for the full text, or use **Copy**.
- **Link to the conversation.** When a process belongs to a Bash tool call or a background task, its row shows which one, and clicking it scrolls to that card in the conversation. Claude Code's `task_started` events give the task ID and tool call ID, and Claudette matches them to the new process.
- **Actions.**
  - **Stop.** For a Claude Code background task, stop it through Claude Code (`stopTask`) so Claude knows it ended. Otherwise, end the process, first gracefully and then forcefully. Either way, confirm first.
  - **Copy command line.**
  - **Reveal executable.**
- **Sampling.** Every 2 seconds while the panel is visible, and every 10 seconds when only the summary is showing. Nothing runs when the monitor is off.
- **How processes are tracked.**
  - Windows: each tab's `claude` process runs in its own Job Object, so every descendant is tracked, even if its parent exits. CPU and memory come from the process APIs, and command lines from WMI (`Win32_Process`).
  - macOS and Linux: Claudette walks the process tree from the tab's `claude` process (libproc / `sysctl` on macOS, `/proc` on Linux). A process that detaches and gets re-parented (for example a daemonized dev server) drops out of the tree. Claudette keeps listing any process it has already seen, marked "detached", until it exits.
- **Cleanup.** The same tracking lets Claudette end a tab's whole process tree when the tab closes, so no orphaned dev servers are left running. If processes are still running, the close confirmation lists them, with an option to leave them running.

## 5. Conversation View

The conversation is drawn from Claude Code's structured output stream, not from terminal text.

| Item | How it's shown |
|---|---|
| User message | Right-aligned bubble; attached images as thumbnails. |
| Assistant text | Markdown with syntax-highlighted code blocks and copy buttons. Streams in as it's generated. |
| Thinking | Collapsed "Thinking…" row; click to expand. |
| Tool call | Compact card: tool icon, name and a one-line summary (file path, command, search pattern). Expand to see full input and output. |
| Edit / Write | Card shows `+added −removed`; expand for an inline diff, or open it in the diff view. |
| Bash | Shows the command; output is collapsed and uses a monospace font. |
| Subagent (Task) | Nested, collapsible group holding that agent's tool calls. |
| To-do list | Pinned checklist at the top of the conversation while it exists. |
| Permission prompt | Inline card with buttons. See [§7](#7-permission-prompts). |
| Errors / API retries | Inline warning row. |
| Turn summary | Small footer after each turn: duration, tokens, model. |

Scrolling follows new output unless the user has scrolled up; a "Jump to latest" button appears when they have.

### Composer

- Multi-line text box. `Enter` sends, `Shift+Enter` adds a new line.
- **Stop.** A Stop button replaces Send while Claude is working, and `Esc` does the same. Stopping interrupts the current turn; it does not close the session.
- You can type and send while Claude is working; the message is queued and delivered to the session.
- `/` opens slash-command autocomplete (built-in plus the project's custom commands).
- `@` opens file autocomplete for the tab's working folder.
- Drag and drop or paste images and files to attach them.
- Per-tab controls in the bar above the composer: working folder (read-only), model, effort level, permission mode, context window usage %, tokens used.

### Quick suffixes

Saved snippets of instructions that can be added to a message in one click, such as *"Ask clarifying questions before you start."*

- **Picking one.** A **Suffixes ▾** button next to the text box opens a dropdown of saved suffixes. `Ctrl/Cmd+Shift+S` opens it from the keyboard, and the first nine entries can be picked with `1`–`9`.
- **Chips.** A picked suffix appears as a chip under the text box instead of being pasted into the text, so the message stays easy to edit. Several can be picked at once. Click a chip's `×` to remove it.
- **Sending.** When the message is sent, the suffixes are appended in the order shown, separated from the message by a blank line. The sent message in the conversation shows the full text, with the suffix part in a lighter style. Sending with only suffixes and no typed text is allowed.
- **Keeping one on.** Right-click a chip → **Keep on this tab** adds that suffix to every message in the tab until it's turned off. A kept chip is marked "(kept)". Kept suffixes are saved with the tab.
- **Own shortcut.** Each suffix can have its own keyboard shortcut that adds it directly.
- **Managing them.** Settings → Quick suffixes: add, edit, reorder and delete. Each suffix has a short **label** (shown in the dropdown and on the chip) and its **text**. Dropdown entries also include **Edit suffixes…** as a shortcut to Settings.
- **Built-in examples.** These can be edited or deleted, and **Reset to defaults** brings them back:

  | Label | Text |
  |---|---|
  | Clarify first | Ask clarifying questions before you start. |
  | Plan only | Don't change any files yet. Explain your plan and wait for me to approve it. |
  | Keep it small | Keep the change as small as possible and don't refactor unrelated code. |
  | Test it | Run the relevant tests when you're done and fix any failures. |
  | Explain | Explain what you changed and why when you're done. |

### Model & effort

- The composer bar shows the tab's current **model** and **effort level** as two clickable indicators. They always show what the session is actually using, including changes made with `/model` or other slash commands.
- **Effort.** Clicking the effort indicator opens a dropdown of the levels the current model supports. Picking one applies it to the session right away, starting with the next turn. No confirmation is needed.
- **Model.** Clicking the model indicator opens a dropdown of the available models. Picking a different one shows a confirmation first, and the choices are **Switch model** and **Cancel**:
  - *"Switching models resets this tab's cached context. The new model has to re-read the whole conversation, so your next message will use more of your limits."*
  - If the new model doesn't support the current effort level, the effort falls back to that model's default, and the dialog says so.
  - The conversation itself is kept. Claude Code applies a model switch in place, even in the middle of a turn.
- The model list and each model's effort levels come from Claude Code (see [Integration](#integration-with-claude-code)), not a list built into Claudette.
- New tabs start with the default model and effort from Settings.

### Check-ins on long turns

If a turn runs for a long time, Claudette can ask Claude how it's going, so a tab doesn't sit stuck and burn usage without anyone noticing.

- **Triggers.** Either of these starts a check-in; each can be set or turned off:
  - **Run time:** the turn has been running for longer than a set time (default 15 minutes).
  - **Quiet time:** the turn has produced no new output for a set time (default 5 minutes).
- **What happens.** Claudette sends a message to the session, the same as if the user had typed it while Claude was working. Claude Code delivers it inside the running turn, and the turn continues. The default message is:
  > *Everything OK? Give me a one or two sentence status update: what you're doing, and whether you're stuck or waiting on something.*
- **In the conversation.** The check-in appears as a user message with an "Automatic check-in" label, so it's clear the user didn't type it. Claude's reply appears as normal.
- **Limits.**
  - At most one check-in per interval. The timers restart after Claude replies.
  - No check-in while the tab is waiting on a permission prompt or question. That is waiting on the user, not on Claude.
  - Claude only sees the message between steps. If a single command is running (for example a long test run), the check-in is delivered when that command finishes. After two check-ins in a row get no reply, Claudette stops sending them for that turn. It marks the tab as possibly stuck and offers **Stop**.
- **Settings.** Configured in Settings → Check-ins, including the message text and an option to also send an OS notification when a check-in is sent. Each tab can override them ([§14](#per-tab-overrides)).
- Check-ins use a small amount of usage, which is counted in the tab's token stats like any other message.

## 6. Token Burn Awareness

This is Claudette's main feature: knowing how fast you're using your plan's limits without running `/usage`.

### Header meters

- **Session (5-hour window)**, the most prominent:
  - Progress bar with % used.
  - Countdown to reset ("resets in 2h 14m", with the clock time in a tooltip).
  - The bar turns amber at 75% and red at 90% (thresholds can be changed in Settings).
- **Weekly limits**, smaller and to the side:
  - Weekly limit across all models.
  - Each model-specific weekly limit the plan has (for example Fable).
  - Each shows % used and its reset day and time in a tooltip.

### Burn trendline

- A sparkline next to the session meter showing usage % over the current window.
- A dotted **projection** line continues the current rate forward.
- A plain-language line under the meter:
  - "At this rate you'll hit the limit in 1h 05m, 1h 09m before it resets" (amber/red), or
  - "On track: about 70% used when the session resets" (neutral).
- **Rate** is measured over a recent window (by default the last 30 minutes, using a moving average), so a single big turn doesn't swing the projection wildly. When nothing is running, the projection says so instead of showing a stale rate.
- Clicking the header opens a **Usage** panel with:
  - A larger chart of the current session and the past week.
  - Tokens per tab for the current window, so you can see which session is burning the most.
  - Past sessions and weeks, as far back as the stored [usage history](#usage-history) goes.

### Per-tab context

Separate from plan limits, each tab shows how full its **context window** is (in the composer bar). It warns near the auto-compact threshold and has a quick **Compact** action.

### Alerts

An OS notification (optional) when:

- Session usage crosses the warning thresholds.
- The projection says you'll hit the limit before it resets.
- A limit resets.

### Data source

| Data | Source | Notes |
|---|---|---|
| Session (5-hour) and weekly (7-day) % used, reset times | Claude Code's `rate_limits` data: `five_hour` and `seven_day`, each with `used_percentage` and `resets_at` | Documented as status line input. Only present for Pro/Max subscriptions, and only after the session's first API response. |
| Limit warnings and rejections | `rate_limit_event` in the stream-json output: `status` (`allowed`, `allowed_warning`, `rejected`), `utilization`, `resetsAt` | Doesn't say which window it refers to. Useful as an immediate signal between samples. |
| Model-specific weekly limits (e.g. Fable) | Output of `/usage` | No structured source. `/usage` can be sent to a session as a prompt and comes back as text, which Claudette would parse. Brittle, so this meter is optional in Settings. |
| Tokens per turn and per tab | `usage` and `modelUsage` on each `result` message; per-call `usage` on `assistant` messages (de-duplicated by message ID) | Documented. |
| Context window % | The `get_context_usage` control request, or last-turn input tokens ÷ `contextWindow` | Documented. |

> **Spike (milestone 1):** `rate_limits` is documented as input to status line scripts, and the status line is a feature of the interactive terminal UI. Confirm how a headless session gets the same data. Options, in order of preference:
> 1. A structured field in stream-json output.
> 2. A status line command set through `--settings` that forwards its JSON input to Claudette over a local pipe, if the status line runs in headless mode.
> 3. `rate_limit_event.utilization` plus a periodic `/usage` probe from one hidden session.
>
> Don't use the undocumented endpoint behind `/usage` directly.

**Sampling.** All tabs share one account, so plan usage is tracked app-wide, not per tab. Every tab reports samples; the newest one wins.

**Idle.** When no tab is running, nothing reports new values. Claudette keeps showing the last value with an "as of" time, and still advances the reset countdown locally.

### Usage history

Claudette stores usage data locally in a SQLite file in the app data folder, so the trendline, charts and per-tab stats survive restarts. The file is per machine and isn't synced. It holds two kinds of records.

**Plan usage samples** (app-wide):

- Timestamp.
- Session (5-hour) % used and its reset time.
- Weekly (7-day) % used and its reset time.
- Model-specific weekly % (for example Fable), if that meter is on.

A sample is saved only when a value changes, and at most once a minute. These feed the trendline and projection, the weekly chart, and the header after a restart.

**Per-turn token records** (per tab):

- Timestamp.
- Tab and session ID.
- Model.
- Input, output, cache write and cache read tokens.
- Estimated cost.

These feed each tab's per-turn chart and the "which tab is burning the most" view.

**Not stored:** prompts, replies, code or any other conversation content. That stays in Claude Code's transcripts and the session library.

**Retention.** Settings → Usage → **Keep usage history** with these options:

- 1 day
- 1 week
- 1 month (the default)
- 1 year
- Forever

Records older than the chosen period are deleted at launch and once a day. With **1 day**, the weekly chart only covers the last day. The header meters and projection are unaffected, since they only need the current 5-hour window and the latest weekly value.

**Clear usage history.** A button beside the retention option. After confirming, it deletes every stored sample and per-turn record.

- The header meters fill in again at the next update from Claude Code.
- Each tab's running token totals are kept, because they're saved with the tab ([§4](#4-tabs--sessions)). A checkbox in the confirmation, **Also reset per-tab token totals**, clears those too.

Even **Forever** stays small: roughly tens of megabytes a year of heavy use.

## 7. Permission Prompts

- When Claude Code needs permission to use a tool, the tab shows an inline card with:
  - The tool and its input (the command, file path, or a diff preview for edits).
  - Buttons: **Allow**, **Always allow** and **Deny**. Deny has an optional message telling Claude what to do instead.
  - **Always allow** saves an allow rule for that tool and input pattern (for example `Bash(dotnet test:*)`) to the project's `.claude/settings.local.json`. That is Claude Code's personal, uncommitted project settings file, so the rule also applies to terminal sessions in that folder. The rule is sent as part of the permission reply, and Claude Code writes the file itself.
  - The **Always allow** button has a menu with **Allow for this session only**, which doesn't save anything.
  - Before saving, the card shows the exact rule, and the user can edit it to make it broader or narrower.
- A tab with a waiting prompt gets the "Needs input" status. If Claudette isn't focused or the tab isn't selected, it also sends an OS notification.
- Keyboard: `Ctrl/Cmd+Enter` allows, `Ctrl/Cmd+Backspace` denies the focused prompt.
- **Permission mode picker** per tab: Default, Accept edits, Plan, Bypass permissions. Bypass needs a confirmation and gives the tab a visible warning style.
- Claude Code's own allow/deny rules in settings still apply; Claudette only shows prompts that Claude Code actually asks for.

## 8. File Changes / Diff View

- A collapsible side panel lists the files changed in the selected tab's session: added, modified or deleted, with `+/−` line counts. It is built from the session's Edit/Write tool calls.
- Selecting a file opens a diff view (side-by-side or inline) with syntax highlighting.
- Actions: open in external diff tool, open in external editor, reveal in Finder/Explorer, copy path.
- If the folder is a git repo, a toggle switches to **working tree vs HEAD**. This also shows changes made by Bash commands or by the user.
- **Before content.** The first time Claude is about to change a file in a session, Claudette saves a copy of it. It does this when the Edit/Write tool call arrives, before the tool runs. That copy is the "before" side of the diff, so diffs are exact even outside a git repo.

> **Spike:** Confirm that the tool call always reaches Claudette before the file is written, including in Accept edits mode where there's no permission prompt. If it doesn't, use a `PreToolUse` hook or Claude Code's file checkpoints as the source instead.

### External diff tool

- In Settings → Diff tool, the user chooses how diffs open: **Built-in** (the default), a **preset**, or a **custom command**.
- Once a tool is set, **Open in diff tool** appears on every changed file. Double-clicking a file in the changed files panel uses the external tool instead of the built-in view.
- **Presets** are found automatically: Claudette looks in each tool's standard install locations and on `PATH`, and a preset only appears if its tool is found.

  | Tool | Windows | macOS |
  |---|---|---|
  | Beyond Compare | `BCompare.exe` | `bcomp` |
  | VS Code | `code --diff --wait` | `code --diff --wait` |
  | WinMerge | `WinMergeU.exe` | — |
  | Kaleidoscope | — | `ksdiff` |
  | Meld | `Meld.exe` | `meld` |
  | P4Merge | `p4merge.exe` | `p4merge` |
  | Your git difftool | `git difftool` (uses your git config) | same |

- A preset fills in the right arguments for the tool, including titles for each side (`auth.cs (before)` and `auth.cs (now)`). The left side is read-only where the tool supports it.
- **Custom command.** A program path plus an arguments template with the placeholders `{left}`, `{right}`, `{leftTitle}` and `{rightTitle}`. For example, Beyond Compare on Windows:

  ```
  "C:\Program Files\Beyond Compare 5\BCompare.exe" "{left}" "{right}" /title1="{leftTitle}" /title2="{rightTitle}"
  ```

- **Files passed to the tool.**
  - The "before" side is written to a temporary read-only file.
  - The "now" side is the real file in the working folder, so edits made in the diff tool are saved directly.
  - Temporary files are deleted when the tab closes.
- **Test** in Settings opens a sample diff so the user can check their command works.

## 9. Sessions: History, Restore & Sync

### Restore on launch

- **Pinned tabs** ([§4](#4-tabs--sessions)) are always restored.
- **Unpinned tabs** that were open when Claudette quit are only restored if **Also restore unpinned tabs** is on in Settings → Sessions. It's off by default.
- **What's restored** for each tab:
  - Folder, name, pinned state and group order.
  - Session ID, model, effort and per-tab overrides.
  - Suffixes kept on the tab, and token stats.
- Tabs come back in the same order and resume their sessions, with the earlier conversation loaded so you can scroll back.
- **Starting fast.** Restored tabs don't start their `claude` process until you first select them or send them a message. Launching with many pinned tabs is quick, and tabs you don't touch use no resources.
- **Old sessions.** Claude Code deletes local transcripts after 30 days by default. A pinned tab you haven't used in a while could lose its transcript, so Claudette resumes from its session library copy, which isn't affected by that cleanup. If neither copy exists, the tab says so and offers to start a new session in the same folder.
- **Missing folder.** If a restored tab's folder no longer exists (for example a deleted clone), the tab shows an error with **Choose folder…** and **Unpin and close**.
- Pinned tabs belong to this machine. On another machine, the same sessions appear in History ([below](#history)) instead.

### History

- **History** (`Ctrl/Cmd+Shift+H`, or from the new tab menu) lists past sessions, grouped by folder. Each entry shows the name/title, the machine it was last used on, last activity time, first prompt and message count.
- Search by title and prompt text.
- Opening an entry resumes that session in a new tab. The earlier conversation is loaded into the view so you can scroll back through it.
- History combines two sources:
  - Claude Code's own session storage on this machine, so it includes sessions started in the terminal.
  - Claudette's session library (below), which can include sessions from other machines.

### Session library (sync across machines)

Claudette keeps its own **session library** in a folder the user chooses (Settings → Sessions). By default it's in the app data folder. Pointing it at a folder that a sync client keeps up to date, such as Google Drive for desktop, Dropbox or OneDrive, lets other machines see and restore the same sessions. Claudette doesn't talk to Google Drive or any cloud API; it just reads and writes files, and the sync client does the rest.

**What's in the library.** One folder per session holding:

- A **session record** (JSON): the tab name, model, effort, per-tab overrides, token stats, which machine last used it and when, and the project identity (below).
- A **copy of Claude Code's transcript** (`.jsonl`), plus subagent transcripts.

Claude Code's credentials and settings are never copied.

**Writing.**

- Claudette copies the transcript into the library after each turn finishes, never while Claude Code is writing it.
- Each file is written to a temporary name, then renamed, so a sync client never uploads a half-written file.
- Library copies aren't affected by Claude Code's own cleanup of local transcripts (30 days by default), so the library also works as a longer-term archive. It has its own retention setting.

**Restoring on another machine.**

1. **Find the project.** Folder paths differ between machines (`D:\Repos\api` vs `/Users/me/src/api`), so the session record stores a project identity: the git remote URL, the branch, and the path inside the repo. Claudette looks for a matching folder among recent folders. If it can't find one, it asks the user to pick the folder and remembers the answer for that machine.
2. **Check the code.** The library moves the conversation, not the code. If the branch or commit on this machine differs from what the other machine had, or the other machine had uncommitted changes, Claudette warns: *"This session was last used on DESKTOP-01 on branch `feature/auth` at `a1b2c3d`. This folder is on `main`. Claude's earlier file changes may not be here."* The user can continue anyway or cancel and sync the code first (push/pull).
3. **Resume.** Claudette resumes from the library copy with `claude --resume <transcript-path>`, which Claude Code supports for a transcript at any absolute path.

**One machine at a time.**

- While a session is open, Claudette keeps a small lease file next to it ("in use on DESKTOP-01", refreshed every minute).
- Opening a session that another machine is actively using asks the user to either:
  - **Open a copy**, which forks it into a new session with `--fork-session`, or
  - **Take over**, after which the other machine's tab becomes read-only on its next sync.
- A lease that hasn't been refreshed in 10 minutes counts as stale.
- If the sync client creates conflict copies (for example `session (1).jsonl`), Claudette shows them in History as separate, forked entries. It never merges them.

**Privacy.** Transcripts contain code, command output and anything else Claude read in the project. When the user picks a library folder inside a known cloud-sync location, Claudette says so and asks them to confirm.

> **Spike:** When a session is resumed with `--resume <transcript-path>`, find out where Claude Code writes the new turns: back to that file, or to local project storage. If it writes locally, Claudette copies the updated local transcript back to the library after each turn, as it does for new sessions. Also confirm that transcripts recorded on one OS resume cleanly on another.

**Rejected alternative.** Pointing Claude Code's whole config folder at the cloud drive (`CLAUDE_CONFIG_DIR`) would also sync credentials and settings, and have several machines writing the same live files at once. The library copies only transcripts, only between turns.


## 10. Notifications

Native OS notifications (Windows toast, macOS User Notifications). Each type can be turned on or off in Settings:

- A background tab finished its turn.
- A tab needs permission or input.
- A tab's process errored or exited unexpectedly.
- Usage alerts (see [§6](#6-token-burn-awareness)).
- Claude Code needs you to sign in (see [§11](#11-sign-in)).
- A Claude Code update is ready (see [§12](#12-claude-code-updates)).

Clicking a notification brings Claudette to the front and goes to the relevant tab or screen. Notifications are skipped when Claudette is focused and that tab is already selected. The Dock (macOS) and taskbar (Windows) show a badge with the number of tabs needing input.

## 11. Sign-in

Claude Code keeps its own credentials. Claudette never reads or stores them; it only detects when Claude Code needs a sign-in and runs Claude Code's own sign-in flow.

### Detecting

- **On launch**, before any tab starts, Claudette runs `claude auth status`. It prints JSON and exits with 0 when signed in and 1 when not.
- **While running**, any of these from a session means "needs sign-in":
  - An `assistant` message with `error: "authentication_failed"`.
  - An `auth_status` message.
  - A session that fails to start with an authentication error.

### Signing in

- At launch, Claudette shows a sign-in screen instead of the tabs. In the middle of a session, it shows a banner across all tabs: *"Claude Code needs you to sign in."* If Claudette isn't focused, it also sends an OS notification.
- **Sign in** runs `claude auth login` as a child process, which opens the default browser to the sign-in page.
- While it waits, Claudette shows *"Finish signing in in your browser"* and stays responsive. The screen has:
  - **Open browser again**, which reopens the sign-in URL from the command's output, in case the browser didn't open or the tab was closed.
  - A code field, shown only if the flow asks for a pasted code. Claudette writes the code to the command's input.
  - **Cancel**, which stops the command.
  - **More options**: sign in with SSO (`--sso`) or with an Anthropic Console account for API billing (`--console`).
- When the command succeeds, Claudette runs `claude auth status` again and shows the account. Any tab that failed is restarted with `--resume`. Messages sent while signed out stay queued and are delivered once sign-in completes.
- If sign-in fails (timed out, cancelled, organization not allowed), Claudette shows the command's message and a **Try again** button.
- **Account menu** (in the header): the signed-in email and plan from `claude auth status`, and **Sign out**, which runs `claude auth logout`. Signing out asks for confirmation first, because every tab will stop working.

> **Spike (milestone 1):** Confirm how `claude auth login` behaves with no terminal attached: whether it opens the browser itself, what it prints (the URL and any paste-a-code prompt), and whether it needs a pseudo-terminal. If it needs a terminal, run it inside a small embedded terminal view.

## 12. Claude Code Updates

How Claude Code updates depends on how it was installed. `claude doctor` is a read-only diagnostic that reports the install method, whether auto-updates are on, the release channel and the last update attempt. For example:

```
Running: native (2.1.284)
Config install method: native
Auto-updates: enabled
Auto-update channel: latest
Last update attempt: success → 2.1.284 (2026-09-28)
```

| Install method | How Claude Code updates | What Claudette offers |
|---|---|---|
| Native installer (the default) | Updates itself in the background. The new version is used the next time a `claude` process starts. | Tell the user an update is installed. **Update now** runs `claude update`. |
| npm | Updates itself, if the npm global folder is writable. | Same as native. If `claude doctor` says it can't update, show the fix it suggests. |
| Homebrew | Doesn't update itself. | **Update now** runs `brew upgrade claude-code`, or `claude-code@latest` if that is the cask installed. |
| WinGet | Doesn't update itself. | **Update now** runs `winget upgrade Anthropic.ClaudeCode`. Windows locks a running executable, so this fails while any tab is running. In that case Claudette offers **Update on next launch**, which runs the upgrade at the next start, before any tab starts its process. |
| apt / dnf / apk | Needs admin rights. | Show the command to run; don't run it. |

### Detecting an update

- On launch and every few hours, Claudette runs `claude --version` and `claude doctor`.
- Each tab knows which version it is running from its `system/init` message. If the installed version is newer, that tab is running an old version.
- For Homebrew and WinGet, Claudette checks with the package manager (`brew outdated`, `winget upgrade`). There is no documented "check only" command for native installs, so Claudette relies on the auto-updater and reads the result from `claude doctor`.
- If updates are turned off (`DISABLE_UPDATES`, or managed settings), Claudette shows the version but doesn't offer to update.

### Applying it

- A small, non-blocking badge appears in the header: *"Claude Code 2.1.290 is ready"*. Clicking it shows the current and new version, a link to the Claude Code changelog, and the actions from the table above.
- **Open tabs are never restarted.** Each tab's `claude` process keeps running the version it started with until the tab is closed. Claudette doesn't restart tabs to apply an update, automatically or otherwise.
  - **New tabs** always start on the newly installed version.
  - **To move an open tab to the new version**, close it and open a new one, or reopen its session from History.
  - **Pinned tabs** pick up the new version the next time Claudette launches, because restored tabs start new processes.
- A tab running an older version than the one installed shows a small note in its tooltip, for example *"Running Claude Code 2.1.284; 2.1.290 is installed. New tabs use 2.1.290."*
- **Update now** shows the command's output in a small progress dialog and reports the new version when it succeeds.
- **Minimum version.** Claudette declares the lowest Claude Code version it supports. If the installed version is older, the setup screen asks you to update before any tab starts, with the same **Update now** action.

## 13. Architecture

```
┌──────────────────────── Claudette.App (Avalonia) ────────────────────────┐
│  Views + ViewModels: MainWindow, UsageHeader, TabStrip, Conversation,    │
│  Composer, DiffView, Processes, History, SignIn, Settings                │
└───────────────┬───────────────────────────────┬──────────────────────────┘
                │                               │
┌───────────────▼──────────────┐  ┌─────────────▼──────────────┐  ┌───────────────────┐
│ Claudette.Core               │  │ Claudette.Usage            │  │ Claudette.Platform│
│  SessionManager              │  │  UsageSampler / store      │  │  Notifications    │
│  ClaudeSession (1 per tab)   │  │  BurnRateCalculator        │  │  Dock/taskbar     │
│   ├ process + stdio          │  │  Projection                │  │  Window chrome    │
│   ├ protocol reader/writer   │  │  TabTokenStats             │  │  External diff    │
│   └ typed event stream       │  └────────────────────────────┘  │  Process monitor  │
│  TranscriptReader (history)  │                                  │  (Job Objects /   │
│  SessionLibrary (sync)       │                                  │   libproc, /proc) │
│  AuthService (sign-in)       │                                  └───────────────────┘
│  InstallService (updates)    │
│  Settings / persistence      │
└───────────────┬──────────────┘
                │ stdin/stdout (JSON lines)
        ┌───────▼───────┐
        │  claude CLI   │  × one per tab
        └───────────────┘
```

- **Claudette.Core** has no UI dependencies, so it can be unit tested and could be reused by another front end.
  - `ClaudeSession` owns one `claude` process. It turns the output stream into typed events (`AssistantDelta`, `ToolUse`, `ToolResult`, `PermissionRequest`, `TurnCompleted`, `TitleChanged`, `UsageUpdated`, `RateLimit`, `AuthRequired`, `Exited`…), and exposes commands such as `SendAsync`, `InterruptAsync`, `RespondToPermissionAsync`, `SetModelAsync`, `SetEffortAsync` and `SetPermissionModeAsync`.
- **Threading.** Each session reads its process on a background task. Events go to the UI thread through a channel, and streaming text is batched so the UI isn't updated for every token.
- **Resilience.** If a process exits unexpectedly, the tab shows an error with a **Restart** button that resumes the same session ID.
- **Shutdown.** When Claudette closes while a tab is working, it interrupts the turn first so the session is left in a clean state.
- **Logging.** Raw protocol traffic can be logged per session (off by default) to help debug parsing problems when Claude Code changes its output.

### Integration with Claude Code

There is no official .NET Agent SDK; the official ones are Python and TypeScript. Claudette drives Claude Code the same way those SDKs do: one long-running `claude` process per tab, in headless streaming mode, exchanging JSON lines over stdin and stdout. It doesn't pass `--bare`, so the user's `CLAUDE.md`, settings, hooks, MCP servers, skills and plugins load the same as in the terminal.

Launch, with the tab's folder as the working directory:

```
claude -p --input-format stream-json --output-format stream-json --verbose
          --include-partial-messages
          --model <model> --effort <level> --permission-mode <mode>
          [--resume <session-id>]
```

It also passes whatever flags the SDKs use to send permission prompts back to the host over stdio; take these from the SDK source.

| Need | How (Agent SDK equivalent) | Documented |
|---|---|---|
| Send a message, with images | A `user` message as one JSON line on stdin. Messages sent while Claude is working are queued. | Yes |
| Receive output | JSON lines on stdout: `system/init`, `assistant`, `user` (tool results), `stream_event` (partial text), `result`, `rate_limit_event`, `auth_status`, `permission_denied`, `api_retry`, `conversation_reset`, task and subagent events | Yes |
| Stop the current turn | `interrupt()` control request. SIGINT as a fallback. Never SIGTERM: it leaves the turn unfinished with no result. | Yes |
| Permission prompts | Claude Code sends a control request; Claudette replies allow, allow with a rule, or deny with a message (`canUseTool`). Prompts still pending after a reconnect are listed in the `initialize` response. | Behavior yes, wire format no |
| Change model | `setModel()`: applied in place, even mid-turn; the conversation is kept | Yes |
| Change effort | `applyFlagSettings({ effortLevel })`: applies from the next turn | Yes |
| Change permission mode | `setPermissionMode()` | Yes |
| Model list and effort levels | `supportedModels()`: `ModelInfo.supportedEffortLevels` | Yes |
| Slash commands for autocomplete | `supportedCommands()`, refreshed by `commands_changed` messages | Yes |
| Context window usage | `getContextUsage()` | Yes |
| Account and plan | `accountInfo()`, or `claude auth status` | Yes |
| Resume | `--resume <session-id>`; the ID is on `system/init` and `result` | Yes |
| Feature detection | The `capabilities` array on `system/init` (for example `interrupt_receipt_v1`). Check this instead of comparing version numbers. | Yes |

**Protocol risk.** The SDK docs describe the behavior and every message type, but not the wire format of control messages (a `control_request` / `control_response` pair matched by `request_id`). To contain that risk:

- Use the open-source Python Agent SDK as the reference for the wire format.
- Keep all protocol code behind one interface (`IClaudeTransport`). Test it against recorded protocol traffic from real sessions.
- Feature-detect with `capabilities`, and enforce a minimum Claude Code version ([§12](#12-claude-code-updates)).
- **Fallback:** if the wire protocol changes too often, swap in a small Node sidecar that runs the official TypeScript Agent SDK and relays to Claudette over a local pipe. This means shipping Node.

**Session naming.**

- Claude Code names a session with an AI-generated title from the first prompt, or with a name set by `--name` or `/rename`.
- The title isn't pushed on the stream-json output. It's saved in the session transcript (the SDK's session list returns it as `summary` / `customTitle`) and passed to hooks as `session_title`. Claudette reads it after each turn until the tab has a title.
- A rename in Claudette is stored by Claudette. Optionally (a setting), Claudette also sends it to Claude Code with `/rename`, so `claude --resume <name>` in a terminal sees the same name.
- A `conversation_reset` message (from `/clear`) clears the view and drops the cached title.

**Transcripts.** Sessions are stored in `~/.claude/projects/<project>/<session-id>.jsonl`. The format is internal and changes between versions, so `TranscriptReader` is kept separate, ignores entries it doesn't recognize, and is tested against real files from several Claude Code versions.

> **Spikes (milestone 1):** Confirm the control message wire format (interrupt, permission prompts, set model, apply flag settings) against the Python SDK. Find the cheapest reliable source of the session title. Also cover the usage data spike in [§6](#data-source) and the sign-in spike in [§11](#signing-in).

## 14. Settings

A **Settings** window opens with `Ctrl+,` on Windows or `Cmd+,` on macOS, where it is also **Settings…** in the app menu. It follows each platform's conventions:

- A sidebar lists the categories.
- Changes apply immediately; there is no Save button.
- Each category has **Reset to defaults**.
- A search box filters settings by name.

### Categories

| Category | Settings |
|---|---|
| General | Confirm before closing a working tab. Also rename the session in Claude Code when a tab is renamed. |
| Sessions | Also restore unpinned tabs on launch (off by default; pinned tabs are always restored). Session library folder (with **Browse…** and **Move library…**, which copies existing sessions to the new folder). Name for this machine, as shown in History. How long to keep sessions in the library. Sync Claudette's settings through the library (off by default). See [§9](#session-library-sync-across-machines) and [Settings sync](#settings-sync-optional). |
| Processes | Show the process monitor. Refresh interval. Show command lines. See [§4](#process-monitor). |
| Claude Code | Path to `claude` (auto-detected, with **Browse…**). Installed version and install method, from `claude doctor`. Signed-in account, with **Sign in** / **Sign out**. Check for Claude Code updates automatically. |
| New tabs | Default model, effort level and permission mode. Number of recent folders to keep (default 20), and **Clear recent folders**. Favorite folders (add, remove, reorder). See [Opening a tab](#opening-a-tab). |
| Appearance | Theme: follow system, light or dark. Font and size for the conversation, and for code. Show thinking expanded or collapsed by default. |
| Usage | Warning thresholds (default 75% and 90%). Burn rate window (default 30 minutes). Show model-specific weekly meters. Keep usage history: 1 day, 1 week, 1 month (default), 1 year or forever, with a **Clear usage history** button beside it. See [Usage history](#usage-history). |
| Quick suffixes | The list of suffixes: label, text and optional shortcut. Add, edit, reorder, delete. See [§5](#quick-suffixes). |
| Check-ins | On/off. Run time before checking in. Quiet time before checking in. Check-in message text. Notify me when a check-in is sent. See [§5](#check-ins-on-long-turns). |
| Diff tool | Built-in, a preset or a custom command, with **Test**. See [§8](#external-diff-tool). |
| Notifications | On/off for each type in [§10](#10-notifications). Dock/taskbar badge on/off. |
| Keyboard | List of shortcuts, each one rebindable. |
| Advanced | Protocol logging and **Open log folder**. Extra command-line arguments passed to `claude`. Minimum supported Claude Code version (read-only). |

### Per-tab overrides

Some settings can be changed for a single tab from the tab's right-click menu, under **Tab settings…**: model, effort level, permission mode, and the check-in settings. A tab with overrides shows a small dot next to its settings entry, and **Use defaults** clears them. Overrides are saved with the tab.

### Storage

- Claudette's settings are stored as JSON in the app data folder: `%APPDATA%\Claudette\settings.json` on Windows, `~/Library/Application Support/Claudette/settings.json` on macOS, and `~/.config/claudette/settings.json` on Linux.
- Settings files have a version number so later releases can migrate them.
- Claudette's settings are separate from Claude Code's. Claudette doesn't edit `~/.claude/settings.json` or a project's `.claude/` settings except where this document says it does.

### Settings sync (optional)

**Sync settings through the session library** (Settings → Sessions, off by default) keeps Claudette's settings the same on every machine that uses the same library folder ([§9](#session-library-sync-across-machines)).

- **What syncs:** appearance, new-tab defaults, usage thresholds, check-ins, quick suffixes, notifications, keyboard shortcuts and process monitor options.
- **What stays on each machine:** the path to `claude`, this machine's name, the library folder itself, the diff tool (program paths differ between machines), recent and favorite folders, folder mappings, pinned tabs, and window sizes and positions.
- The synced settings are stored as one file in the library. Each setting keeps the time it was last changed, and the newest change wins, so edits on two machines don't overwrite each other wholesale.
- The first time sync is turned on and the library already has settings from another machine, Claudette asks: **Use synced settings** or **Replace them with this machine's**.
- Turning sync off keeps the current values on this machine and stops syncing.

## 15. Milestones

1. **Skeleton.** Avalonia app, Claude Code detection and version check, sign-in, one tab: launch `claude`, send a prompt, stream the reply as Markdown, Stop. Includes the spikes in [§6](#data-source), [§11](#signing-in) and [§13](#integration-with-claude-code).
2. **Tool rendering.** Tool call cards, Edit diffs, Bash output, thinking, subagents, to-do list.
3. **Tabs & settings.** Multiple sessions, folder per tab, automatic and user naming, status icons, model and effort indicators and pickers, per-tab token stats, pinned tabs and restore on launch, Settings window and per-tab overrides, check-ins, quick suffixes.
4. **Permissions.** Inline prompts, permission mode picker.
5. **Usage.** Header meters, local sample store, burn trendline and projection, Usage panel, alerts.
6. **History, sync & diffs.** Session history and resume, session library and cross-machine restore, changed files panel, built-in diff view, external diff tools, process monitor.
7. **Polish & ship.** Notifications, Claude Code update handling, keyboard shortcuts, platform chrome, packaging and signing for Windows and macOS.

## 16. Open Questions

None right now.