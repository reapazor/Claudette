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
| UI | Avalonia 12 | One codebase for Windows, macOS and Linux. |
| Look & feel | Fluent theme on Windows, macOS-style theme on macOS | Follows the OS light/dark setting and accent color. Mica backdrop on Windows 11; native title bar, traffic lights and menu bar on macOS. Mica is used only once Windows grants it: the title bar, header and sidebar show it, and the page keeps an opaque background. The macOS-style theme isn't built yet; macOS uses the Fluent theme for now. |
| Pattern | MVVM with CommunityToolkit.Mvvm | |
| Markdown | LiveMarkdown.Avalonia | For assistant messages. Built for streaming: text is appended as it arrives instead of re-rendering the whole message. Includes syntax-highlighted code blocks. (Markdown.Avalonia only had an alpha for Avalonia 12.) |
| Diffs | Claudette's own line diff and diff view, highlighted with TextMateSharp | The TextMate grammars and themes LiveMarkdown already ships for code blocks. AvaloniaEdit was the plan, but a read-only diff doesn't need an editor. |
| Usage history | SQLite (Microsoft.Data.Sqlite) | [§6](#usage-history) |
| Dependency | Claude Code CLI | Must already be installed. Claudette finds `claude` on `PATH` (or a path set in Settings), checks its version on launch against a minimum supported version, and shows a setup screen if it is missing or too old. Sign-in is handled inside Claudette (see [§11](#11-sign-in)). |
| Packaging | Windows: MSIX. macOS: signed, notarized `.app` in a `.dmg`. | [Below](#packaging-and-signing). |

### Packaging and signing

The files are in `packaging/`, and `.github/workflows/package.yml` builds them.

- **Windows: MSIX**, one per architecture (x64, arm64), self-contained.
  - `packaging/windows/build-msix.ps1` publishes the app, adds `Package.appxmanifest` and the tile images, builds `resources.pri` for the scaled taskbar icons, packs with `makeappx`, and signs with `signtool`.
  - The identity is `reapazor.Claudette`, the same as the AppUserModelID an unpackaged Claudette uses. The manifest's `Publisher` must match the signing certificate's subject; the script takes it as `-Publisher` or `MSIX_PUBLISHER`.
  - **File and registry write virtualization are off** (`desktop6:FileSystemWriteVirtualization`, with the `unvirtualizedResources` capability). Claude Code and every tool it runs are Claudette's children and share its package container. Otherwise their writes under AppData and HKCU would go to Claudette's private copy, where a terminal `claude` wouldn't see them.
  - Windows only installs signed packages. For a local test, sign with a self-signed certificate whose subject matches the publisher, and trust it.
- **macOS: a `.dmg` per architecture** (arm64, x64) holding `Claudette.app` and an Applications link.
  - `packaging/macos/build-dmg.sh` publishes into the bundle, writes `Info.plist` and the icon, signs every Mach-O file and then the bundle with the hardened runtime, builds the `.dmg`, signs it, notarizes it with `notarytool` and staples the ticket.
  - The bundle identifier is `com.reapazor.claudette`; User Notifications need one ([§10](#10-notifications)). The entitlements allow only what .NET's JIT needs.
  - `Info.plist` has purpose strings for the Documents, Desktop, Downloads, removable and network volume prompts. Claude Code runs as Claudette's child, so macOS asks about Claudette when Claude Code reads a project in one of those places.
- **The workflow.**
  - A `v*` tag builds signed packages and attaches them to a draft GitHub release. **Run workflow** builds them for a given version.
  - A pull request that changes `packaging/` builds them unsigned, to check the scripts.
  - Signing and notarization use repository secrets, listed at the top of the workflow. Without them, the packages are built unsigned.
- **The icon** is a placeholder drawn by Claudette's own renderer: `packaging/icon/claudette-1024.png` for macOS, and `src/Claudette.App/Assets/claudette.ico` and `packaging/windows/Assets/` for Windows. Replace those files to change it.

> **Not yet tested on a real machine:** installing and running the MSIX and the `.dmg`, and signing and notarization, which need the certificates. The pull request build checks that both packages build.

## 3. Main Window

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ● ● ●  Claudette                                                           │
│ ┌ Session ███████████░░░░░░░ 62%  resets 2h 14m ┐  Weekly ███░░░ 38%       │
│ └ ╱╱╱ at this rate: limit hit in 1h 05m !       ┘  Fable  █░░░░░ 12%       │
├──────────────────┬─────────────────────────────────────┬───────────────────┤
│ + New tab      « │                                     │ Changed files     │
│ ▾ ● api        + │  You: refactor the auth middleware… │ M src/auth.cs +12 │
│ │ ● refactor au… │                                     │ A src/token +40   │
│ │   Opus · High  │  Claude: I'll start by reading…     │                   │
│ │ ! fix login b… │   ┌ Read  src/auth.cs ────────────┐ │                   │
│ │   Needs input  │   └───────────────────────────────┘ │                   │
│ ▸ ● docs  !    + │   ┌ Edit  src/auth.cs (+12 −3) ───┐ │                   │
│                  │   └───────────────────────────────┘ │                   │
│                  │   ┌ Allow Bash: dotnet test? ─────┐ │                   │
│                  │   │ [Allow] [Always allow] [Deny] │ │                   │
│                  │   └───────────────────────────────┘ │                   │
├──────────────────┼─────────────────────────────────────┴───────────────────┤
│ History          │ ~/src/api  Opus ▾  High ▾  Default ▾  Ctx 41%  1.2M tok │
│ 2.1.290 is ready │ ┌──────────────────────────────────────────┐            │
│ Settings         │ │ Message Claude…                          │ [ ■ Stop ] │
│                  │ └──────────────────────────────────────────┘            │
│                  │ Suffixes ▾  [Clarify first ×]                           │
└──────────────────┴─────────────────────────────────────────────────────────┘
```

1. **Usage header**, across the top. Always visible. Session usage is the most prominent item; weekly limits are smaller. See [§6](#6-token-burn-awareness).
2. **Sidebar**, on the left. One row per tab (one tab per session), with a status icon, grouped by working folder. **New tab** is at its top; **History**, the Claude Code update badge and **Settings** are at its foot. It collapses to a rail of status icons. See [§4](#sidebar).
3. **Conversation.** The selected tab's conversation. See [§5](#5-conversation-view).
4. **Side panel (collapsible).** Files changed in this tab ([§8](#8-file-changes--diff-view)), and optionally its running processes ([§4](#process-monitor)).
5. **Composer.** Where you type to the selected tab, plus the Stop button and per-tab controls.

### Visual style

The visual reference is Claude Code's own Visual Studio Code extension:

- A dense, calm layout that follows the OS light or dark theme.
- Tool calls are compact one-line rows with a small status dot (running, done, failed), expandable for detail, not heavy cards.
- Diffs are inline, in red and green.
- Thinking is a collapsed row.
- User prompts sit in a subtle bordered box rather than a chat bubble.
- The composer is a rounded box with the mode and model controls beside it, and a square Stop button.

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
- **Model and effort** are easy to see for every tab. The second line of the tab's row shows them (for example `Opus · High`), and the composer bar shows the same thing in full for the selected tab. See [Model & effort](#model--effort).
- **Tab info card.** Hovering a tab shows a card with the tab's details. It's the one place features add per-tab information, rather than putting it in the tab name. It shows:
  - The full folder path and git branch.
  - Model and effort.
  - Session start time, tokens used and context %.
  - Later features add rows here, for example the Perforce changelist ([§18](#perforce-changelist-in-the-tab-title)).
  - The same card opens from an **ⓘ** button in the composer bar, for the selected tab.
- **Token stats per tab.** Each tab keeps a running count of the tokens it has used:
  - Input, output, cache write and cache read tokens, split by model when the session used more than one.
  - Two time spans: **this session window** (since the current 5-hour window started, which is the part that counts against the session limit) and **all time** for this tab's session.
  - An estimated cost, labeled as an estimate (Claude Code computes it at list price; it is not your bill).
  - Shown in short form in the composer bar (for example `1.2M tok`). Click it for a popover with the full breakdown and a small per-turn chart.
  - Saved with the tab, so the counts survive app restarts and session resumes.
  - The same numbers feed the "which tab is burning the most" view in the Usage panel ([§6](#6-token-burn-awareness)).
- **Grouped by folder.** Tabs that share a working folder sit together in a group, like browser tab groups. In the sidebar a group is a label with its tabs listed under it:
  - Each group has a label with the folder name and a color. A line in the color runs down beside its tabs. The color is picked automatically and can be changed. If two folders have the same name, the label adds the parent folder (`work/api`, `personal/api`).
  - Hovering the label shows the full path. The group's `+` opens a new tab in the same folder.
  - A group can be collapsed to just its label. A collapsed group still shows the most urgent status of its tabs, such as "needs input".
  - Tabs can be dragged to reorder them within their group, and groups can be dragged (by their label) to reorder them. A tab can't be dragged into another group, because its folder is fixed, and pinned tabs stay ahead of the others.
    - A dragged tab is selected, and the list rearranges as soon as the pointer passes the middle of a neighbor. **Move up** and **Move down** in the tab menu do the same from the keyboard or mouse.
  - A group with a single tab still gets a label, so the sidebar always looks the same.
- Closing a tab that is working asks for confirmation, then stops the process. Right-clicking a group label gives **Close group**.
- **Pinned tabs** come back every time Claudette launches, resuming their sessions.
  - Pin or unpin from the tab's right-click menu. A pinned tab shows a pin icon and sits at the start of its folder group.
  - **Close group** and **Close other tabs** skip pinned tabs.
  - Closing a pinned tab asks *"This tab is pinned. Close and unpin it?"*
  - Unpinned tabs aren't restored unless **Also restore unpinned tabs** is on in Settings. See [§9](#restore-on-launch) for what's restored.
- Keyboard: `Ctrl/Cmd+T` new tab, `Ctrl/Cmd+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle, `Ctrl/Cmd+1…9` jump to a tab, `Ctrl/Cmd+B` collapse or expand the sidebar.

### Sidebar

The tabs are listed in a sidebar on the left of the window, rather than a strip across the top, so long session names, a status line and many tabs all fit.

- **A tab's row** has two lines:
  - The status icon, a pin icon if pinned, a gear while a process it started is busy ([Process monitor](#process-monitor)), and the name, cut short with an ellipsis if it doesn't fit.
  - The model and effort, or instead what needs attention: *Needs your input*, or the error.
  - The close button shows on hover and on the selected tab. Hovering the row shows the tab info card; double-clicking renames it.
- **Top:** **New tab**, which opens the picker ([Opening a tab](#opening-a-tab)), and the button that collapses the sidebar.
- **Foot:** **History** ([§9](#history)), the Claude Code update badge when there is one ([§12](#applying-it)), **New build ready** when a source build of Claudette has a new build ([§9](#working-on-claudette)), and **Settings** ([§14](#14-settings)). Later features add their own entries here.
- **Resizing.** Drag the sidebar's edge to make it wider or narrower (180 to 420 pixels; 248 by default). Double-click the edge for the default width. The width is remembered.
- **Collapsing.** The collapse button, or `Ctrl/Cmd+B`, shrinks the sidebar to a rail:
  - The rail shows each group's color, then a square per tab with the first letter of its name and a small status icon. Hovering a square shows the tab info card.
  - A collapsed group shows only its color and its most urgent status.
  - New tab, History, the update badge and Settings stay as icons.
  - Whether the sidebar is collapsed is remembered.
- **Narrow windows.** Below 900 pixels wide the sidebar collapses to the rail by itself, and expands again when the window is widened. Expanding it by hand in a narrow window lasts until the window is widened, when the remembered choice applies again; neither changes that choice.

### Opening a tab

`Ctrl/Cmd+T`, or **New tab** at the top of the sidebar, opens the **New tab** picker. A group's own `+` skips the picker and opens a tab in that group's folder.

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

- Dragging a folder from Finder or Explorer onto the sidebar opens a tab there.
- The command line: `Claudette --folder <path>` opens a tab in that folder on startup. Open Recent and the jump list use this too.
- On macOS, **File → Open Recent** and the Dock icon's menu list recent folders. On Windows, the taskbar jump list does the same.
- Choosing any of these opens a new tab in that folder.
- The lists hold favorites first, then recent folders, up to 10, leaving out folders that no longer exist. Folders with the same name show their parent too (`work/api`), as tab groups do.
- **One Claudette at a time.** A launch while Claudette is running (from the jump list, or by opening the app again) passes its arguments to the running one over a named pipe and exits. The running one comes to the front, and opens a tab if a folder was given. A development copy with its own `CLAUDETTE_HOME` counts as a separate instance.
- The macOS menu bar also has **File → New Tab**, **History…** and **Close Tab**, and the app menu has **Settings…**. Each shows its shortcut from Settings → Keyboard ([§14](#keyboard-shortcuts)).

### Process monitor

An optional view of the processes each tab has started, such as test runs, dev servers, builds and MCP servers, with their CPU and memory use. It's off by default and turned on in Settings → Processes. There's no per-tab switch yet; each tab shows its own processes on the Processes page of its side panel.

- **Summary.** When it's on, the composer bar shows a compact summary for the tab, for example `3 procs · 42% CPU · 1.1 GB`, except while the side panel is open. The count leaves out `claude` itself. The tab itself gets a small activity icon while any child process is using noticeable CPU (5% or more).
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
- **Sampling.** Every 2 seconds while the panel is visible (Settings → Processes → Refresh), and every 10 seconds when only the summary is showing. No sampling runs when the monitor is off.
- **How processes are tracked.** Tracking is always on, even with the monitor off, because tab cleanup relies on it; only sampling is off.
  - **Windows.**
    - Each tab's `claude` process goes into its own Job Object right after it starts, so every descendant is tracked, even if its parent exits. Nested jobs work, so this holds when Claudette itself runs inside a job (as it does when started from VS Code or a terminal).
    - The job doesn't kill on close, so "leave them running" stays possible. If the job can't be created, Claudette falls back to walking parent links.
    - CPU, memory and start times come from the process APIs, and command lines from `NtQueryInformationProcess` (no WMI).
    - Windows gives every console program a console host (`conhost.exe`) in the job; it's hidden from the list and the counts.
  - **macOS and Linux.** Claudette walks the process tree from the tab's `claude` process: `/proc` on Linux, and `ps` on macOS, which is simpler to get right than libproc. A process that detaches and gets re-parented (for example a daemonized dev server) drops out of the tree. Claudette keeps listing any process it has already seen, marked "detached", until it exits. This code compiles but hasn't run on either OS yet.
- **Linking a process to its tool call.** A process that first appears while a Bash call is running is linked to that call. A `task_started` message ties a background task to its Bash call, so **Stop** goes through Claude Code (`stop_task`) for those.
- **Cleanup.** The same tracking lets Claudette end a tab's whole process tree when the tab closes, so no orphaned dev servers are left running.
  - If processes are still running, the close confirmation lists them. The choices are **Close and stop them** and **Close, leave running**.
  - Claudette lets `claude` exit on its own first, so it finishes its transcript, then ends whatever it left behind.
  - Quitting Claudette stops every tab's processes.

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
- Clicking the header opens a **Usage** panel (its own window) with:
  - A larger chart of the current session and the past week.
  - Tokens per tab for the current window, so you can see which session is burning the most.
  - Past sessions and weeks, as far back as the stored [usage history](#usage-history) goes. Each past window shows the highest usage it reached.
- Accounts without plan limits (an API key, for example) get no meters; the header just says Claudette.

### Per-tab context

Separate from plan limits, each tab shows how full its **context window** is (in the composer bar). It warns near the auto-compact threshold and has a quick **Compact** action.

- Clicking the context indicator shows the detail and **Compact now**, which sends `/compact` to the session.
- A `compact_boundary` message adds a "Conversation compacted" note, or says Claude Code compacted it by itself.

### Alerts

An OS notification (optional) when:

- Session usage crosses the warning thresholds.
- The projection says you'll hit the limit before it resets.
- A limit resets.

Each alert fires once per window. The first reading after a restart doesn't alert for levels that were already crossed. The alert also shows as a dismissible line under the header, and the OS notification is skipped while Claudette is in front ([§10](#10-notifications)).

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
- `rate_limit_event` messages from any tab update the header immediately between polls. They carry only the session and weekly windows, so the model-specific readings from the last poll are kept.
- If `get_usage` fails or changes shape, Claudette falls back to `rate_limit_event`, and hides the model-specific meters unless the `/usage` fallback is turned on in Settings. It keeps trying `get_usage` on the normal schedule.
- The `/usage` fallback sends `/usage` to the utility session as a message and reads the text it prints.
- Reset times from the two sources differ by fractions of a second (`02:19:59.95` against `02:20:00`), so they're rounded to the second before being compared.
- After a restart, the header shows the last stored sample (with "as of" its time) until the first poll.

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
- When Claude Code suggests a mode switch instead of a rule (for a file edit it suggests `acceptEdits` for the session), the card offers **Allow all edits this session** in place of **Always allow**.
- When Claude Code marks a request `suppressAlwaysAllowRule` (the rule would grant more than this request), **Always allow** isn't offered.
- A tab with a waiting prompt gets the "Needs input" status. If Claudette isn't focused or the tab isn't selected, it also sends an OS notification ([§10](#10-notifications)).
- Keyboard: `Ctrl/Cmd+Enter` allows, `Ctrl/Cmd+Backspace` denies the oldest waiting prompt in the tab.
  - Not while typing in one of the prompt's own fields, and `Ctrl/Cmd+Backspace` still deletes a word in a field with text.
  - A request Claude Code marks `defaultToNo` can't be allowed from the keyboard.
- **Permission mode picker** per tab: Default, Accept edits, Plan, Bypass permissions.
  - Picking a mode changes it for this session only. The mode a tab starts in comes from Tab settings or the New tabs default.
  - The picker always shows the mode Claude Code reports, including changes it makes itself (for example after a plan is approved).
  - Bypass needs a confirmation and gives the tab a visible warning style: a red border on the tab and the composer, and a warning line above the composer.
  - Claude Code only allows switching into Bypass in a session that was started with bypass allowed. Claudette doesn't start sessions that way, so the switch is refused unless the tab started in Bypass mode, and the tab says how to do that. Launching every session with `--allow-dangerously-skip-permissions` would lift this, but that's left to a deliberate decision.
- Claude Code's own allow/deny rules in settings still apply; Claudette only shows prompts that Claude Code actually asks for. When a rule or the mode denies a tool without asking (a `permission_denied` message), the conversation shows a note.

**Questions and plans.** Two tools reach Claudette through the same `can_use_tool` request and get their own cards:

- **Clarifying questions** (`AskUserQuestion`): each question with its options (radio buttons, or checkboxes for multi-select) and an "Other" field for the user's own answer.
  - **Send answers** allows the tool with `updatedInput` set to the original `questions` plus `answers`, keyed by question text. Several choices are joined with ", ".
  - **Dismiss** denies it.
  - The card replaces the tool's own row.
- **A plan to approve** (`ExitPlanMode`, in Plan mode): the plan as Markdown, taken from the tool's `plan` input. The buttons are:
  - **Approve, accept edits** and **Approve, ask before edits**. These allow the tool with a `setMode` update to `acceptEdits` or `default`, so the session leaves plan mode.
  - **Keep planning…**, which denies it with optional feedback.
- The real-CLI tests confirmed both against Claude Code 2.1.284.

**How it works** (confirmed by the spike):

- **The request.** Claude Code sends a `can_use_tool` control request with `tool_name`, `input`, `description`, `tool_use_id` and `permission_suggestions`.
- **The suggested rule.** The suggestions include ready-made rules, for example `addRules` with `Bash(touch c.txt)` for `localSettings`, or `setMode: acceptEdits`. The first `addRules` suggestion is the default rule shown on the **Always allow** card.
- **Allow:** `{ "behavior": "allow", "updatedInput": <input> }`.
- **Always allow** adds `updatedPermissions: [{ "type": "addRules", "rules": [{ "toolName": "Bash", "ruleContent": "touch:*" }], "behavior": "allow", "destination": "localSettings" }]`. Claude Code then writes `Bash(touch:*)` to `.claude/settings.local.json` itself, and later matching commands don't prompt.
- **Deny:** `{ "behavior": "deny", "message": "<text>" }`.
- **Read-only commands** such as `sleep` never prompt at all.

## 8. File Changes / Diff View

- A collapsible side panel lists the files changed in the selected tab's session: added, modified or deleted, with `+/−` line counts. It is built from the session's Edit/Write tool calls, live and when a transcript is replayed.
  - The **Files (n)** button in the composer bar opens it.
  - The counts compare Claude's "before" with the file on disk now, so later edits by you show up too.
  - A file that's back to how it was shows as unchanged.
- Selecting a file opens a diff view (side-by-side or inline) with syntax highlighting.
  - The view is a window of its own, so it can stay open beside the conversation.
  - It shows the changes with a few lines of context, or the **Whole file**.
  - Highlighting uses TextMate grammars, by file extension, with the dark or light theme to match the app. Files over 20,000 lines, and binary files, are shown without it.
  - A leading byte order mark isn't counted as a change.
- Actions: open in external diff tool, open in external editor (the app the OS uses for that file type), reveal in Finder/Explorer, copy path.
- If the folder is a git repo, a toggle switches to **working tree vs HEAD**. This also shows changes made by Bash commands or by the user.
  - It covers the whole repository, not only the tab's folder.
  - Untracked files count as added.
  - Git runs with `--no-optional-locks`, so refreshing never takes git's index lock.
- **Before content.** Claude Code reports it. The result of every Edit and Write tool call (the `tool_use_result` field on the `user` message that carries the tool result) includes:
  - `originalFile`: the file's full content before the change, or `null` for a new file.
  - `structuredPatch`: the change as diff hunks.
- The "before" side of a file's diff is the `originalFile` from Claude's first change to that file in the session, so diffs are exact even outside a git repo. The spike confirmed this in Accept edits mode too. Claudette never has to snapshot files itself, so there's no race with the tool writing the file.

### External diff tool

- In Settings → Diff tool, the user chooses how diffs open: **Built-in** (the default), a **preset**, or a **custom command**.
- Once a tool is set, **Open in diff tool** appears on every changed file. Double-clicking a file in the changed files panel uses the external tool instead of the built-in view, and a single click only selects it; the built-in view stays in the file's menu.
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
  - The "before" side is written to a temporary read-only file, in a folder of its own per launch. The file keeps its name and extension, for example `auth (before).cs`, so the tool picks the right language.
  - The "now" side is the real file in the working folder, so edits made in the diff tool are saved directly.
  - Temporary files are deleted when the tab closes.
- **Test** in Settings opens a sample diff so the user can check their command works.
- Only presets whose tool is installed are listed. Detection only looks for files, so opening Settings stays quick.
- **Batch-file safety.** On Windows, `.cmd` and `.bat` tools (VS Code's `code.cmd`, or a custom command) run through `cmd.exe`, where a file name could inject commands. Claudette refuses arguments that `cmd.exe` would interpret, such as `%` or `"`. The rare file name that trips this, for example `Q&A.md` in a path without spaces, can still be opened in the built-in view.

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
- Opening an entry resumes that session in a new tab. The earlier conversation is loaded into the view so you can scroll back through it. A session that's already open in a tab just selects that tab.
- History combines two sources:
  - Claude Code's own session storage on this machine, so it includes sessions started in the terminal.
  - Claudette's session library (below), which can include sessions from other machines.
- **Merging the sources.** A session in both is one entry: the library adds its name and, when another machine used it more recently, that machine. A session only in the library (from another machine, or older than Claude Code's cleanup) opens through "Restoring on another machine" below.
- **Speed.** History reads every transcript line by line, skipping lines cheaply before parsing them, and caches each file by size and date, so opening it again only reads what changed.

### Session library (sync across machines)

Claudette keeps its own **session library** in a folder the user chooses (Settings → Sessions). By default it's in the app data folder. Pointing it at a folder that a sync client keeps up to date, such as Google Drive for desktop, Dropbox or OneDrive, lets other machines see and restore the same sessions. Claudette doesn't talk to Google Drive or any cloud API; it just reads and writes files, and the sync client does the rest.

**What's in the library.** One folder per session holding:

- A **session record** (JSON): the tab name, model, effort, per-tab overrides, token stats, which machine last used it and when, and the project identity (below).
- A **copy of Claude Code's transcript** (`.jsonl`), plus subagent transcripts.

Claude Code's credentials and settings are never copied.

**Writing.**

- Claudette copies the transcript into the library after each turn finishes, never while Claude Code is writing it. It waits a second after the turn's result, so Claude Code has finished writing.
- Each file is written to a temporary name, then renamed, so a sync client never uploads a half-written file. The record is written last, so a record in the library means its transcript is there too.
- A file that hasn't changed (same size, and a modified time within 2 seconds, since some synced drives store coarse times) isn't copied again.
- Library copies aren't affected by Claude Code's own cleanup of local transcripts (30 days by default), so the library also works as a longer-term archive. It has its own retention setting.

**Restoring on another machine.**

1. **Find the project.** Folder paths differ between machines (`D:\Repos\api` vs `/Users/me/src/api`), so the session record stores a project identity: the git remote URL, the branch, and the path inside the repo.
   - Remote URLs are compared in a normal form, so `git@github.com:Owner/Repo.git` and `https://github.com/owner/repo` match.
   - Claudette looks for a matching folder among the folders it remembered, recent and favorite folders, and open tabs, and prefers a clone on the recorded branch. Then it tries the recorded path, in case it exists here too.
   - If it can't find one, it asks the user to pick the folder and remembers the answer for that machine.
   - The identity is read from the `.git` folder's files (including worktrees), without running git. Whether there were uncommitted changes needs `git status`, so that part runs git.
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
- The lease names the machine and a random id for this run of Claudette, so two copies of Claudette on one machine are told apart.
- When a refresh finds another machine's name in the lease, the session was taken over. The tab stops its `claude` process, becomes read-only, and says where the session continued.
- If the sync client creates conflict copies (for example `session (1).jsonl`), Claudette shows them in History as separate, forked entries. It never merges them.
  - It recognizes the numbered copies Google Drive and OneDrive make, Dropbox's "conflicted copy", Syncthing's `.sync-conflict-…`, and `<id>-<machine>.jsonl`.
  - Opening one copies it to a folder of its own and resumes it with `--fork-session`, so it can't overwrite the working copy of the original.
- Library retention (Settings → Sessions) never deletes a session that's open here or held by a live lease.

**Privacy.** Transcripts contain code, command output and anything else Claude read in the project. When the user picks a library folder inside a known cloud-sync location, Claudette says so and asks them to confirm.

**Rejected alternative.** Pointing Claude Code's whole config folder at the cloud drive (`CLAUDE_CONFIG_DIR`) would also sync credentials and settings, and have several machines writing the same live files at once. The library copies only transcripts, only between turns.


### Working on Claudette

Claudette can host the Claude Code session that works on Claudette's own source. When it runs from a source build, its state survives the rebuilds that session makes: it restarts into each new build with every tab as it was.

- **Source builds.** A Claudette whose program is in a `bin` folder below a checkout with `Claudette.slnx` is a source build, for example one started with `dotnet run --project src/Claudette.App`. Everything below applies only to source builds.
- **Running from a copy.** A source build copies its build output to `builds` in the data folder and runs from there, so the build output itself is never in use. On Windows a running program's files are locked, so every rebuild would fail; elsewhere, replacing a running program's files can crash it.
  - Only this platform's native libraries are copied, about 40 MB on Linux and macOS and 140 MB on Windows, rather than the 700 MB for every platform.
  - Each build is copied once. The copy the running build came from is kept, since that build may still be closing; older copies are deleted.
  - With a debugger attached, or with `CLAUDETTE_RUN_IN_PLACE=1`, a source build runs in place.
- **Noticing a new build.** Claudette checks the build output every 2 seconds. A build counts once its files have stopped changing from one check to the next. A failed build writes nothing, so it's never offered.
- **Offering the restart.** **New build ready** appears at the foot of the sidebar ([§4](#sidebar)). Its dialog says when the build was made, and offers:
  - **Restart now.** A working tab is interrupted, and resumes its session after the restart.
  - **Restart when idle**, while a tab is working: it waits until no tab is starting, working or waiting on the user.
  - **Restart into new builds by itself when no tab is working**, remembered on this machine. With it on, a Claude Code session that rebuilds Claudette sees the restart as soon as its turn ends.
- **What's kept:**
  - Every open tab, pinned or not, in order, with its session, name, overrides, kept suffixes and token stats.
  - The message typed in each tab, with its one-off suffixes.
  - The selected tab, and the window's position and size.
  - Tabs whose Claude Code was running start again straight away and resume their sessions. The others start when selected, as on launch.
  - Processes the tabs started are stopped, as when Claudette closes ([Process monitor](#process-monitor)).
- **The handover:**
  1. The running build copies the new one and writes a snapshot of the above to `restart.json` in the data folder. It saves its state, then stops every tab as closing does, and from then on writes no settings or state.
  2. It stops taking later launches ([Other ways in](#opening-a-tab)), and starts the new build with `--source-build <build output> --restore <nonce>`.
  3. The new build opens the snapshot's tabs instead of the saved ones and places its window there. Once its startup checks are done and its first page has drawn, it writes the nonce to `restart-ready` in the data folder, and the old build closes.
- **If the new build doesn't start.** It may exit first, or not say it's up within 60 seconds, in which case it's stopped. Either way the old build takes its tabs back, and starts taking launches again. Its dialog shows why, with the last lines the new build wrote to its error output. The build stays offered, to try again once it's fixed, but isn't restarted into by itself again. A broken change never costs the session that's fixing it.

## 10. Notifications

Native OS notifications (Windows toast, macOS User Notifications). Each type can be turned on or off in Settings:

- A background tab finished its turn.
- A tab needs permission or input.
- A tab's process errored or exited unexpectedly.
- Usage alerts (see [§6](#6-token-burn-awareness)).
- Claude Code needs you to sign in (see [§11](#11-sign-in)).
- A Claude Code update is ready (see [§12](#12-claude-code-updates)).

Clicking a notification brings Claudette to the front and goes to the relevant tab or screen. Notifications are skipped when Claudette is focused and that tab is already selected. The Dock (macOS) and taskbar (Windows) show a badge with the number of tabs needing input.

- **What each one says.** Tab notifications carry the tab's name as their title:
  - **Finished:** the first line of Claude's reply. Only a turn that ends normally counts; one you stopped, or that ended with an error, doesn't.
  - **Needs input:** what's waiting, such as *"Allow this command? npm test"*, *"Claude has a question: Which database?"* or *"Claude has a plan for you to review."*
  - **Errors:** *"Claude Code stopped unexpectedly (exit code 3)."*, or why it couldn't start.
  - **Check-ins:** Settings → Check-ins → **Notify me when a check-in is sent** (off by default), which Tab settings can override ([§5](#check-ins-on-long-turns)).
- **Skipping.** App-wide notifications (usage alerts, sign-in, updates) are skipped while Claudette is focused, because the header or the sign-in screen already shows them. Usage alerts also keep their line under the header.
- **One per subject.** A newer notification replaces an older one of the same kind for the same tab. A tab's notifications are taken away once you look at it; a waiting-prompt notification also goes once the prompt is answered. An update is announced once per version.
- **Clicking.**
  - A tab notification selects the tab, expanding its group if it's collapsed.
  - A usage alert opens the Usage panel, and an update opens the update dialog.
- **Badge.** Settings → Notifications → **Show the number of tabs needing input on the Dock or taskbar icon**. On Windows it's an overlay icon on the taskbar button, drawn by Claudette.
- **How each OS does it** (the code is in `Claudette.Platform/Notifications`):
  - **Windows:** WinRT toasts (`ToastNotificationManager`), called through source-generated COM interop so the app stays a plain `net10.0` build. A click raises the toast's `Activated` event in the running Claudette. An MSIX install has package identity. Run unpackaged, Claudette sets its AppUserModelID (`reapazor.Claudette`) and registers it under `HKCU\Software\Classes\AppUserModelId`, as the Windows App SDK does. The badge uses `ITaskbarList3::SetOverlayIcon`.
  - **macOS:** `UNUserNotificationCenter` through the Objective-C runtime, with a delegate that reports clicks and lets notifications show while Claudette is in front. It needs the app bundle's identifier, so a build run with `dotnet run` has no notifications and Settings says so. The badge is the Dock tile's `badgeLabel`.
  - **Linux:** `notify-send --wait` with a default action, which reports a click. Without `notify-send`, there are no notifications.

> **Not yet tested on a real machine:** showing and clicking notifications on Windows and macOS, and the badges. CI builds a real toast through WinRT on Windows (without showing it), and checks the Objective-C string calls on macOS.

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

How Claude Code updates depends on how it was installed. `claude doctor` is a read-only diagnostic that reports the install method, whether auto-updates are on, the release channel and the last update attempt. For example (2.1.284):

```
Claude Code doctor

Running: native (2.1.284)
Commit: 2b8ce618c24d
Platform: linux-x64
Path: /home/me/.local/share/claude/versions/2.1.284
Config install method: native
Search: OK (/usr/bin/rg)
Auto-updates: enabled
Auto-update channel: latest
Last update attempt: success → 2.1.284 (2026-09-28)

1 warning found
- Leftover npm global installation at /usr/local/bin/claude
  Fix: Run: npm -g uninstall @anthropic-ai/claude-code
```

What Claudette reads from it (the command is documented; the line format isn't, so every field is optional):

- **`Running:`** gives the install type: `native`, `npm-global`, `npm-local`, `package-manager` or `development`. Anything else counts as unknown and is treated like native.
- **`Package manager:`** appears only for package-manager installs: `homebrew`, `winget`, `deb`, `rpm`, `apk`, `pacman`, `mise` or `asdf` in 2.1.284. For those, `Auto-updates:` reads `Managed by package manager`.
- **`Path:`** shows the Homebrew cask, as in `…/Caskroom/claude-code@latest/…`.
- **`Auto-updates:`** is `enabled` or `disabled (<reason>)`. The reason is `set by env: DISABLE_AUTOUPDATER`, `set by env: DISABLE_UPDATES`, `config` or `development build`.
- **Warnings** follow `N warnings found`, as `- <issue>` lines, each with an indented `Fix:`. Settings → Claude Code shows them, which covers the npm case below.
- `doctor` reads the settings files of its working folder, so Claudette runs it (and the other update commands) in its own utility folder.

| Install method | How Claude Code updates | What Claudette offers |
|---|---|---|
| Native installer (the default) | Updates itself in the background. The new version is used the next time a `claude` process starts. | Tell the user an update is installed. **Update now** runs `claude update`. |
| npm | Updates itself, if the npm global folder is writable. | Same as native. If `claude doctor` says it can't update, show the fix it suggests. |
| Homebrew | Doesn't update itself. | **Update now** runs `brew upgrade claude-code`, or `claude-code@latest` if that is the cask installed. |
| WinGet | Doesn't update itself. | **Update now** runs `winget upgrade Anthropic.ClaudeCode`. Windows locks a running executable, so this fails while any tab is running. In that case Claudette offers **Update on next launch**, which runs the upgrade at the next start, before any tab starts its process. |
| apt / dnf / apk | Needs admin rights. | Show the command to run; don't run it. |

### Detecting an update

- On launch and every 4 hours, Claudette runs `claude --version` and `claude doctor`. **Check for Claude Code updates automatically** in Settings → Claude Code turns this off, and **Check now** runs it by hand.
- Each tab knows which version it is running: the installed version when its process started, confirmed by `claude_code_version` in its `system/init` message. If the installed version is newer, that tab is running an old version.
- For Homebrew and WinGet, Claudette checks with the package manager:
  - Homebrew: `brew outdated --cask --greedy --json=v2 <cask>`, which reads Homebrew's local index, so it knows what Homebrew last fetched.
  - WinGet: `winget list --id Anthropic.ClaudeCode --exact --upgrade-available`. Not `winget upgrade --id …`, which would install the update.
- There is no documented "check only" command for native installs, so Claudette relies on the auto-updater and notices the new version with `claude --version`.
- If updates are turned off (`DISABLE_UPDATES`, set directly or through managed settings), Claudette shows the version but doesn't offer to update.

### Applying it

- A small, non-blocking badge appears at the foot of the sidebar: *"Claude Code 2.1.290 is ready"* (an icon when the sidebar is collapsed). Clicking it shows the current and new version, a link to the Claude Code changelog, and the actions from the table above.
  - It appears when the package manager has a newer version than the one installed, or when the installed version is newer than the one an open tab is running.
  - **Dismiss** hides it until a newer version comes along. It also goes away once no open tab runs an older version.
  - Settings → Claude Code shows the same details and actions, plus the install method, auto-update state, channel, last update attempt and `claude doctor`'s warnings.
- **Open tabs are never restarted.** Each tab's `claude` process keeps running the version it started with until the tab is closed. Claudette doesn't restart tabs to apply an update, automatically or otherwise.
  - **New tabs** always start on the newly installed version.
  - **To move an open tab to the new version**, close it and open a new one, or reopen its session from History.
  - **Pinned tabs** pick up the new version the next time Claudette launches, because restored tabs start new processes.
- A tab running an older version than the one installed shows a small note in its tooltip, for example *"Running Claude Code 2.1.284; 2.1.290 is installed. New tabs use 2.1.290."*
- **Update now** shows the command's output in a small progress dialog and reports the new version when it succeeds. `claude update` prints `Successfully updated from <old> to version <new>`, or `Claude Code is up to date (<version>)`; either way Claudette reads the new version with `claude --version` afterwards.
- **WinGet.** Claudette's hidden utility session is a running `claude` too. **Update now** stops it first; it starts again when next needed. While any tab is running, **Update on next launch** replaces **Update now**. It's saved with this machine's state, and runs at the next start, before the utility session or any tab starts its process.
- **Minimum version.** Claudette declares the lowest Claude Code version it supports. If the installed version is older, the setup screen asks you to update before any tab starts, with the same **Update now** action.

> **Not yet tested on a real machine:** the Homebrew and WinGet checks and upgrades. Their commands and output parsing are covered by unit tests with recorded output; `claude update` and `claude doctor` are also covered with `fake-claude` and the real CLI.

## 13. Architecture

```
┌──────────────────────── Claudette.App (Avalonia) ────────────────────────┐
│  Views + ViewModels: MainWindow, UsageHeader, Sidebar, Conversation,     │
│  Composer, DiffView, Processes, History, SignIn, Settings                │
└───────────────┬───────────────────────────────┬──────────────────────────┘
                │                               │
┌───────────────▼──────────────┐  ┌─────────────▼──────────────┐  ┌───────────────────┐
│ Claudette.Core               │  │ Claudette.Usage            │  │ Claudette.Platform│
│  ClaudeSession (1 per tab)   │  │  UsagePoller (sampling)    │  │  Process monitor  │
│   ├ process + stdio          │  │  UsageStore (SQLite)       │  │  (Job Objects,    │
│   ├ protocol reader/writer   │  │  BurnRate (projection)     │  │   /proc, ps)      │
│   └ typed event stream       │  │  UsageAlerts               │  │  Notifications,   │
│  TranscriptReader, History   │  └────────────────────────────┘  │   Dock/taskbar    │
│  SessionLibrary, leases      │                                  │   badge           │
│  Diffs: line diff, changed   │                                  │  Jump list, one   │
│   files, external diff tools │                                  │   instance        │
│  Claude Code updates         │                                  └───────────────────┘
│  Source builds: copies, new  │
│   builds, restart snapshots  │
│  Git: identity, working tree │
│  Auth, install checks        │
│  Settings, state, sync       │
└───────────────┬──────────────┘
                │ stdin/stdout (JSON lines)
        ┌───────▼───────┐
        │  claude CLI   │  × one per tab
        └───────────────┘
```

- **Claudette.Core** has no UI dependencies, so it can be unit tested and could be reused by another front end. External diff tools live here rather than in Platform: they only look for files and start processes through `IProcessLauncher`. So does running a source build from a copy and restarting it into new builds ([§9](#working-on-claudette)), which is plain file copying and process starting on every OS.
- **Claudette.Usage** holds the usage engine, with no UI: parsing, the SQLite history, the burn rate and projection, alerts and the polling schedule.
- **Claudette.Platform** holds the OS-specific code: the process monitor, and notifications with the Dock and taskbar badge ([§10](#10-notifications)).
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
          --thinking-display summarized --forward-subagent-text
          --model <model> --effort <level> --permission-mode <mode>
          [--resume <session-id or transcript path>]
```

- `--permission-prompt-tool stdio` sends permission prompts to Claudette as control requests. The TypeScript SDK passes this flag when a `canUseTool` callback is set.
- `--thinking-display summarized` makes newer models return thinking text; by default they send empty thinking blocks. The flag isn't in `claude --help`, but the Agent SDKs pass it.
- `--forward-subagent-text` includes subagents' text and thinking in the stream, so subagent groups can show them.
- **Clean environment.** Claude Code sets session variables for the processes it starts, such as `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_ENTRYPOINT` and `CLAUDE_CODE_MESSAGING_SOCKET`. If Claudette was started from a terminal inside Claude Code, those variables make `claude` behave as a child session; in the spike it ignored the API key and reported "Not logged in".
  - Claudette removes exactly those variables. The full list is `ClaudeEnvironment.SessionVariables`, tracked in `compat/surface.yaml`.
  - It doesn't strip by prefix, because variables like `CLAUDE_CONFIG_DIR` and `CLAUDE_CODE_USE_BEDROCK` are user configuration.
  - The real-CLI tests run from inside Claude Code confirmed that the list is enough.

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
| Usage | Warning thresholds (default 75% and 90%). Burn rate window (default 30 minutes). Show model-specific weekly meters, and read them from `/usage` if `get_usage` stops working (off by default). Keep usage history: 1 day, 1 week, 1 month (default), 1 year or forever, with a **Clear usage history** button beside it. See [Usage history](#usage-history). |
| Quick suffixes | The list of suffixes: label, text and optional shortcut. Add, edit, reorder, delete. See [§5](#quick-suffixes). |
| Check-ins | On/off. Run time before checking in. Quiet time before checking in. Check-in message text. Notify me when a check-in is sent. See [§5](#check-ins-on-long-turns). |
| Diff tool | Built-in, a preset or a custom command, with **Test**. See [§8](#external-diff-tool). |
| Notifications | On/off for each type in [§10](#10-notifications). Dock/taskbar badge on/off. |
| Keyboard | List of shortcuts, each one rebindable ([below](#keyboard-shortcuts)). |
| Advanced | Protocol logging and **Open log folder**. **Diagnostics** page ([§16](#staying-tolerant-at-runtime)). Extra command-line arguments passed to `claude`. Minimum supported Claude Code version (read-only). |

### Keyboard shortcuts

Settings → Keyboard lists every shortcut Claudette handles, with its default from the section that describes it: new tab, close tab, next and previous tab, go to tab 1–9, History, Settings, collapsing the sidebar, Stop, the quick suffixes menu, and allowing or denying the waiting prompt.

- **Rebinding.** Click a shortcut and press the new keys; Esc cancels. **Reset** puts one back, **Remove** clears it, and **Reset to defaults** restores them all.
- **One key for both OSes.** Shortcuts are stored with a *Primary* modifier: Ctrl on Windows and Linux, Cmd on macOS. That way a shortcut synced between a Windows machine and a Mac means the same thing on both. Ctrl is its own modifier only on macOS; elsewhere it is Primary.
- **Refused shortcuts.** A shortcut already used by another command or a quick suffix is refused, and the row names the conflict. So is a letter, digit or punctuation key without Ctrl, Alt or Cmd, since it would get in the way of typing. Escape, Tab, Enter, Backspace, Delete and function keys are allowed on their own.
- **Go to tab 1–9** is one shortcut for all nine digits; rebinding it takes any digit and keeps its modifiers.
- **Fixed keys**, listed on the page but not rebindable: Enter sends and Shift+Enter starts a new line; in the new tab picker and the quick suffixes menu, 1–9 pick an entry.
- **Quick suffixes** each get their own optional shortcut in Settings → Quick suffixes ([§5](#quick-suffixes)), checked for conflicts the same way.
- Tooltips and the composer's placeholder show the current shortcuts.

**Search.** The box above the categories filters settings by name: it lists matching settings with their category, and picking one opens that category.

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
- **When it syncs.** At launch, after each settings change, and once a minute to pick up changes from other machines.
- **How it merges.** Each setting is a path such as `appearance.theme`; lists such as the quick suffixes sync as one value. Claudette remembers the value and time it last saw or published for each path.
  - A local edit is published with the current time.
  - A newer synced change is applied here.
  - When both changed, the newer one wins.
  - Equal values are never a conflict.
- Notification settings sync. Keyboard shortcuts sync once they can be rebound.

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
| UI | View models, and views: sidebar, composer, chips, permission cards, meters | View-model tests with no UI; Avalonia.Headless for rendering and input; snapshot tests with Verify | None |
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

- xUnit v3, Avalonia.Headless.XUnit, Verify (snapshot testing) and Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`). xunit.v3 stays on 3.2.x until Avalonia.Headless.XUnit supports 4.x.
- GitHub Actions (`.github/workflows/ci.yml`):
  - A build-and-test job on Windows, macOS and Linux runs everything except `RealCli` and `Live`, for every push and pull request.
  - A second Linux job installs Claude Code and runs the `RealCli` tests.

### Where things are

| Piece | Location |
|---|---|
| Fake transport and replay transport | `tests/Claudette.Core.Tests/Support/` |
| Protocol fixtures | `tests/Claudette.Core.Tests/Fixtures/protocol/2.1.284/` (recorded in the spikes, with paths and personal details removed) |
| `fake-claude` | `tools/Claudette.FakeClaude/`. Scripted by the prompt (`ASK_PERMISSION`, `SLOW`, `CRASH`) and by environment variables, rather than scenario files. |
| Mock Messages API | `tools/Claudette.MockApi/`. Runs in-process in tests, or on its own with `dotnet run`. |
| Tests against `fake-claude` and the real CLI | `tests/Claudette.IntegrationTests/`. The real-CLI tests are tagged `RealCli`. |
| View model tests | `tests/Claudette.App.Tests/`. `Support/TabTestHarness.cs` gives a tab a scripted Claude Code connection, a fake clock, a temporary data folder and a fake process tracker. |
| Usage engine tests (parsers, store, burn rate, alerts, poller) | `tests/Claudette.Usage.Tests/`, with recorded `get_usage`, `rate_limit_event` and `/usage` fixtures |
| Process monitor tests | `tests/Claudette.Platform.Tests/`. Some start real process trees on the current OS; the Linux ones skip elsewhere. |
| History, library, leases, settings sync, diffs, git | `tests/Claudette.Core.Tests/{History,Library,Settings,Diffs,Git}`. Git tests use the real `git` in a temporary repo and skip without it. |
| Real-CLI checks of questions and plans | `RealCliTests`, with the mock's `ASK_QUESTION` and `EXIT_PLAN` scripts |

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

A scheduled GitHub Action (`.github/workflows/compat.yml`) runs once a day. It uses `compat/check.mjs`, a dependency-free Node script. When npm's `latest` tag shows a Claude Code version newer than `lastTested` in `compat/surface.yaml`, and there's no report for it yet, it:

1. **Collects:**
   - The changelog entries after `lastTested`.
   - The Agent SDK type definitions (`sdk.d.ts`) for both versions. It finds the SDK release for each CLI version through the `claudeCodeVersion` field in the SDK's npm metadata.
   - The docs pages listed in the surface file.
   - `claude --help` from the new version.
2. **Diffs:**
   - The SDK types between the two versions.
   - The docs pages and CLI help against the snapshots kept in `compat/docs/` and `compat/cli-help.txt`.
3. **Matches** the changelog and changed diff lines against the identifiers in `compat/surface.yaml`, as whole tokens, so changes to things Claudette uses are listed first.
4. **Tests** by installing that version and running the free test suite against it, including the real-CLI tests with the mock model.
5. **Reports** by opening a GitHub issue, *"Claude Code 2.1.285 compatibility report"*, labeled `compat`.
   - The issue shows test results first, then matched changes, then the full diffs in collapsed sections.
   - If nothing matched and every test passed, the issue is closed automatically and kept as a record.

A dry run against the two versions before 2.1.284 (`node compat/check.mjs report --from 2.1.281 --version 2.1.284`) flagged eight changes. One was a changelog line about `claude -p` startup; another was an Agent SDK doc comment change on `apply_flag_settings`.

### Tested versions

- Claudette records two versions:
  - `ClaudeLocator.MinimumVersion` (`minimum` in `compat/surface.yaml`): the hard floor from [§12](#applying-it).
  - `ClaudeLocator.LastTestedVersion` (`lastTested`): updated each time a compatibility report is handled.
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
3. Bump `lastTested` in `compat/surface.yaml` and `ClaudeLocator.LastTestedVersion` together; a unit test checks they match.
4. Refresh the snapshots: `node compat/check.mjs update-snapshots --help-file <output of claude --help>`.
5. If the change breaks a released Claudette version, ship a patch release. Raise `minimum` (and `ClaudeLocator.MinimumVersion`) only if older Claude Code versions can no longer be supported.

## 17. Milestones

1. **Skeleton.** ✅ Built 2026-09-28.
   - **Scope:**
     - Avalonia app, Claude Code detection and version check, sign-in.
     - One tab: launch `claude`, send a prompt, stream the reply as Markdown, basic Allow/Deny permission cards, Stop.
     - Status bar with model, mode and context window %.
     - Test harness: fake transport, `fake-claude`, mock model server, first protocol fixtures and CI.
     - `compat/surface.yaml` and the daily compatibility check.
     - The utility session.
   - **Still to verify:** completing an in-app sign-in (the `claude_oauth_*` requests), which needs a real sign-in into a throwaway config.
   - The spikes were done on 2026-09-28. Their findings are recorded in the sections they affected; the scripts are in [`spikes/`](spikes/README.md).
2. **Tool rendering.** ✅ Built 2026-09-28.
   - Tool rows with status dots, expandable to full input and output.
   - Inline Edit and Write diffs from `structuredPatch`, and Bash command and output.
   - Collapsed thinking.
   - Subagents as nested groups, routed by `parent_tool_use_id`.
   - The pinned to-do list, for both `TodoWrite` and the Task tools.
   - A single retry note that updates in place, a summary after each turn, "Jump to latest", and clearing on `/clear`.
3. **Tabs & settings.** ✅ Built 2026-09-28.
   - **Tabs:** multiple tabs grouped by folder, with group colors, collapse and close. The new-tab picker has search, favorites and recents.
   - **Tab details:** status icons, AI titles with the first-prompt fallback, rename, pin, and the tab info card.
   - **Session controls:** model and effort pickers, with confirmation before a model switch, and token stats with a breakdown.
   - **Persistence:** restore on launch, lazy start, and the earlier conversation reloaded from the transcript.
   - **Settings:** the Settings window for the current categories, and per-tab overrides.
   - **Composer:** check-ins and quick suffixes.
   - **Keyboard shortcuts** for tabs and settings.
   - **Deferred to later milestones:**
     - Dragging tabs to reorder, the Settings search box, and per-suffix shortcuts and number keys in the suffix menu: built in milestone 7.
     - The "this session window" token split, which needs milestone 5's usage data.
     - OS notifications for check-ins: built in milestone 7.
4. **Permissions.** ✅ Built 2026-09-28.
   - **Prompts:** inline prompts with the tool's input and a diff preview for edits.
     - **Always allow** shows the exact rule and lets you edit it; the menu offers **Allow for this session only**.
     - Mode suggestions (accept edits), and **Deny with a message**.
     - `defaultToNo` and `suppressAlwaysAllowRule` are respected.
   - **Questions and plans:** cards for clarifying questions (`AskUserQuestion`) and plan approval (`ExitPlanMode`), both confirmed against the real CLI.
   - **Mode picker:** the permission mode picker, with a confirmation and warning style for Bypass.
   - **Keyboard and notes:** Ctrl/Cmd+Enter and Ctrl/Cmd+Backspace answer prompts, and notes appear for denials Claude Code made by itself.
   - **Deferred:**
     - The OS notification for a waiting prompt: built in milestone 7.
     - Switching into Bypass mid-session works only for tabs started in Bypass mode ([§7](#7-permission-prompts)).
5. **Usage.** ✅ Built 2026-09-28.
   - **Header:** meters, countdowns and the sparkline with projection.
   - **Polling:** `get_usage` through the utility session, plus `rate_limit_event` updates and the `/usage` fallback.
   - **History and panel:** the SQLite usage history with retention and **Clear usage history**, and the Usage panel.
   - **Alerts:** shown in the header.
   - **Per tab:** each tab's "this session window" tokens and per-turn chart, and **Compact**.
   - **Deferred:** OS notifications for alerts, built in milestone 7.
6. **History, sync & diffs.** ✅ Built 2026-09-28.
   - **History:** History (Ctrl/Cmd+Shift+H, or from the new-tab picker).
   - **Session library:**
     - Copies after each turn, leases with take-over and **Open a copy**, and conflict copies.
     - Restoring on another machine, with project identity and the code check.
     - Retention, **Move library…**, the cloud-folder warning, and settings sync.
   - **Changed files:** the Changed files panel (session edits, or working tree vs HEAD), the built-in diff view with syntax highlighting, and external diff tools with presets, a custom command and **Test**.
   - **Processes:** the process monitor, and stopping a tab's processes when it closes.
   - **Deferred:**
     - The per-tab switch for the process monitor.
     - Running the macOS and Linux process code for real (it compiles and its parsers are tested).
     - Resuming a transcript recorded on the other OS ([§9](#session-library-sync-across-machines)).
     - **Choose folder…** and **Unpin and close** for a restored tab whose folder is gone; it still shows a note.
7. **Polish & ship.** ✅ Built 2026-09-29.
   - **Claude Code updates ([§12](#12-claude-code-updates)):**
     - Checks with `claude --version`, `claude doctor` and Homebrew or WinGet, at launch and every 4 hours.
     - The update badge and its dialog, **Update now** for each install method, and **Update on next launch** for WinGet.
     - The tab note for an older version, and **Update now** on the too-old setup screen.
   - **Notifications ([§10](#10-notifications)):** every type, with its setting, click routing and the Dock/taskbar badge, on Windows, macOS and Linux.
   - **Keyboard ([§14](#keyboard-shortcuts)):** Settings → Keyboard with rebindable, synced shortcuts, per-suffix shortcuts, and 1–9 in the suffix menu.
   - **Settings:** the search box, and the Notifications and Keyboard categories.
   - **Platform chrome ([§2](#2-platform--tech-stack), [§4](#opening-a-tab)):**
     - Mica on Windows 11.
     - The macOS menu bar (Settings…, File with Open Recent) and Dock menu, and the Windows jump list.
     - One running instance, which takes later launches' `--folder`.
     - Folders dropped on the sidebar open tabs.
     - Dragging tabs and groups.
   - **Sidebar ([§4](#sidebar)):** the tab strip was replaced by a sidebar on the left, which collapses to a rail (by hand, and in narrow windows), can be resized, and holds History, the update badge and Settings at its foot.
   - **Packaging ([§2](#packaging-and-signing)):** MSIX, and a signed, notarized `.dmg`, built by `package.yml`, with a placeholder icon.
   - **Still to verify on real machines** (CI builds and runs the platform tests on Windows and macOS, but nothing there is looked at or clicked):
     - Showing and clicking toasts and macOS notifications, and both badges.
     - Mica, the jump list, and the macOS menus and Dock menu.
     - Homebrew and WinGet updates.
     - Installing the MSIX and `.dmg`, and signing and notarization, which need the certificates.
   - **Deferred:**
     - A macOS-style theme ([§2](#2-platform--tech-stack)). macOS uses the Fluent theme for now; this needs a design decision.
     - The replacement for the placeholder icon.
8. **Working on Claudette.** ✅ Built 2026-09-29. A source build runs from a copy of its build output, notices new builds, and restarts into them with every tab, draft and the window as they were, taking its tabs back if the new build doesn't start ([§9](#working-on-claudette)). Checked end to end on Linux under Xvfb: rebuilding while it ran, the automatic restart, and a broken build being refused.
   - **Still to verify on Windows:** rebuilding while a copy runs, which is what the copy is for, and starting the new build from Explorer and from `dotnet run`.
9. **Later.** The features in [§18](#18-future-features), in an order decided after v1 ships.

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

### Perforce changelist in the tab title

When Claude is working in a specific Perforce changelist, Claudette shows its number. It's always a row in the tab info card ([§4](#4-tabs--sessions)), and optionally (a setting) a badge on the tab itself, for example *"fix login bug · CL 12345"*.

- **Detecting the changelist.** Claudette watches the tab's Bash tool calls and their results:
  - Commands that name a changelist: `p4 edit -c 12345`, `p4 add -c`, `p4 reopen -c`, `p4 shelve -c`, `p4 change -o 12345`, `p4 submit -c`.
  - Output that creates one: *"Change 12345 created."*
  - The most recently used changelist wins. The default changelist isn't shown.
- **Showing it.**
  - A "Perforce changelist" row in the tab info card.
  - If **Show changelist on tabs** is on (Settings → Perforce), also a `CL 12345` badge after the tab name. The badge is separate from the name, so renaming the tab doesn't drop it.
  - Several changelists in one session: the latest is shown, and the info card lists them all.
- **Actions** (on the info card row or the badge): copy the number, or open it in P4V (`p4v -cmd "open changelist 12345"`).
- **Saved with the tab**, so a restored tab shows it again.
- **Resetting it.** When the changelist is submitted (*"Change 12345 submitted."*) or deleted, the badge changes to "submitted" or goes away.

### Agent map

A live view of what a tab's subagents are doing. When Claude fans work out to several subagents, possibly nested, the conversation shows each one as a collapsed group ([§5](#5-conversation-view)). That's fine for one agent at a time, but hard to follow when several run in parallel.

- **What it shows.** A map of the tab's agents: the main agent at the root, and each subagent as a node under the agent that started it, with nesting kept.
- **Each node** shows:
  - The agent type (for example `Explore` or `general-purpose`) and its task description.
  - Its status: running, waiting on a permission prompt, done or failed.
  - What it's doing right now: its latest tool call (for example `Grep "auth" in src/`) or a line of its text.
  - Running time, tool calls so far and tokens used, where Claude Code reports them.
- **The prompt each subagent was given.** Selecting a node shows the full instructions the parent agent sent it: the `prompt` input of its `Agent` tool call, rendered as Markdown, with **Copy**. It also shows what the subagent returned, the tool result its parent received, once it finishes. This is often the quickest way to see why a subagent did what it did.
- **Look and feel.** Follow how Claude Code's VS Code extension presents subagents ([§3](#visual-style)), and check it again when this feature is designed: compact rows, the task description up front, and the prompt and activity one click away rather than always expanded.
- **Where it lives.** A page of the side panel next to Changed files and Processes, and optionally a larger view of its own. The tab's info card gets a row such as "3 agents running".
- **Interaction.**
  - Clicking a node scrolls the conversation to that subagent's group and expands it.
  - A node waiting on a permission prompt is highlighted, and clicking it goes to the prompt.
  - Stopping one subagent, if Claude Code offers a way to (for example `stop_task` for background agents), with the whole-turn Stop as the fallback.
- **Data source.** Everything needed is already in the stream:
  - Subagent traffic is tagged with `parent_tool_use_id`, which gives the tree, including nesting.
  - The `Agent` tool call's input gives the type and description, and its result marks the end.
  - `--forward-subagent-text` includes the subagents' own text and thinking.
  - `task_started` / `task_notification` and `tool_progress` messages give background tasks and progress.
  - The map is a different view of what the conversation builder already routes into subagent groups, so the two stay consistent.
- **Restored tabs.** A transcript replay shows the finished tree, with no live status.
- **Open questions:**
  - A tree list is simpler, but a graph layout reads better for wide fan-outs; which one?
  - Does it need to show agent teams (teammates), which Claude Code reports differently from subagents?

## 19. Open Questions

None right now.