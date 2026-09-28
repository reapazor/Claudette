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
  - By default a tab uses the AI-generated title Claude Code gives the session, the same kind of name it shows as the terminal tab title. In headless mode Claudette has to ask for it after the first prompt (see [session naming](#integration-with-claude-code)).
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
  - Claude only sees the message between steps. The spike confirmed that a message sent during a tool call arrives with that tool's result, inside the same turn. So if a single command is running (for example a long test run), the check-in is delivered when that command finishes.
  - If Claude is in the middle of a long text-only reply, the check-in runs as the next turn instead.
  - After two check-ins in a row get no reply, Claudette stops sending them for that turn. It marks the tab as possibly stuck and offers **Stop**.
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

Plan limits come from three sources, tried in order. All three were confirmed by the milestone 1 spike (2026-09-28, Claude Code 2.1.284), and none of them uses tokens.

| # | Source | What it gives | Notes |
|---|---|---|---|
| 1 | The `get_usage` control request | A `rate_limits.limits` list with one entry per limit: `kind` (`session`, `weekly_all`, or `weekly_scoped` with `scope.model.display_name` such as "Fable"), plus `percent`, `severity` (`normal` … `critical`), `resets_at` and `is_active` | Everything the header needs, including model-specific weekly limits. **Undocumented:** the TypeScript SDK exposes it as `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET`. |
| 2 | `rate_limit_event` messages, sent after model calls | `rateLimitType`, `status`, and `unifiedWindows.five_hour` / `seven_day`, each with `utilization` (0–1) and `resetsAt` (epoch seconds) | Session and weekly only, with no model-specific limits. The documented type only lists `status`, `utilization` and `resetsAt`; the window fields are undocumented. |
| 3 | `/usage` sent as a prompt | Text such as `Current week (Fable): 100% used · resets Sep 30, 6:59am` | A local command, so it's free and makes no model call. Parsing the text is brittle; last resort only. |

The status line's `rate_limits` data can't be used: the spike confirmed that status line commands don't run in headless mode.

Token and context data are documented:

| Data | Source |
|---|---|
| Tokens per turn and per tab | `usage` and `modelUsage` on each `result` message; per-call `usage` on `assistant` messages (de-duplicated by message ID). `modelUsage` also includes `costUSD` and `contextWindow`. |
| Context window % | The `get_context_usage` control request, or last-turn input tokens ÷ `contextWindow` |

**Sampling.**

- All tabs share one account, so plan usage is tracked app-wide, not per tab.
- Claudette calls `get_usage` on launch, after each turn (at most once a minute), and every 5 minutes otherwise. It uses a hidden **utility session** for this ([§13](#integration-with-claude-code)), so the header stays current even when no tab is working.
- `rate_limit_event` messages from any tab update the header immediately between polls.
- If `get_usage` fails or changes shape, Claudette falls back to `rate_limit_event`, and hides the model-specific meters unless the `/usage` fallback is turned on in Settings.

**Extra data.** `get_usage` also reports what's contributing to usage, such as the share of requests at long context and the top skills and subagents over the last day and week. That could become a panel later; it's not in v1.

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

**How it works** (confirmed by the spike):

- **The request.** Claude Code sends a `can_use_tool` control request with `tool_name`, `input`, `description`, `tool_use_id` and `permission_suggestions`.
- **The suggested rule.** The suggestions include ready-made rules, for example `addRules` with `Bash(touch c.txt)` for `localSettings`, or `setMode: acceptEdits`. The first `addRules` suggestion is the default rule shown on the **Always allow** card.
- **Allow:** `{ "behavior": "allow", "updatedInput": <input> }`.
- **Always allow** adds `updatedPermissions: [{ "type": "addRules", "rules": [{ "toolName": "Bash", "ruleContent": "touch:*" }], "behavior": "allow", "destination": "localSettings" }]`. Claude Code then writes `Bash(touch:*)` to `.claude/settings.local.json` itself, and later matching commands don't prompt.
- **Deny:** `{ "behavior": "deny", "message": "<text>" }`.
- **Read-only commands** such as `sleep` never prompt at all.

## 8. File Changes / Diff View

- A collapsible side panel lists the files changed in the selected tab's session: added, modified or deleted, with `+/−` line counts. It is built from the session's Edit/Write tool calls.
- Selecting a file opens a diff view (side-by-side or inline) with syntax highlighting.
- Actions: open in external diff tool, open in external editor, reveal in Finder/Explorer, copy path.
- If the folder is a git repo, a toggle switches to **working tree vs HEAD**. This also shows changes made by Bash commands or by the user.
- **Before content.** Claude Code reports it. The result of every Edit and Write tool call (the `tool_use_result` field on the `user` message that carries the tool result) includes:
  - `originalFile`: the file's full content before the change, or `null` for a new file.
  - `structuredPatch`: the change as diff hunks.
- The "before" side of a file's diff is the `originalFile` from Claude's first change to that file in the session, so diffs are exact even outside a git repo. The spike confirmed this in Accept edits mode too. Claudette never has to snapshot files itself, so there's no race with the tool writing the file.

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
3. **Resume.**
   - Claudette copies the library's `<session-id>.jsonl` to a local working folder (`<app data>/sessions/`) and resumes with `claude --resume <local path>`.
   - After each turn, it copies the file back to the library.

**How resuming from a file behaves** (confirmed by the spike):

- `--resume <path>` loads the full history and keeps the same session ID.
- Claude Code then writes the continued transcript, complete and not just the new turns, to `<session-id>.jsonl` in the **same folder** as the file it was given.
- Because the local working copy already has that name, Claude Code keeps writing to it. Resuming straight from the library folder would make Claude Code write live into the synced folder in the middle of a turn, which the library is designed to avoid.

> **Not yet tested:** resuming on macOS a transcript recorded on Windows, and the reverse. Transcripts store absolute paths (`cwd`, file paths in tool calls), so check this once a Mac is available.

**One machine at a time.**

- While a session is open, Claudette keeps a small lease file next to it ("in use on DESKTOP-01", refreshed every minute).
- Opening a session that another machine is actively using asks the user to either:
  - **Open a copy**, which forks it into a new session with `--fork-session`, or
  - **Take over**, after which the other machine's tab becomes read-only on its next sync.
- A lease that hasn't been refreshed in 10 minutes counts as stale.
- If the sync client creates conflict copies (for example `session (1).jsonl`), Claudette shows them in History as separate, forked entries. It never merges them.

**Privacy.** Transcripts contain code, command output and anything else Claude read in the project. When the user picks a library folder inside a known cloud-sync location, Claudette says so and asks them to confirm.

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

- **On launch**, before any tab starts, Claudette runs `claude auth status`. It prints JSON and exits with 0 when signed in and 1 when not. The spike confirmed these fields:
  - `loggedIn` and `authMethod` (`claude.ai`, `none`, …).
  - `email`, `orgName` and `subscriptionType`.
  - `configDirectory`, and `projectsDirectory`, which is where Claude Code keeps its transcripts. History uses it.
- **While running**, any of these from a session means "needs sign-in":
  - An `assistant` message with `error: "authentication_failed"`.
  - An `auth_status` message.
  - A session that fails to start with an authentication error.

### Signing in

- At launch, Claudette shows a sign-in screen instead of the tabs. In the middle of a session, it shows a banner across all tabs: *"Claude Code needs you to sign in."* If Claudette isn't focused, it also sends an OS notification.
- **Sign in** (main flow) uses the utility session's control protocol, as the Agent SDK does:
  1. Claudette sends `claude_authenticate` with `loginWithClaudeAi: true`. Claude Code replies with two URLs and doesn't open a browser itself:
     - `automaticUrl` redirects back to a local port Claude Code is listening on, so sign-in finishes without copying anything.
     - `manualUrl` redirects to a page that shows a code to copy.
  2. Claudette opens `automaticUrl` in the default browser and sends `claude_oauth_wait_for_completion`, which returns when sign-in finishes.
  3. If that doesn't work (for example a browser on another device, or a firewall blocking the local port), the user can switch to **Enter a code instead**. Claudette opens `manualUrl`, shows a code field, and sends the code with `claude_oauth_callback`.
- These control requests are **undocumented** (they exist in the TypeScript SDK but not its docs). The **fallback** is the documented `claude auth login`. The spike ran it with no terminal attached: it opened the browser itself and printed a fallback URL, but used the copy-a-code flow. With this fallback, the code field writes the code to the command's input (still to be confirmed when building it).
- While it waits, Claudette shows *"Finish signing in in your browser"* and stays responsive. The screen has:
  - **Open browser again**, which reopens the same URL, in case the browser didn't open or the tab was closed.
  - **Enter a code instead**, as described above.
  - **Cancel**.
  - **More options**: sign in with an Anthropic Console account for API billing (`loginWithClaudeAi: false`, or `claude auth login --console`), or with SSO (`claude auth login --sso`).
- When sign-in succeeds, Claudette runs `claude auth status` again and shows the account. Any tab that failed is restarted with `--resume`. Messages sent while signed out stay queued and are delivered once sign-in completes.
- If sign-in fails (timed out, cancelled, organization not allowed), Claudette shows the command's message and a **Try again** button.
- **Account menu** (in the header): the signed-in email and plan from `claude auth status`, and **Sign out**, which runs `claude auth logout`. Signing out asks for confirmation first, because every tab will stop working.

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

Everything in this section was confirmed by the milestone 1 spike on 2026-09-28 against Claude Code 2.1.284, unless marked otherwise.

**Launch**, with the tab's folder as the working directory:

```
claude -p --input-format stream-json --output-format stream-json --verbose
          --include-partial-messages --permission-prompt-tool stdio
          --model <model> --effort <level> --permission-mode <mode>
          [--resume <session-id or transcript path>]
```

- `--permission-prompt-tool stdio` sends permission prompts to Claudette as control requests. The TypeScript SDK passes this flag when a `canUseTool` callback is set.
- **Clean environment.** Claudette removes inherited `CLAUDECODE`, `CLAUDE_CODE_*` and `CLAUDE_AGENT_SDK_*` variables before launching. If Claudette was started from a terminal inside Claude Code, those variables make `claude` behave as a child session. In the spike this ignored the API key and reported "Not logged in".

**Startup.** The first thing Claudette sends is an `initialize` control request. The reply contains:

- `models`: each with `value`, `resolvedModel`, `displayName`, `description`, `supportsEffort` and `supportedEffortLevels`.
- `commands`: slash commands, for autocomplete.
- `account`: `email`, `organization`, `subscriptionType` and `tokenSource`.
- Also `current_permission_mode`, `agents`, output styles and `pid`.

The `system/init` message that follows gives `session_id`, `model`, `permissionMode`, `claude_code_version` and `capabilities`. In 2.1.284 the capabilities were `interrupt_receipt_v1`, `interrupt_cancel_queued_v1`, `msg_lifecycle_v1`, `mcp_read_resource_v1` and `mcp_tool_ui_meta_v1`.

**Wire format.** Every control message is one JSON line:

```
→ {"type":"control_request","request_id":"req_1","request":{"subtype":"interrupt"}}
← {"type":"control_response","response":{"subtype":"success","request_id":"req_1","response":{"still_queued":[]}}}
```

- Errors come back as `"subtype":"error"` with an `error` string.
- Claude Code sends its own control requests the same way (for example `can_use_tool`), and Claudette answers with a `control_response` carrying the same `request_id`.
- The wire names come from the Agent SDK sources. The SDK docs describe the matching methods but not the wire format.

| Need | How | Documented |
|---|---|---|
| Send a message, with images | A `user` message as one JSON line on stdin. See "Messages sent while Claude is working" below. | Yes |
| Receive output | JSON lines on stdout: `system/init`, `system/status`, `assistant`, `user` (tool results, with `tool_use_result`), `stream_event` (partial text), `result`, `rate_limit_event`, `auth_status`, `permission_denied`, `api_retry`, `conversation_reset`, `task_started` / `task_notification`, `thinking_tokens` | Yes |
| Stop the current turn | `interrupt`. The reply lists `still_queued` messages; the turn ends with a `result` of `error_during_execution` / `aborted_streaming`. SIGINT is a fallback. Never SIGTERM: it leaves the turn unfinished with no result. | Yes |
| Permission prompts | Incoming `can_use_tool`; reply allow, allow with `updatedPermissions`, or deny with a message ([§7](#7-permission-prompts)) | Behavior yes, wire format no |
| Change model | `set_model` with `model`. Applied in place, even mid-turn, and the conversation is kept. Claude Code also emits a `user` message containing `<local-command-stdout>Set model to …</local-command-stdout>`, which Claudette shows as a small system note. | Yes |
| Change effort | `apply_flag_settings` with `settings: { effortLevel }`. Applies from the next request, which carries `output_config.effort`. | Yes |
| Change permission mode | `set_permission_mode` with `mode`; also reported as a `system/status` message | Yes |
| Context window usage | `get_context_usage`. Claude Code calls the API's token-counting endpoint for this, which costs nothing. | Yes |
| Stop a background task | `stop_task` with `task_id` | Yes |
| Plan usage limits | `get_usage` ([§6](#data-source)) | **No** (marked experimental) |
| Sign-in | `claude_authenticate`, `claude_oauth_wait_for_completion`, `claude_oauth_callback` ([§11](#signing-in)) | **No** |
| Session title | `generate_session_title`, `rename_session` (below) | **No** |
| Resume | `--resume <session-id>`, or `--resume <path to a .jsonl>` ([§9](#session-library-sync-across-machines)) | Yes |
| Feature detection | The `capabilities` array on `system/init`. Check this instead of comparing version numbers. | Yes |

Every **No** row has a fallback, listed in its section, and is marked `undocumented` in `compat/surface.yaml` ([§16](#16-tracking-claude-code-changes)).

**Messages sent while Claude is working.**

- **During a tool call:** the message is delivered inside the running turn, together with the next tool result, as a note that says *"The user sent a new message while you were working"*. This is what check-ins rely on ([§5](#check-ins-on-long-turns)).
- **During a text-only reply:** there's no tool boundary, so the message waits and runs as the next turn.

**Tool results.** The `user` message that carries a tool result also has a `tool_use_result` field with structured details:

- Edit and Write: `originalFile`, `structuredPatch`, `oldString` / `newString`.
- Bash: `stdout`, `stderr`, `interrupted`.

The conversation view and diff view use these instead of parsing the tool result text.

**Session naming.**

- Headless sessions don't get an AI-generated title on their own.
- After a tab's first prompt, Claudette sends `generate_session_title` with `description` set to the prompt and `persist: true`. The reply's `title` becomes the tab name. This is one small model call per new session.
- A rename in Claudette is stored by Claudette. Optionally (a setting), Claudette also sends `rename_session` with `title` and `source: "host"`, so `claude --resume <name>` in a terminal sees the same name.
- Both are saved in the transcript, as `{"type":"ai-title","aiTitle":…}` and `{"type":"custom-title","customTitle":…}` entries. History reads them from there, and the last entry of each type wins.
- **Fallback** if these requests stop working: name the tab from the first line of the first prompt.
- A `conversation_reset` message (from `/clear`) clears the view and drops the cached title.

**Utility session.** Claudette keeps one hidden `claude` process with `--no-session-persistence` that never sends a prompt. It serves the requests that don't belong to a tab: `initialize` (model list and account), `get_usage`, and sign-in. It's started on launch and restarted if it exits.

**Transcripts.** Sessions are stored as `<session-id>.jsonl` in the `projectsDirectory` reported by `claude auth status` (normally `~/.claude/projects/<project>/`). The format is internal and changes between versions, so `TranscriptReader` is kept separate, ignores entries it doesn't recognize, and is tested against real files from several Claude Code versions.

**Protocol risk.** To contain it:

- Keep all protocol code behind one interface (`IClaudeTransport`), and test it against recorded protocol traffic.
- Feature-detect with `capabilities`, and enforce a minimum Claude Code version ([§12](#12-claude-code-updates)).
- Watch for changes daily ([§16](#16-tracking-claude-code-changes)).
- **Fallback:** if the wire protocol changes too often, swap in a small Node sidecar that runs the official TypeScript Agent SDK and relays to Claudette over a local pipe. This means shipping Node.

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
| Advanced | Protocol logging and **Open log folder**. **Diagnostics** page ([§16](#staying-tolerant-at-runtime)). Extra command-line arguments passed to `claude`. Minimum supported Claude Code version (read-only). |

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

## 15. Testing

The whole test suite runs without an Anthropic account and without using any tokens. Only a small, opt-in live suite talks to the real service.

### Design rules that make this possible

These apply from milestone 1:

- `ClaudeSession` talks to Claude Code only through `IClaudeTransport`, never directly to a process, so tests can swap in a fake.
- Processes are started through `IProcessLauncher`, so tests can check the exact command line and environment, and fake the process.
- All time-based code (burn rate, check-ins, leases, usage retention, update checks, sampling) uses .NET's `TimeProvider`. Tests move the clock forward with `FakeTimeProvider` instead of waiting.
- File locations (app data, the session library, Claude Code's config folder) are injected, so tests use temporary folders.

### Test layers

| Layer | What it covers | How | Tokens |
|---|---|---|---|
| Unit | Pure logic: burn rate and projection, check-in timers, usage retention, settings sync merging, suffix composition, diff command templates, recent folders, tab grouping, project identity matching, lease files | Plain unit tests with a fake clock and temporary folders | None |
| Protocol replay | Turning Claude Code's output into events, and what Claudette writes back: messages, interrupts, permission replies, model and effort changes | Recorded stream-json traffic from real sessions, checked in as fixture files and replayed through a fake transport | None |
| Fake CLI | Process handling: launch flags, stdin/stdout, interrupts, crashes, hangs, sign-in failures, `--version` and `doctor` output, child processes for the process monitor | A small `fake-claude` test program that speaks the stream-json protocol and follows a scenario file. Claudette points at it through the "path to `claude`" setting. | None |
| Real CLI, fake model | End to end against the real `claude` binary: real tools, permission prompts, file edits, transcripts and resume | A local mock server that implements the Anthropic Messages API and returns scripted replies. `claude` points at it with `ANTHROPIC_BASE_URL` and a dummy `ANTHROPIC_API_KEY`. | None |
| UI | View models, and views: tab strip, composer, chips, permission cards, meters | View-model tests with no UI; Avalonia.Headless for rendering and input; snapshot tests with Verify | None |
| Live (opt-in) | What only the real service can confirm: `rate_limits` data, sign-in, real model output | Tests tagged `Live`, excluded by default and run manually before a release | A few cents |

### Protocol fixtures

- Recorded from real sessions by a **record** mode: the Live suite, or a developer session with protocol logging turned on.
- Before they're checked in, they're cleaned of paths, emails, account details and session IDs.
- Stored under `tests/fixtures/protocol/<claude-code-version>/`.
- Scenarios covered:
  - A simple reply, streaming text and thinking.
  - Tool calls and subagents.
  - A permission prompt that is allowed, and one that is denied.
  - An interrupt.
  - `rate_limit_event` and an authentication failure.
  - `/clear` (conversation reset), compaction and API errors.
- When a new Claude Code version comes out, recording the same scenarios again and diffing them against the old fixtures shows protocol changes before users hit them.

### Fake CLI (`fake-claude`)

- Each scenario file lists what the fake prints and what it expects to receive. It also controls timing (delays, and long silences to trigger check-ins), exit codes, crashes, and child processes to spawn.
- It also answers the other commands Claudette runs: `--version`, `auth status`, `auth login` (with a fake browser step), `doctor` and `update`. That covers sign-in and update handling without the real CLI.

### Real CLI against a fake model

- The mock server implements the API format Claude Code expects from an LLM gateway, including streaming.
- Each test scripts the model's side. For example: "reply with a `tool_use` for Edit on `a.txt`, then a short summary."
- Claude Code then runs the real tool against a temporary git repo, so permission prompts, diffs, "before" snapshots, transcripts and `--resume` are all tested for real.
- Every test runs `claude` with:
  - `CLAUDE_CONFIG_DIR` set to a temporary folder, so it never touches the user's `~/.claude` settings, sessions or credentials.
  - `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`, so there's no auto-update or telemetry.
- API-key mode doesn't produce subscription `rate_limits` data. The usage meters are covered by protocol fixtures instead.
- These tests need Claude Code installed, so they're tagged `RealCli`. CI installs Claude Code before running them.

**Confirmed by the spike** (Claude Code 2.1.284): the real `claude` runs complete sessions against a local mock server with a dummy key. It calls three endpoints:

- `HEAD /api/hello`, a connectivity check.
- `POST /v1/messages?beta=true`, streaming.
- `POST /v1/messages/count_tokens?beta=true`, for context usage.

Tool calls, permission prompts, file edits, interrupts, model and effort changes, titles and resume all worked with no tokens used. Two things to note:

- The test runner must launch `claude` with a clean environment, the same as the app ([§13](#integration-with-claude-code)).
- Each request carries the settings in effect, such as `model` and `output_config.effort`, so tests can check that those changes reached the API.

The spike's Node scripts (a mock Messages API, a stream-json driver and the scenarios that were run) are kept in [`spikes/`](spikes/README.md). They're a working reference for the protocol until the .NET harness replaces them.

### Live suite

- A handful of tests using the cheapest settings: Haiku, low effort, one-line prompts.
- Runs with a separate API key that has a spend limit, not someone's personal subscription.
- Also used to record new protocol fixtures.

### Tools and CI

- xUnit v3, Avalonia.Headless.XUnit, Verify (snapshot testing) and Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`).
- GitHub Actions runs everything except `Live` on Windows, macOS and Linux for every push and pull request.

## 16. Tracking Claude Code Changes

Claude Code ships new versions often. Claudette depends on its command-line flags, output messages, control protocol, settings, file locations and command output, and some of these aren't documented. This section is the plan for catching changes before users do.

### The compatibility surface list

`compat/surface.yaml` in the repo lists every Claude Code item Claudette relies on, one entry each. Examples: `--input-format stream-json`, `rate_limit_event.rate_limit_info.utilization`, the exit codes of `claude auth status`, the `~/.claude/projects/` layout, the "Config install method" line of `claude doctor`.

Each entry records:

- The exact identifier, used to match against diffs.
- Where Claudette uses it (project and class).
- The doc page that describes it, or **undocumented**.
- The first Claude Code version that has it.
- The test that covers it.
- For undocumented items, the fallback if it breaks.

**Rule:** any code that starts using a new Claude Code item adds an entry in the same pull request. Code review checks this.

### What's watched

| Source | What it tells us | Where |
|---|---|---|
| New releases | A new version exists | npm release tags for `@anthropic-ai/claude-code` (`latest` and `stable`) |
| Changelog | What changed, in words | `CHANGELOG.md` in the `anthropics/claude-code` GitHub repo |
| Agent SDK types | The exact shape of every message, option and control method | `sdk.d.ts` in the `@anthropic-ai/claude-agent-sdk` npm package, compared between versions |
| Python Agent SDK source | The wire format of control messages, which isn't documented | The `anthropics/claude-agent-sdk-python` GitHub repo |
| Docs pages | Documented behavior | The Markdown version of each page Claudette depends on (add `.md` to the URL); `llms.txt` lists every page |
| The CLI itself | Flags, and actual output | `claude --help` and subcommand help, plus protocol output recorded against the mock model server ([§15](#real-cli-against-a-fake-model)) |

### Daily compatibility check

A scheduled GitHub Action runs once a day. When a new Claude Code version appears on either npm release tag, it:

1. **Snapshots** the new version's changelog entry, SDK type definitions, the docs pages listed in the surface file, and `claude --help` output into `compat/snapshots/<version>/`.
2. **Diffs** each one against the previous version's snapshot.
3. **Matches** the diffs and changelog lines against the identifiers in `compat/surface.yaml`, so changes to things Claudette uses are listed first.
4. **Tests** by installing that version and running the free test suite against it, including the real-CLI tests with the mock model. It also records the protocol output for the fixture scenarios again (this is free with the mock model) and diffs it against the previous version's.
5. **Reports** by opening a GitHub issue, *"Claude Code 2.1.285 compatibility report"*, labeled `compat`. The issue shows test results first, then matched changes, then the full diffs in collapsed sections. If nothing matched and every test passed, the issue is closed automatically and kept as a record.

For example, the 2.1.284 changelog says the status line's `rate_limits.spend_limit` gained `used_usd`, `limit_usd` and `period`. `rate_limits` is in the surface list, so that line would be flagged.

### Tested versions

- Claudette records two versions:
  - `MinimumClaudeCodeVersion`: the hard floor from [§12](#applying-it).
  - `LastTestedClaudeCodeVersion`: updated each time a compatibility report is handled.
- A version newer than the last tested one is allowed. Settings → Claude Code shows a quiet note, *"Newer than the last tested version (2.1.284)"*, and nothing more intrusive.

### Staying tolerant at runtime

Claudette has to keep working when Claude Code adds things it doesn't know about yet:

- Unknown fields are ignored. Unknown message types are skipped and counted. An unknown enum value (for example a new error category) is handled like `unknown`.
- A line that fails to parse never ends a session. It's logged, and Claudette moves on.
- With protocol logging on, a skipped message appears in the conversation as a collapsed *"Unsupported message from Claude Code"* row that shows the raw JSON.
- Features are detected with the `capabilities` list from `system/init`, not by comparing version numbers.
- Settings → Advanced has a **Diagnostics** page. It shows the Claude Code version and counts of unknown messages and fields seen, and has **Copy diagnostics** for bug reports.

### Handling a report

1. Read the matched changes and any failing tests. To see exactly what changed in the protocol, re-run the relevant scenario in [`spikes/`](spikes/README.md).
2. Update the code, `compat/surface.yaml` and the protocol fixtures.
3. Bump `LastTestedClaudeCodeVersion`.
4. If the change breaks a released Claudette version, ship a patch release. Raise `MinimumClaudeCodeVersion` only if older Claude Code versions can no longer be supported.

## 17. Milestones

1. **Skeleton.** Avalonia app, Claude Code detection and version check, sign-in, one tab: launch `claude`, send a prompt, stream the reply as Markdown, Stop. Test harness: fake transport, `fake-claude`, mock model server, first protocol fixtures and CI. Start `compat/surface.yaml` and the daily compatibility check. Also includes the utility session, and turning the spike scripts in [`spikes/`](spikes/README.md) into the .NET test harness. The spikes themselves were done on 2026-09-28; their findings are recorded in the sections they affected.
2. **Tool rendering.** Tool call cards, Edit diffs, Bash output, thinking, subagents, to-do list.
3. **Tabs & settings.** Multiple sessions, folder per tab, automatic and user naming, status icons, model and effort indicators and pickers, per-tab token stats, pinned tabs and restore on launch, Settings window and per-tab overrides, check-ins, quick suffixes.
4. **Permissions.** Inline prompts, permission mode picker.
5. **Usage.** Header meters, local sample store, burn trendline and projection, Usage panel, alerts.
6. **History, sync & diffs.** Session history and resume, session library and cross-machine restore, changed files panel, built-in diff view, external diff tools, process monitor.
7. **Polish & ship.** Notifications, Claude Code update handling, keyboard shortcuts, platform chrome, packaging and signing for Windows and macOS.
8. **Later.** The features in [§18](#18-future-features), in an order decided after v1 ships.

## 18. Future Features

Planned for after v1. Each needs a fuller design before it's built.

### Perforce ticket handling

**The problem.**

- In a Perforce workspace, Claude runs `p4` commands through Bash.
- Perforce login tickets expire (often after 12 hours). After that, every `p4` command fails with *"Your session has expired, please login again."*
- Claude can't log in by itself, because `p4 login` asks for a password, so a long-running tab gets stuck.

**The goal.** Claudette keeps each Perforce tab logged in, so Claude can query and use Perforce without stopping. Claude never sees the password.

**Detecting a Perforce workspace.**

- When a tab opens, Claudette runs `p4 -ztag info` in the tab's folder. Perforce resolves the server, user and workspace from its usual sources (`P4CONFIG` files, `p4 set`, `P4ENVIRO`, environment variables), the same way Claude's own `p4` commands will.
- If it is a Perforce workspace:
  - The tab's tooltip shows the server, user and ticket status, for example *"Perforce: matt @ ssl:perforce:1666, ticket expires in 11h"*.
  - Claudette adds a short note to the session with `--append-system-prompt`: this folder is a Perforce workspace, with its server, user and workspace name, and Claudette keeps the login fresh. That way Claude knows to use `p4` rather than assuming git.

**Keeping the ticket fresh.**

- **Ahead of time.** `p4 login -s` reports whether the ticket is valid and when it expires. Claudette checks when a tab starts, before each turn in a Perforce tab, and every 15 minutes. When the ticket has expired, or less than 30 minutes are left (configurable), Claudette logs in again.
- **Just in time.** Claudette registers a `PreToolUse` hook for Bash commands that start with `p4`. It does this through the hooks field of the `initialize` request, the same way the Agent SDK registers hook callbacks, and Claude Code calls back with a `hook_callback` control request. Before the command runs, Claudette makes sure the ticket is valid. The hook never changes the command; it only lets it continue.
- **Recovery.** If a `p4` command still fails with an expired-session or "P4PASSWD invalid or unset" error (visible in the Bash `tool_use_result`), Claudette logs in again. It then sends a mid-turn message: *"Perforce login renewed. Retry the last p4 command."*

**Logging in.**

- Claudette runs `p4 -p <server> -u <user> login` and writes the password to the command's standard input. The password never goes on the command line, where other processes and the process monitor could see it.
- Optional setting: request a ticket valid on all hosts (`login -a`).
- Perforce writes the ticket to its own tickets file (`P4TICKETS`) as usual; Claudette doesn't touch that file.

**Where the password comes from** (Settings → Perforce → Password source):

1. **Stored by Claudette** (recommended): saved in the OS credential store (Windows Credential Manager, macOS Keychain, Secret Service on Linux) under the server and user. It's never written to `settings.json`, never synced ([§14](#settings-sync-optional)), and never logged.
2. **Perforce's own configuration**: `P4PASSWD` from the `P4CONFIG` file, `p4 set` or the environment. Claudette only reads it. Settings notes that these sources store the password in plain text.
3. **Ask each time**: when a login is needed, Claudette sends a notification and shows a password prompt, and stores nothing. The `p4` command waits in the hook until the user answers or the hook times out.

**Settings → Perforce** (off by default): turn ticket handling on or off, password source, renew-before-expiry time, all-hosts tickets, and per-folder overrides for server and user.

**Not covered.** Servers that sign in through SSO (`P4LOGINSSO`) or multi-factor authentication. For those, Claudette sends a notification asking the user to log in themselves (in a terminal or P4V), then checks again.

## 19. Open Questions

None right now.