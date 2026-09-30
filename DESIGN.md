# Claudette Design

Claudette is a .NET desktop client that wraps [Claude Code](https://code.claude.com/docs) in a friendly, native-feeling interface. It runs several Claude Code sessions side by side in tabs and makes it obvious how fast you are using up your plan's limits.

Claudette does not replace Claude Code. It runs the real `claude` CLI as a child process for each tab, and draws the conversation with native controls instead of a terminal.

## 1. Goals

- **Nicer than a terminal.** Show messages, tool calls, diffs and permission prompts as real UI, using each platform's native look and window behavior.
- **Many sessions at once.** Each tab is an independent Claude Code session with its own working folder.
- **Token burn awareness.** Always show how much of the current session, weekly and model-specific limits you've used, how fast you're using them, and when you'll run out at the current rate.
- **Easy to stop.** It should always be one click (or one key) to stop what Claude is doing in the current tab.
- **Pick up where you left off.** Tabs come back when Claudette restarts, and a session library in a synced folder lets another machine continue the sessions you choose to sync.

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
| Look & feel | Fluent theme on Windows, macOS-style theme on macOS | Follows the OS light/dark setting and accent color, or the Claude apps' look ([Visual style](#visual-style)). Mica backdrop on Windows 11 (not in the Claude style, which is solid); native title bar, traffic lights and menu bar on macOS. Mica is used only once Windows grants it: the title bar, header and sidebar show it, and the page keeps an opaque background. The macOS-style theme isn't built yet; macOS uses the Fluent theme for now. |
| Pattern | MVVM with CommunityToolkit.Mvvm | |
| Markdown | LiveMarkdown.Avalonia | For assistant messages. Built for streaming: text is appended as it arrives instead of re-rendering the whole message. Includes syntax-highlighted code blocks. (Markdown.Avalonia only had an alpha for Avalonia 12.) |
| Diffs | Claudette's own line diff and diff view, highlighted with TextMateSharp | The TextMate grammars and themes LiveMarkdown already ships for code blocks. AvaloniaEdit was the plan, but a read-only diff doesn't need an editor. |
| Usage history | SQLite (Microsoft.Data.Sqlite) | [§6](#usage-history) |
| Dependency | Claude Code CLI | Must already be installed. Claudette finds `claude` on `PATH` (on macOS and Linux, the login shell's `PATH`, [§13](#login-shell-environment)) or at a path set in Settings, checks its version on launch against a minimum supported version, and shows a setup screen if it is missing or too old. Sign-in is handled inside Claudette (see [§11](#11-sign-in)). |
| Service status | Claude's public status page, status.claude.com | Read for the header's status dot and the incident banner: its Statuspage summary, with no sign-in. Settings → General turns it off. [§18](#service-status). |
| Packaging | Windows: MSIX. macOS: signed, notarized `.app` in a `.dmg`. | [Below](#packaging-and-signing). |

### Packaging and signing

The files are in `packaging/`, and `.github/workflows/package.yml` builds them.

- **Windows: MSIX**, one per architecture (x64, arm64), self-contained.
  - `packaging/windows/build-msix.ps1` publishes the app, adds `Package.appxmanifest` and the tile images, builds `resources.pri` for the scaled taskbar icons, packs with `makeappx`, and signs with `signtool`.
  - The identity is `reapazor.Claudette`, the same as the AppUserModelID an unpackaged Claudette uses. The manifest's `Publisher` must match the signing certificate's subject; the script takes it as `-Publisher` or `MSIX_PUBLISHER`.
  - A startup task (`windows.startupTask`, `ClaudetteAtLogin`, off until Settings turns it on) starts Claudette at login ([§9](#starting-at-login)).
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
- **The icon** is Claudette: Clawd, Claude Code's pixel mascot, with hair on top, a berry hair tie and a ponytail. `packaging/icon/build-icons.mjs` draws every file from one 12×12 sprite, with a whole number of pixels per cell so the small sizes stay crisp. To change the icon, change the sprite and run the script (`node packaging/icon/build-icons.mjs`).
  - **Windows:** `src/Claudette.App/Assets/claudette.ico` and `packaging/windows/Assets/`, with no background. Its taskbar sizes include 24, 36 and 48 px (100%, 150% and 200% scaling), where the sprite fills the icon.
  - **macOS:** `packaging/icon/claudette-1024.png`, on an ivory tile.
  - **SVG:** `packaging/icon/claudette.svg`. The script also writes it to `.idea/.idea.Claudette/.idea/icon.svg`, where Rider looks for a project icon.
  - **Animation frames** ([§10](#10-notifications)): `src/Claudette.App/Assets/AppIcon/`, embedded in the app. The taskbar overlay's spark, and Claudette typing and waving on the Dock icon, drawn at half the macOS icon's size so the Dock icon doesn't shift when an animation starts.

> **Not yet tested on a real machine:** installing and running the MSIX and the `.dmg`, and signing and notarization, which need the certificates. The pull request build checks that both packages build.

### Updating Claudette

An installed Claudette checks its GitHub releases for a newer version, downloads it, installs it and restarts into it, with every tab as it was: the same handover a source build uses for a new build ([§9](#working-on-claudette)).

- **Checking.**
  - At launch and every 6 hours, Claudette asks the GitHub REST API for `reapazor/Claudette`'s releases. It doesn't sign in, and the check sends nothing but the request.
  - It offers the newest release above the running version. Tags are versions such as `v1.3.0` or `v1.4.0-beta.1`, ordered as Semantic Versioning orders them.
  - Pre-releases count only with **Include pre-releases**. Drafts aren't visible without signing in to GitHub, so a release is offered once it's published: the package workflow's draft release becomes an update when it's published.
  - **Check for Claudette updates automatically** turns this off. Both settings are in Settings → General, which also shows the version, when it last checked and **Check now**. The version is also at the foot of the Settings sidebar ([§14](#version)).
  - A source build never checks. Its new builds come from its checkout.
- **Which package.** The release asset named for this install, as `package.yml` names them:
  - `Claudette-<version>-<x64|arm64>.msix` for an MSIX install.
  - `Claudette-<version>-<arm64|x64>.dmg` for `Claudette.app`.
  - Anything else, such as a Linux build or an unpackaged Windows one, can't replace itself. The release is still announced, with a link to its page.
- **Offering it.** A badge at the foot of the sidebar, like Claude Code's ([§12](#applying-it)): *"Claudette 1.3.0 is available"*. Its dialog shows:
  - the release notes and a link to the release page;
  - **Download and restart**, which becomes **Restart to update** once the package is downloaded;
  - **Update when idle**, while a tab is working, which waits until no tab is starting, working or waiting on the user;
  - **Download**, to fetch it now and install later;
  - **Skip this version**, which hides the badge until a newer release (saved with this machine's state).

  Settings → General has the same actions.
- **Downloading.**
  - The package goes to `updates/<version>/` in the data folder, under a temporary name.
  - It's kept only once its size and the SHA-256 digest GitHub publishes for it match. A package that doesn't match is deleted.
  - A package already downloaded isn't fetched again. Downloads of the running version and older ones are deleted at launch.
- **Checking the package** before anything closes. If the package fails a check, nothing else happens.
  - **MSIX:** its manifest must be `reapazor.Claudette`, from the same publisher as the installed package (compared as the package family name, so a package signed by someone else would install beside this one instead of replacing it). It must also be the release's version and this machine's architecture. Windows checks the signature when it installs.
  - **macOS:** the image is mounted, and its `Claudette.app` copied next to the running one, as `.Claudette-update.app` in the same folder. The copy must have:
    - a valid signature (`codesign --verify --deep --strict`), from the same team as the running app (when the running app is signed);
    - the bundle identifier `com.reapazor.claudette`;
    - the release's version.
- **The handover**, as for a new build ([§9](#working-on-claudette)):
  1. Claudette writes the restart snapshot (every tab, draft, the selected tab and the window's placement) with the versions it's updating from and to.
  2. It saves its state and stops writing it, stops every tab as closing does, and stops taking later launches.
  3. **Windows:** it calls `RegisterApplicationRestart` with `--restore <nonce>`, then `PackageManager.AddPackageAsync` with `ForceApplicationShutdown`, as Microsoft documents for apps published outside the Store. Windows closes Claudette, installs the update and starts the new version with those arguments. If the install finishes without closing Claudette, Claudette starts the new version itself (`IApplicationActivationManager`) and waits up to 60 seconds for it to say it's up.
  4. **macOS:** Claudette starts a small helper script and quits. Once Claudette has quit, the helper:
     - renames the running app to `Claudette.app.previous` and the new one to `Claudette.app`;
     - starts it with `--restore <nonce>`;
     - waits up to 60 seconds for it to write the nonce to `restart-ready`.
  5. The new version opens the snapshot's tabs, writes the nonce once its first page has drawn, and only then deletes the snapshot.
- **If it goes wrong.**
  - **Before Claudette closes** (the check fails, Windows refuses the package, the helper can't start): the tabs come straight back and the dialog says why.
    - If the package is fine but couldn't be installed this way, **Open the installer** (App Installer on Windows) or **Open the disk image** installs it by hand.
    - macOS runs an app opened from Downloads from a temporary, read-only copy (app translocation), and that copy can't be updated. The dialog asks to move Claudette to Applications, or to install by hand.
  - **The new version doesn't start** (macOS): the helper stops it, puts the old app back and starts that with the same `--restore`. The old app takes the tabs back from the snapshot, which is still there, and says the update didn't finish.
  - **Windows doesn't start the new version:** the snapshot waits a day. The next launch within that time takes the tabs from it, even without `--restore`, and says whether the update installed.
- **MSIX versions** have four parts and no pre-release label (`1.4.0-beta.1` packages as `1.4.0.0`), so a pre-release and its final release have the same package version. Windows won't reinstall the same version, so give the final release a higher version than its pre-releases' packages.

> **Not yet tested on a real machine:** installing an update on Windows and macOS, which needs signed packages from a published release. The steps before and after are covered by tests: finding and downloading the release against a fake GitHub, checking the packages, the handover and taking the tabs back. On Linux the macOS helper script is run for real, both swapping the apps and going back.

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

1. **Usage header**, across the top. Always visible. Session usage is the most prominent item; weekly limits are smaller. A chevron at its right draws it taller, with charts ([Detailed header](#detailed-header)). See [§6](#6-token-burn-awareness). At its right, before the account name, the CPU and memory of every tab's processes while the process monitor is on ([§4](#process-monitor)), then a dot that shows Claude's service status; while Claude has an incident a banner runs across the top under it ([§18](#service-status)).
2. **Sidebar**, on the left. One row per tab (one tab per session), with a status icon, grouped by working folder, and under a tab's row, the runs of its project actions ([§18](#project-tools)). **New tab** is at its top; the selected tab's project, with its actions and links ([§18](#project-tools)), **History**, the Claude Code and Claudette update badges and **Settings** are at its foot. It collapses to a rail of status icons. See [§4](#sidebar).
3. **Conversation.** The selected tab's conversation. See [§5](#5-conversation-view).
4. **Side panel (collapsible).** Files changed in this tab ([§8](#8-file-changes--diff-view)), its agent map ([§18](#agent-map)), its project's tools and their output when it has any ([§18](#project-tools)), and optionally its running processes ([§4](#process-monitor)).
   - Its pages are tabs along its top, over a divider, as in VS Code's panel: the page showing is in full text with an accent line under it, the others muted. A page's tab shows a busy dot while it has something running.
   - **Resizing.** Drag its left edge to make it wider or narrower (260 to 900 pixels; 340 by default), leaving the conversation at least 360. Double-click the edge for the default width. The width is the same for every tab, and remembered.
5. **Composer.** Where you type to the selected tab, plus the Stop button and per-tab controls.

### Visual style

Settings → Appearance → **Style** picks one of two looks, in light and dark alike. It changes at once, without a restart, and syncs with the other Appearance settings.

**Standard** (the default) takes Claude Code's own Visual Studio Code extension as its reference:

- A dense, calm layout that follows the OS light or dark theme, with neutral greys and the OS's accent color for selection, meters and checked boxes.
- Tool calls are compact one-line rows with a small status dot (running, done, failed), expandable for detail, not heavy cards. A row's summary is cut to fit; hovering it shows it in full (a whole command, with its line breaks, or a file's whole path).
- Diffs are inline, in red and green.
- Thinking is a collapsed row.
- User prompts sit in a subtle bordered box rather than a chat bubble.
- The composer is a rounded box with the mode and model controls beside it, and a square Stop button.
- Inline code and code blocks use VS Code's Light+ and Dark+ colors.

**Claude** takes the Claude apps (the iOS app and claude.ai) as its reference. The layout, tool rows, diffs and every control stay the same; the look changes:

- **Colors**, from the Claude apps' scale:
  - Light: an ivory page (`#FAF9F5`), a warmer sidebar (`#F5F4ED`), white cards and composer, near-black text (`#141413`) and warm grey muted text (`#73726C`).
  - Dark: a charcoal page (`#262624`), a darker sidebar (`#1F1E1D`), lighter cards and composer (`#30302E`), off-white text (`#FAF9F5`) and warm grey muted text.
  - The accent is Claude's orange (`#D97757`), with a darker orange for accent text on light and a lighter one on dark, so it stays readable. The working line's glyph and verb are orange, like the Claude apps' spark.
  - Cautions (a waiting prompt, a usage alert, "Needs your input") are warm tints of the accent rather than yellow. Errors, diffs, the charts' model lines and a tab group's own color keep their usual colors.
- **Your messages are bubbles** on the right (rounded, filled, no border), and Claude's replies run full width beside them.
- **Claude's replies are set in a serif**, as the Claude apps set them. Their typeface isn't available, so it's the closest installed one: Charter on macOS, Georgia or Cambria on Windows, and Charter, Noto Serif or DejaVu Serif on Linux. A conversation font set in Settings wins.
- **The composer** is a big rounded box lifted a little off the page, and Send and Stop are round buttons; Send is an arrow.
- **Rounder corners** on code blocks, prompts and the sidebar's rows.
- **No Mica**, so the sidebar and header stay warm rather than showing the desktop through.

How it's built: `Themes/ClaudeColors.axaml` holds the Claude values of Claudette's own tokens, and `Themes/AppColors` swaps them in and gives Fluent a matching palette (the window background, text, controls, and the accent it derives its shades from). Fluent reads most palette colors only when its resources are first used, so switching loads a fresh Fluent theme with the palette already set. The shapes are styles under the `claude` class, which the main view takes, as Density's are under `compact`. The replies' font is the `ReplyFont` resource.

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
  - Error or process exited. The tab's row and info card say what went wrong: the last line Claude Code wrote to its error output, or why it couldn't start.
- **Model and effort** are easy to see for every tab. The second line of the tab's row shows them (for example `Opus · High`), and the composer bar shows the same thing in full for the selected tab. See [Model & effort](#model--effort).
- **Tab info card.** Hovering a tab shows a card with the tab's details. It's the one place features add per-tab information, rather than putting it in the tab name. It shows:
  - The full folder path and git branch.
  - Model and effort.
  - When the session started (for a resumed session, its transcript's first entry; saved with the tab), tokens used and context %.
  - Later features add rows here: the Perforce login and changelist ([§18](#perforce-ticket-handling)), the **Project** the tab's folder holds and whether Claude was told about it ([§18](#project-tools)), and the **Claude app** connection: connected at the session's address, connecting, or why not ([§18](#remote-control-the-claude-app)), for example.
  - **Agents**, while the tab has subagents: how many are running or waiting on you, or how they ended ([§18](#agent-map)).
  - **Running tasks**, while Claude Code has work going in the background: one line each, such as *Shell command: Start the dev server*, up to four and then *and 2 more* ([§5](#running-tasks)).
  - **Usage limit**, while a usage limit has stopped the tab's task: when it resets, and whether the task continues then ([§6](#continuing-after-a-limit-resets)).
  - The same card opens from an **ⓘ** button in the composer bar, for the selected tab.
- **Token stats per tab.** Each tab keeps a running count of the tokens it has used:
  - Input, output, cache write and cache read tokens, split by model when the session used more than one.
  - Two time spans: **this session window** (since the current 5-hour window started, which is the part that counts against the session limit) and **all time** for this tab's session.
  - An estimated cost, labeled as an estimate (Claude Code computes it at list price; it is not your bill).
  - Shown in short form in the composer bar (for example `1.2M tok`). Click it for a popover with the full breakdown and a small per-turn chart.
  - Saved with the tab, so the counts survive app restarts and session resumes.
  - The same numbers feed the "which tab is burning the most" view in the Usage panel ([§6](#6-token-burn-awareness)).
- **Grouped by folder.** Tabs that share a working folder sit together in a group, like browser tab groups. In the sidebar a group is a label with its tabs listed under it:
  - Each group has a label with the folder name and a color. A line in the color runs down beside its tabs. If two folders have the same name, the label adds the parent folder (`work/api`, `personal/api`).
  - **The color.** A new group takes the first of eight group colors that no open group has. **Change color…** in the group's menu, in the full sidebar or the rail, opens a color picker beside the label:
    - The eight group colors as swatches, each named in its tip, with the group's own ringed. Clicking one applies it and closes the picker.
    - For any other color, a saturation and brightness square, a hue slider, and a hex field (`#12AB34`, or `#1A3`). The group changes color as you drag or type, and the hex field applies once it holds a whole color; Enter closes the picker.
    - The color is remembered for the folder on this machine (in `state.json`), so the group has it again after a restart, or when the folder is opened again after its group was closed, however its path was written.
  - Hovering the label shows the full path. The group's `+` opens a new tab in the same folder.
  - A group can be collapsed to just its label. A collapsed group still shows the most urgent status of its tabs, such as "needs input".
  - Tabs can be dragged to reorder them within their group, and groups can be dragged (by their label) to reorder them. A tab can't be dragged into another group, because its folder is fixed, and pinned tabs stay ahead of the others.
    - A dragged tab is selected, and the list rearranges as soon as the pointer passes the middle of a neighbor. **Move up** and **Move down** in the tab menu do the same from the keyboard or mouse.
  - A group with a single tab still gets a label, so the sidebar always looks the same.
- Closing a tab that is working asks for confirmation, then stops the process. Right-clicking a group label gives **New tab here**, **Collapse group** (or **Expand group**), **Change color…** and **Close group**.
- **Pinned tabs** come back every time Claudette launches, resuming their sessions.
  - Pin or unpin from the tab's right-click menu. A pinned tab shows a pin icon and sits at the start of its folder group.
  - **Close group** and **Close other tabs** skip pinned tabs.
  - Closing a pinned tab asks *"This tab is pinned. Close and unpin it?"*
  - Unpinned tabs aren't restored unless **Also restore unpinned tabs** is on in Settings. See [§9](#restore-on-launch) for what's restored.
- **Syncing.** **Sync to other machines** in the tab's right-click menu (a check item), or in its **Tab settings…**, turns copying the tab's session to the session library on or off ([§9](#session-library-sync-across-machines)). It's off for a new tab unless Settings → Sessions says otherwise. A tab that syncs shows a small sync icon in its row, and **Sync now** in its menu, which copies the session to the library straight away instead of after the next turn.
- **The Claude app.** **Connect to the Claude app** in the tab's right-click menu (a check item), or in its **Tab settings…**, connects the tab to the Claude app with Remote Control whenever its session runs ([§18](#remote-control-the-claude-app)). It's off for a new tab unless Settings → Claude Code says otherwise. A connected tab shows a small phone icon in its row, and **Open in the Claude app** in its menu.
- Keyboard: `Ctrl/Cmd+T` new tab, `Ctrl/Cmd+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle, `Ctrl/Cmd+1…9` jump to a tab, `Ctrl/Cmd+B` collapse or expand the sidebar.

### Sidebar

The tabs are listed in a sidebar on the left of the window, rather than a strip across the top, so long session names, a status line and many tabs all fit.

- **A tab's row** has two lines:
  - The status icon, a pin icon if pinned, a sync icon if it syncs to the session library (muted, with the tip *"Synced to the session library"*), a phone icon while it's connected to the Claude app (*"Connected to the Claude app"*, dimmed while it connects or reconnects, and fainter still once it's switched off and waiting to disconnect; [§18](#remote-control-the-claude-app)), a gear while a process it started is busy ([Process monitor](#process-monitor)), a small count of its running tasks once its turn is over (*"2 tasks still running"*, [§5](#running-tasks)), and the name, cut short with an ellipsis if it doesn't fit. With **Show changelist on tabs** on, a `CL 12345` badge sits at the end of the line ([§18](#perforce-changelist-in-the-tab-title)).
  - The model and effort, or instead what needs attention: *Needs your input*, the error, or *Possibly stuck* when check-ins get no reply ([§5](#check-ins-on-long-turns)). While a usage limit has stopped the tab's task and it will continue when the limit resets, *Usage limit · continues at 15:45* ([§6](#continuing-after-a-limit-resets)).
  - **Context ring.** A small ring at the end of the row, level with the second line and under the close button, fills up with the tab's context window ([§6](#per-tab-context)).
    - It's muted, amber when the context indicator warns (near auto-compact), and red from 95%.
    - Its tip is the composer bar's context text and detail, for example *"Context 75% (150,000 of 200,000 tokens · auto-compacts at 160,000)"*.
    - It's hidden until the tab has context data, so a tab that hasn't started has none. **Show context on tab rows** (Settings → Appearance, on by default) turns it off. The rail doesn't show it.
  - The close button shows on hover and on the selected tab. Hovering the row shows the tab info card; double-clicking renames it.
- **Project runs.** Each time one of the tab's project actions runs as a job, such as a build, it gets a small entry under the tab's row, indented to the tab's name, newest last ([§18](#project-tools)):
  - A glyph for how it's going: the busy dot while it runs, ✓ when it succeeded, ✕ in the error color when it failed, and a muted ■ when it was stopped.
  - Its name, and under it how long it has been running (*Running · 1m 05s*), or how it ended and when (*Failed · exit code 6 · 14:32*, in the error color). Hovering it shows its status line, when it started and how long it took.
  - Clicking it selects the tab and opens the side panel's Project page on its log. The entry whose log the page shows is picked out.
  - An entry never closes on its own, whatever the result. A finished one has a close button (×) that takes the entry and its log away. While it runs, a **Stop** button (■) is in that place instead, and ends the job as the Project page's Stop does; so a click on × never stops a build.
  - Closing the tab takes its entries away. They aren't saved, so they're gone when Claudette quits or restarts.
- **A tab's menu** (right-click, in the full sidebar and the rail): Rename, Reset name, Pin, **Sync to other machines** and **Sync now** while it syncs ([§9](#session-library-sync-across-machines)), **Connect to the Claude app** (disabled, with the reason as its tip, when the account can't use it) and **Open in the Claude app** while it's connected ([§18](#remote-control-the-claude-app)), **Tab settings…**, **Move up** and **Move down**, and Close. The project's actions and links aren't in it: they're in the project's row at the foot, which is there for every tab ([§18](#project-tools)).
- **Top:** **New tab**, which opens the picker ([Opening a tab](#opening-a-tab)), and the button that collapses the sidebar.
- **Foot:** the selected tab's project, a row that opens its menu of actions and links, whatever the folder holds ([§18](#project-tools)), **History** ([§9](#history)), the Claude Code update badge when there is one ([§12](#applying-it)), the Claudette update badge when there's a new release ([§2](#updating-claudette)), **New build ready** when a source build of Claudette has a new build ([§9](#working-on-claudette)), and **Settings** ([§14](#14-settings)). Later features add their own entries here.
- **Resizing.** Drag the sidebar's edge to make it wider or narrower (180 to 420 pixels; 248 by default). Double-click the edge for the default width. The width is remembered.
- **Collapsing.** The collapse button, or `Ctrl/Cmd+B`, shrinks the sidebar to a rail:
  - The rail shows each group's color, then a square per tab with the first letter of its name and a small status icon. Hovering a square shows the tab info card. It doesn't list project runs.
  - Right-clicking a group's color gives the group's menu, **Change color…** included.
  - A collapsed group shows only its color and its most urgent status.
  - New tab, the project, History, the update badge and Settings stay as icons.
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
- The command line: `Claudette --folder <path>` opens a tab in that folder on startup. Open Recent and the jump list use this too. `--login` is how the login entry starts Claudette: minimized, and ignored by a running Claudette ([§9](#starting-at-login)).
- On macOS, **File → Open Recent** and the Dock icon's menu list recent folders. On Windows, the taskbar jump list does the same, unless the user has turned off **Show recently opened items** in Windows' Start settings, which stops Windows from showing any app's recent items there.
- Choosing any of these opens a new tab in that folder.
- The lists hold favorites first, then recent folders, up to 10, leaving out folders that no longer exist. Folders with the same name show their parent too (`work/api`), as tab groups do.
- **One Claudette at a time.** A launch while Claudette is running (from the jump list, or by opening the app again) passes its arguments to the running one over a named pipe and exits. The running one comes to the front, and opens a tab if a folder was given. A development copy with its own `CLAUDETTE_HOME` counts as a separate instance.
- The macOS menu bar also has **File → New Tab**, **History…** and **Close Tab**, and the app menu has **Settings…**. Each shows its shortcut from Settings → Keyboard ([§14](#keyboard-shortcuts)).

### Process monitor

An optional view of the processes each tab has started, such as test runs, dev servers, builds and MCP servers, with their CPU and memory use. It's off by default and turned on in Settings → Processes. One tab can turn it on or off for itself in its **Tab settings…** ([§14](#per-tab-overrides)). Each tab shows its own processes on the Processes page of its side panel.

- **Summary.** When it's on, the composer bar shows a compact summary for the tab, for example `3 procs · 42% CPU · 1.1 GB`, except while the side panel is open. The count leaves out `claude` itself. The tab itself gets a small activity icon while any child process is using noticeable CPU (5% or more).
- **Header total.** The usage header, before the service status dot, adds up every tab's latest sample, `claude` included: `63% CPU · 2.4 GB`. Its tooltip gives each tab's summary, and how many tabs aren't counted (not started yet, or with the monitor off). It comes from the tabs' own samples, so it takes no sampling of its own and follows their pace, and it's hidden while no tab has the monitor on. Claudette's own process isn't in it.
- **Processes panel.** A page of the side panel, next to Changed files, Agents and Project. It shows a tree of the tab's processes, starting from its `claude` process, with these columns:
  - Name and PID.
  - CPU %, following the platform's convention: on Windows, 100% means all cores, as in Task Manager; on macOS, 100% means one core, as in Activity Monitor.
  - Memory (working set / resident size).
  - Running time.
  - **Command line.** Can be hidden in Settings, because command lines sometimes contain tokens or passwords. It's truncated in the table; hover for the full text, or use **Copy**.
- **Project jobs.** A project action's job, such as a build ([§18](#project-tools)), is tracked from its own process and listed with the tab's, as a top-level process beside `claude`. **Stop** on one of its processes stops it within the job's tree.
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
  - **macOS and Linux.** Claudette walks the process tree from the tab's `claude` process: `/proc` on Linux, and `ps` on macOS, which is simpler to get right than libproc. A process that detaches and gets re-parented (for example a daemonized dev server) drops out of the tree. Claudette keeps listing any process it has already seen, marked "detached", until it exits.
    - On Linux it's tested against real processes: children and their details, a process whose parent exited, **Stop** with `SIGTERM` and then `SIGKILL` for one that ignores it, CPU use, and ending a tree that keeps starting processes.
    - The macOS code compiles and its `ps` parsing is tested, but it hasn't run on a Mac yet.
- **Linking a process to its tool call.** A process that first appears while a Bash call is running is linked to that call. A `task_started` message ties a background task to its Bash call, so **Stop** goes through Claude Code (`stop_task`) for those.
- **Cleanup.** The same tracking lets Claudette end a tab's whole process tree when the tab closes, so no orphaned dev servers are left running.
  - If processes are still running, the close confirmation lists them. The choices are **Close and stop them** and **Close, leave running**.
  - Claudette lets `claude` exit on its own first, so it finishes its transcript, then ends whatever it left behind.
  - Quitting Claudette stops every tab's processes.

## 5. Conversation View

The conversation is drawn from Claude Code's structured output stream, not from terminal text.

| Item | How it's shown |
|---|---|
| User message | Right-aligned bubble; attached images as thumbnails. Its time and **Copy** on hover ([below](#copy-and-times)). |
| Assistant text | Markdown with syntax-highlighted code blocks, each with **Copy**. Streams in as it's generated. Its time and **Copy** on hover. |
| Thinking | Collapsed "Thinking…" row; click to expand. |
| Tool call | Compact card: tool icon, name and a one-line summary (file path, command, search pattern). Expand to see full input and output. |
| Edit / Write | Card shows `+added −removed`; expand for an inline diff, or **Open diff** to see the file in the diff view ([§8](#8-file-changes--diff-view)): from before Claude's first change in this session to the file now, as Changed files shows it. |
| Bash | Shows the command; output is collapsed and uses a monospace font. |
| Subagent (Task) | Nested, collapsible group holding that agent's text and tool calls, with its result line (its report). A background subagent's group stays running until it finishes. The agent map shows every subagent as a tree ([§18](#agent-map)). |
| To-do list | Pinned checklist at the top of the conversation while it exists. |
| Permission prompt | Inline card with buttons. See [§7](#7-permission-prompts). |
| Errors / API retries | Inline warning row. |
| Turn summary | Small footer after each turn: duration, tokens, model. |

Scrolling follows new output unless the user has scrolled up; a "Jump to latest" button appears when they have.

### Copy and times

- **Code blocks.** A code block has a header line with its language, **Wrap lines** and **Copy**.
  - Copy puts the block's code on the clipboard, without the Markdown fences and with the OS's line endings, and the button says *Copied* for 1.5 seconds.
  - The buttons are LiveMarkdown's, in Claudette's own template for the block. The tab does the copying, through the same clipboard as everything else. Code blocks in the agent map's prompts and reports work the same way.
- **Messages.** Hovering a user message or a reply shows a small chip on its top-right corner: when it was sent, and **Copy message**.
  - It also shows while the message has keyboard focus, so Tab reaches the button.
  - A reply copies as its Markdown, as Claude wrote it. A user message copies as it was sent, with its quick suffixes after a blank line.
  - The button says *Copied* for a moment, as on code blocks.
- **Times.** The chip's time is short, in the current culture: *14:05* today, *Mon 14:05* in the week before, else the date and time. Its tooltip has the full date.
  - A live message takes the time it was added: when it was sent, or when the reply started.
  - A restored message takes its transcript entry's `timestamp`. An entry without one shows no time, rather than the time it was restored.
  - Selecting a tab brings "today" up to date, for a tab left open overnight.

### Composer

- Multi-line text box. `Enter` sends, `Shift+Enter` adds a new line.
- **Stop.** A Stop button replaces Send while Claude is working, and `Esc` does the same. Stopping interrupts the current turn; it does not close the session.
- You can type and send while Claude is working; the message is queued and delivered to the session.
- `/` opens slash-command autocomplete (built-in plus the project's custom commands), and `@` file autocomplete for the tab's working folder. See [Autocomplete](#autocomplete).
- Drag and drop, paste, or pick with the attach button images and files to attach them. See [Attachments](#attachments).
- Per-tab controls in the bar above the composer: working folder (read-only), model, effort level, permission mode, the **Agents** button while the tab has subagents ([§18](#agent-map)), the **running tasks** chip while Claude Code has work going in the background (*"● 2 running tasks"*, [below](#running-tasks)), context window usage %, tokens used.
- **The bar keeps Send in view.** The choices (model, effort, permission mode) are at its left, and the counts (processes, Agents, running tasks, Files, context, tokens) with **Send** at its right. When they don't all fit on one line, as in a narrow window, the right-hand group moves to a second line under the choices, still at the right, rather than being pushed out of sight (`ControlBarPanel`).

### Working line

While Claude works, a line above the composer says so, the way Claude Code's terminal spinner does: *"✻ Noodling… 42s · 3.1k tokens · Esc to stop"*.

- **The glyph** twinkles through · ✢ ✳ ✶ ✻ ✽ and back, in the accent color.
- **The verb** is picked at random and changes every 8 seconds, never to the same one twice in a row.
- **Then:**
  - how long the turn has run;
  - its tokens so far (from each call's usage, as the tab's token count, [§6](#per-tab-context));
  - the Stop shortcut, as currently bound.
- **What Claude is doing.** While one of the main agent's tools runs, from its `tool_use` to its result, the verb gives way to what the tool is doing, then comes back:
  - *"Running dotnet test…"*, *"Reading TabView.axaml…"*, *"Editing App.cs…"*, *"Searching for TODO…"*, *"Fetching code.claude.com…"*, *"Using create_issue (github)…"*;
  - the newest call when several run at once, *"Reading b.cs and 2 more…"*, and *"Running 3 agents…"* for a fan-out;
  - a subagent's own calls show on the agent map ([§18](#agent-map)), not here;
  - hovering the line shows the running calls in full, one per line: *"Bash: dotnet test Claudette.slnx --filter …"*;
  - **Show what Claude is doing while it works** (Settings → Appearance, on by default) turns it off, leaving the verb.
- **When it shows.** From the turn's start to its end. It hides while a permission prompt, question or plan waits on the user, and the turn's time keeps running meanwhile.
- **Verbs.** Claudette has its own list, since Claude Code doesn't publish its built-in one. Claude Code's documented `spinnerVerbs` setting changes it:
  - `"append"` adds the user's verbs;
  - `"replace"` shows only theirs (an empty list keeps the built-in ones).
  - The setting is read from the tab's `.claude/settings.local.json`, then `.claude/settings.json`, then the user's `settings.json` in Claude Code's config folder (`configDirectory` from `claude auth status`). The first file that sets it wins; managed settings aren't read.
  - It's read as the tab's session starts, so a change shows from the next session.
- **Turning it off.** **Show fun words while Claude works** (Settings → Appearance, on by default). Off, the line says *"✻ Working…"* with a still glyph, and still shows the time, tokens and Stop shortcut.
- Claude Code's spinner tips (`spinnerTipsEnabled`, `spinnerTipsOverride`) aren't shown.

### Running tasks

Claude Code keeps some work going after a turn ends, and its own UIs count it (*"1 running task"*). So does Claudette.

- **What counts.** What Claude Code reports with `system/task_started` and runs in the background:
  - a shell command run with `run_in_background`, or moved there later;
  - a background subagent (a foreground one is part of the turn, and only on the Agents page, [§18](#agent-map));
  - a Monitor watch;
  - a remote agent or a workflow, and a kind Claudette doesn't know yet.
  - Not a foreground command or subagent, and not Claude Code's own work (`ambient: true`), which the Agent SDK says to leave out of activity indicators.
- **How it's read.**
  - `task_started` gives the task's id, its tool call (`tool_use_id`), `task_type`, `description` and `is_backgrounded`.
  - `local_bash` covers both commands and Monitor watches, so the tool call's name tells them apart. A Monitor always counts. Without `is_backgrounded`, the call's own `run_in_background` decides.
  - A `task_updated` patch with `is_backgrounded: true` moves a foreground task to the background, and a new `description` renames it.
  - A subagent follows its node on the agent map, so the two agree: one whose `Agent` call returns `async_launched` counts from then on, and one the map sees end has ended.
  - A task runs until a `task_updated` or `task_notification` with `completed`, `failed`, `stopped` or `killed`. Other statuses (`pending`, `running`, or ones Claude Code adds later) leave it running.
  - When the tab's `claude` exits or is restarted, its tasks go with it and the count clears. `/clear` keeps them, without their cards.
  - Claude Code also sends `background_tasks_changed` with the whole live set. Claudette doesn't use it yet: the Agent SDK says not to pair it with the per-task messages, and nothing recorded shows it from 2.1.284.
- **The chip.** In the composer bar, next to **Agents**: a pulsing dot and *"1 running task"* or *"3 running tasks"*. It's hidden at none. Clicking it lists the tasks, oldest first:
  - an icon for the kind (a prompt for a command, a pulse for a Monitor, the agents icon for a subagent or workflow, a cloud for a remote agent);
  - the description, else the command, else the subagent type, with the kind under it (*Shell command*, *Monitor*, *Subagent*, *Remote agent*, *Workflow*, *Task*);
  - how long it's been running, from the app's clock, ticking every second while the list is open;
  - **Stop**, after a confirmation, through `stop_task` as the process monitor and the agent map stop tasks. A subagent's Stop is the agent map's own. The task stays listed until Claude Code says it ended.
  - Clicking a task scrolls to the card of the call that started it and opens it, expanding the subagent groups it's in. Its tooltip has the command in full.
- **Elsewhere.** The tab's row counts them once the turn is over ([§4](#sidebar)), and the info card lists them ([§4](#4-tabs--sessions)). The process monitor's **Stop** finds a process's task through the same record ([§4](#process-monitor)).
- **Code and tests.** `Conversation/RunningTasks.cs`, kept by `ConversationBuilder` beside the agent map; `TabViewModel.Tasks.cs`. `RunningTasksTests` (the view model) and `RunningTasksUiTests` (the chip, its list, and the row's count in both styles and densities).

### Autocomplete

- **Slash commands.** Typing `/` at the start of a message lists the session's commands: built-in, user, project, plugin and MCP ones. Only the start counts, because that's the only place Claude Code runs a command.
  - Names, argument hints and descriptions come from the `commands` of the `initialize` reply. A `system/commands_changed` message replaces them when the list changes mid-session, for example when Claude Code finds skills in a subfolder.
  - Each turn's `system/init` lists the commands the session accepts (`slash_commands`). A command only it names is offered without a description.
  - Terminal-bound commands (`terminal_slash_commands`, such as `doctor`) and internal ones (a leading `__`) aren't offered.
  - Filtering ignores case. Names that start with what's typed come first (an exact name first of all), then aliases (`/reset` finds `/clear`), then names that contain it, then, from three letters on, descriptions.
  - Picking one replaces the word with `/name ` and closes the list. Claude Code runs the command when the message is sent, as in the terminal.
- **Files.** Typing `@` at the start of a word lists the working folder's files and folders.
  - In a git repository the list is `git ls-files --cached --others --exclude-standard`, so `.gitignore` is respected.
  - Elsewhere it's a breadth-first walk of the folder that skips `.git`, `.hg`, `.svn`, `node_modules`, `bin`, `obj`, `.vs`, `.idea`, `__pycache__` and `.venv`, and stops at 20,000 entries or 16 levels deep.
  - The list is cached per tab. It's refreshed when it's more than 15 seconds old and after each turn, and the old list is used while the new one loads.
  - Matching ignores case. Best first: a name that starts with what's typed, a path that does (`src/de` finds `src/deep/`), a path segment that does, a name or path that contains it, then its letters in order, preferring the starts of words (`tvm` finds `TabViewModel.cs`). Ties go to shallower, then shorter, paths. With nothing typed, the top of the folder is listed, folders first.
  - Picking a file inserts `@path/to/file ` relative to the working folder. Picking a folder inserts `@folder/` and lists what's in it. A path with spaces is quoted: `@"docs/my notes.md"`.
- **Keys.** The list opens above the composer, and the text box keeps focus.
  - `Up` and `Down` move through it, `Enter` or `Tab` picks, and clicking an entry picks it.
  - `Esc` closes it (without stopping Claude), and it stays closed while the caret is in that command or mention.
  - `Enter` still sends when the list has nothing to pick, or when what's typed is already the highlighted entry, such as `/compact` in full.
- **What Claude Code does with `@path`.** Checked against 2.1.284:
  - When the message is sent, Claude Code reads each mentioned file with its Read tool and gives Claude the content as context. A folder gets a listing, and an image file becomes an image. Absolute paths outside the folder work too, and none of this asks for permission.
  - A file that was already read and hasn't changed isn't attached again. A path that doesn't exist is left as text, and so is a binary file it can't read.
  - It only reads mentions in the message's last content block, and only when that block is text. Claudette sends the text after any images for this reason.
  - Setting `client_composed` on a message turns this off, along with running slash commands. Claudette doesn't set it.

### Attachments

- Images and files can be dropped on the composer, pasted into it, or picked with the attach button (the paper clip).
- **Images.** PNG, JPEG, GIF and WebP, recognized by their first bytes.
  - They show as thumbnails above the text box, 56 px high and at most 120 px wide, each with a `×` (**Remove image**) to remove it. A thumbnail is decoded at thumbnail size, so a large image doesn't cost its full size to show.
  - They're sent as base64 `image` content blocks in the `user` message, before the text. A message can be only images: **Send** is enabled with just an image attached. Sending clears them from the composer.
  - Each can be up to 20 MB, and a message can have up to 20. Past either limit, a note under the composer says why: *"Pasted image is 20.1 MB; images can be up to 20 MB."* or *"A message can have up to 20 images."* Sizes are rounded up, so an image just over the limit never reads as the limit itself.
  - Claude Code scales images down itself before they reach the API: 2.1.284 fits them in 2000 px and re-encodes large ones as JPEG: a 20 MB PNG reaches the API under 5 MB. So Claudette sends them as they are, and the API's own size limits don't apply to what's attached.
- **Other files and folders** become `@path` mentions, inserted at the caret. The path is relative to the working folder when the file is in it, and the full path otherwise. Claude Code reads them when the message is sent (see [Autocomplete](#autocomplete)), which covers text, PDFs and notebooks, and the message still says what was attached.
  - Binary files Claude can't read are refused with a note under the composer, rather than becoming a mention Claude Code would silently drop. A file is treated as binary when its first 8 KB contain a zero byte, except PDFs.
  - Images over 20 MB and other image formats (such as BMP) are refused the same way.
- **Pasting.** `Ctrl+V` or `Shift+Insert` (`Cmd+V` on macOS), or **Paste** on the text box's right-click menu.
  - Copied files are attached.
  - Otherwise, text on the clipboard pastes as text, even when an image comes with it, since apps often add a picture of the same text. Text that's only white space doesn't count: some apps add a line break to a copied picture.
  - An image on its own, such as a screenshot, is attached. Claudette reads it the way each OS puts one on the clipboard (Avalonia does this; checked against 12.1.3's source):
    - Windows: `PNG` or `image/png`, else a bitmap (`CF_DIB`, `CF_DIBV5` or `CF_BITMAP`), which is what Snipping Tool and `Win+Shift+S` put there.
    - macOS: `public.png`, else `public.tiff` or `public.jpeg`, converted to PNG.
    - Linux: `image/png` or `image/jpeg`.
  - A pasted or dropped picture is sent as PNG. When the PNG would be over 20 MB, as a photo-like screenshot of a 5K screen can be, it's sent as JPEG at quality 90 instead, about a tenth of the size.
- **In the conversation.** A sent message shows its images as thumbnails above its text. Claude Code stores them in the transcript, so a restored tab shows them too.
- **Drafts.** Attached images stay with the message until it's sent.
  - A message sent while Claude Code needs a sign-in waits with its images, and they go with it after the sign-in ([§11](#signing-in)).
  - Restarting into a new build keeps them with the tab's draft, in `restart.json`, and so does taking the tabs back when the new build doesn't start ([§9](#working-on-claudette)).
  - They aren't saved anywhere else: closing Claudette with an unsent message loses them, as it loses the text.

### Quick suffixes

Saved snippets of instructions that can be added to a message in one click, such as *"Ask clarifying questions before you start."*

- **Picking one.** A **Suffixes ▾** button next to the text box opens a dropdown of saved suffixes. `Ctrl/Cmd+Shift+S` opens it from the keyboard, and the first nine entries can be picked with `1`–`9`.
- **Chips.** A picked suffix appears as a chip under the text box instead of being pasted into the text, so the message stays easy to edit. Several can be picked at once. Click a chip's `×` to remove it.
- **Check marks.** The dropdown puts a check mark beside each suffix already on the message, kept or not. Picking a checked one (by click or its number) takes it off, like its chip's `×`.
- **Sending.** When the message is sent, the suffixes are appended in the order shown, separated from the message by a blank line. The sent message in the conversation shows the full text, with the suffix part in a lighter style. Sending with only suffixes and no typed text is allowed.
- **After a slash command.** Claude Code takes everything after a command's name as its arguments, so a message that starts with `/` gets its suffixes in a text block of their own before the command instead. A command that runs a prompt (a skill or a custom command) gets them alongside that prompt, and its arguments stay what was typed. A local command such as `/compact` or `/model` doesn't query the model, so its suffixes change nothing (checked against 2.1.284).
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
  - They sit side by side. When the header is too narrow for that, they stack one above the other, with their bars lined up.

### Burn trendline

- A sparkline next to the session meter showing usage % over the current window.
- A dotted **projection** line continues the current rate forward.
- A plain-language line under the meter:
  - "At this rate you'll hit the limit in 1h 05m, 1h 09m before it resets" (amber/red), or
  - "On track: about 70% used when the session resets" (neutral).
- **Rate** is measured over a recent window (by default the last 30 minutes, using a moving average), so a single big turn doesn't swing the projection wildly. When nothing is running, the projection says so instead of showing a stale rate.
- Clicking the header opens a **Usage** panel (its own window) with:
  - A larger chart of the current session and the past week.
  - Tokens per tab for the current window, so you can see which session is burning the most. A tab closed since then keeps the last name it had, from the [usage history](#usage-history), after a restart too.
  - Past sessions and weeks, as far back as the stored [usage history](#usage-history) goes. Each past window shows the highest usage it reached. The lists show the latest 30 sessions and 12 weeks, with **Show more** for the next page.
- Accounts without plan limits (an API key, for example) get no meters; the header just says Claudette.

### Detailed header

The header can be drawn taller, with charts, for keeping an eye on usage without opening the Usage panel.

- **Turning it on.** A chevron at the header's right expands it, and collapses it again. Settings → Appearance → **Detailed usage header** is the same switch.
  - The choice is remembered on this machine and doesn't sync, like the sidebar's collapsed state.
  - Collapsed, the header is exactly as described above.
- **Layout.** The one-line header stays on top. Under it, about 150 px high: the session chart, the weekly chart, the numbers and the busiest tabs, left to right.
- **Session chart.** Usage over the current 5-hour window, from its start to its reset.
  - Faint bands above the warning and critical thresholds (Settings → Usage), with a dashed line at each.
  - The dotted projection from now, a "now" marker, and times at the start, now and the reset.
  - When the projection crosses the critical threshold before the reset, that point is marked and the chart says when: "Hits 90% at 14:05, 1h 16m before it resets." Past the threshold, it marks the limit instead: "Hits the limit at …".
- **Weekly chart.** Usage over the current 7-day window, with a tick at each midnight and the days' names, and the projection to the week's reset.
  - The weekly projection uses the week's average pace so far, not the burn rate window: a week has nights and days off in it.
  - Model-specific weekly limits are thinner lines, with a legend, when the model meters are on (Settings → Usage). Two get a line at most: more hues than that don't stay distinct beside the accent color.
- **Numbers.**
  - **Burn rate:** % per hour over the burn rate window, or *Idle*.
  - **Time to limit** at that rate, and whether that's before the reset or the window resets first.
  - **Busiest tabs:** the top three tabs by tokens this window, as "name 41%" with a small bar. It's the same count as the Usage panel's list of tabs, and a closed tab keeps its last name there too.
- Clicking the charts opens the Usage panel, as the header does.
- **Narrow windows** leave out the busiest tabs first (below 960 px), then the weekly chart (below 700 px). The session chart takes the room.
- Accounts without plan limits get no charts, as they get no meters.
- **Data.**
  - The session chart uses the same readings as the sparkline.
  - The week and the busiest tabs are read from the [usage history](#usage-history), off the UI thread and only while the charts show: when they open, when a new sample may have been stored (at most once a minute) or a window reset, and after each turn.
  - The charts draw the last value in each minute (session) or each 15 minutes (week), not every sample.
- **Drawing.** Claudette draws the charts itself, with no charting package. They use the app's color tokens, so they follow the light and dark theme; the model lines' two colors were checked against the accent and each other, including for color blindness.

### Per-tab context

Separate from plan limits, each tab shows how full its **context window** is (in the composer bar). It warns near the auto-compact threshold and has a quick **Compact** action.

- Clicking the context indicator shows the detail and **Compact now**, which sends `/compact` to the session.
- A `compact_boundary` message adds a "Conversation compacted" note, or says Claude Code compacted it by itself.
- **On every tab's row.** A small ring in the sidebar shows the same percentage for each tab, so a tab nearing its limit stands out without selecting it ([§4](#sidebar)). It's muted, amber when the indicator warns, and red from 95%. It comes from the same `get_context_usage` reply, or the same estimate when that isn't available.

### Alerts

An OS notification (optional) when:

- Session usage crosses the warning thresholds.
- The projection says you'll hit the limit before it resets.
- A limit resets.

Each alert fires once per window. The first reading after a restart doesn't alert for levels that were already crossed. The alert also shows as a dismissible line under the header, and the OS notification is skipped while Claudette is in front ([§10](#10-notifications)). A threshold alert's line keeps up with the session meter (the percentage, rounded the same way, and the countdown), so the two never disagree.

### Continuing after a limit resets

When a plan usage limit stops Claude mid-task, the tab waits for the limit to reset and then continues the task, so work left running overnight or over lunch picks up again in the new session window. It's on by default: Settings → Usage → **Continue tasks when a usage limit resets** turns it off everywhere, and each tab can turn it on or off for itself in **Tab settings…** ([§14](#per-tab-overrides)).

Claude Code does the same in its own terminal (`autoContinueAtUsageLimit`, since 2.1.234), but not in `-p` runs such as Claudette's, so Claudette does it. It follows Claude Code's rules where they apply.

- **Noticing.** A turn stopped at the limit ends with an error (`result` with `is_error`, and `api_error_status` 429 or none), and Claude Code sends a `rate_limit_event` with `status: rejected` and `resetsAt`, in either order. Its `rateLimitType` names the limit (session, weekly, Opus or Sonnet), as Claude Code's own message does (*"You've hit your session limit · resets 3:45pm"*, which still shows as the turn's error). Not a limit that waiting fixes:
  - A rejection with an `errorCode`, such as `credits_required`: the account needs usage credits.
  - A turn that ended any other way: stopped by the user, out of turns or budget, or a server error.
  - A rejection the turn got past, for example on extra usage.
- **Continuing.** A minute after the reset, the tab sends *"Continue from where you left off."*, the prompt Claude Code uses. It appears as a user message labeled *"Automatic continue after the usage limit reset"*, as a check-in is labeled ([§5](#check-ins-on-long-turns)). It doesn't resend the user's last message. The continued turn is like any other: it still asks for permissions, so it can stop on a prompt while the user is away.
- **While it waits.**
  - A bar over the composer says so: *"You've hit your session limit. The task continues by itself when it resets, at 15:45."*, with **Don't continue**.
  - The tab's row shows *Usage limit · continues at 15:45* in place of the model ([§4](#sidebar)), and its info card has a **Usage limit** row with the bar's text.
  - A day other than today reads *"on Mon at 09:00"*.
- **When it doesn't continue by itself** the bar says why, with **Continue when it resets** (or **Continue**, once it has) and a close button:
  - Turned off, in Settings or for the tab: *"You've hit your session limit. It resets at 15:45."*
  - **Don't continue**, for that reset. Hitting the limit again before it resets doesn't start another wait; the next window starts fresh.
  - The reset is more than a day away, as a weekly limit's can be.
  - Three continues in a row each ran straight into the limit again. Like Claude Code, it waits again at most twice, then stops (Claude Code says *"Automatic continue stopped after repeated usage-limit hits"*). A continue that gets going, or a message from the user, starts the count again.
  - The limit reset more than 30 minutes before Claudette noticed, because the computer slept through it or Claudette wasn't running: *"Your session limit reset at 15:45 while this computer was asleep or Claudette was closed, so the task didn't continue by itself."* Claude Code also waits for the user after a long sleep.
- **Ending the wait.** Any turn that starts ends it: the user sent a message, or something else started one. Turning the setting off or on while a tab waits changes that wait straight away.
- **Saved with the tab**, so a restart, including one into a new build ([§9](#working-on-claudette)), keeps waiting. A restored tab that hasn't started is started to continue.

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
| Tokens per turn and per tab | `usage` and `modelUsage` on each `result` message. Per-call `usage` on `assistant` messages moves the composer's count during a turn: Claude Code repeats a call's usage on each of its content blocks, so calls are counted once by message ID, with their latest figures. `modelUsage` also includes `costUSD` and `contextWindow`. |
| Context window % | The `get_context_usage` control request after each turn. Without it (it fails, or this Claude Code lacks it), an estimate: the main agent's latest call (everything it read plus what it wrote) ÷ that model's `contextWindow` from the last `result`, updated with each call. The estimate warns at 90% of the auto-compact threshold from `autocompact_state` (undocumented; sent before each turn with `enabled`, `effective_window` and `threshold`), or at 80% without it. |

**Sampling.**

- All tabs share one account, so plan usage is tracked app-wide, not per tab.
- Claudette calls `get_usage` on launch, after each turn (at most once a minute), and every 5 minutes otherwise. It uses a hidden **utility session** for this ([§13](#integration-with-claude-code)), so the header stays current even when no tab is working.
- `rate_limit_event` messages from any tab update the header immediately between polls. They carry only the session and weekly windows, so the model-specific readings from the last poll are kept.
- If `get_usage` fails or changes shape, Claudette falls back to `rate_limit_event`, and hides the model-specific meters unless the `/usage` fallback is turned on in Settings. It keeps trying `get_usage` on the normal schedule.
- The `/usage` fallback sends `/usage` to the utility session as a message and reads the text it prints.
- Reset times from the two sources differ by fractions of a second (`02:19:59.95` against `02:20:00`), so they're rounded to the second before being compared.
- **Stale readings.** When Anthropic's usage endpoint fails, `get_usage` answers from Claude Code's cached reading (`cachedUsageUtilization` in `~/.claude.json`, kept for an hour) and doesn't say so. Seen on 2.1.284: polls kept reporting 85% for 20 minutes while `rate_limit_event`s climbed to 90%, so the meter fell back after the 90% alert. Within one window a limit's usage only rises, so every reading, from any source, merges with the current one: a lower reading for the same window, or one for an earlier window, is stale, and the meter keeps the current value. Once the current value hasn't been reported for an hour (no cached reading is older), a lower one is believed, since the limit really went down, as after a plan change. Readings without a reset time replace the current one.
- After a restart, the header shows the last stored sample (with "as of" its time) until the first poll, and the first readings merge with it the same way.

**Extra data.** `get_usage` also reports what's contributing to usage, such as the share of requests at long context and the top skills and subagents over the last day and week. That could become a panel later; it's not in v1.

### Usage history

Claudette stores usage data locally in a SQLite file in the app data folder, so the trendline, charts and per-tab stats survive restarts. The file is per machine and isn't synced. It holds three kinds of records.

**Plan usage samples** (app-wide):

- Timestamp.
- Session (5-hour) % used and its reset time.
- Weekly (7-day) % used and its reset time.
- Model-specific weekly % (for example Fable), if that meter is on.

A sample is saved only when a value changes, and at most once a minute. These feed the trendline and projection, the weekly charts (the Usage panel's and the [detailed header](#detailed-header)'s), and the header after a restart.

**Per-turn token records** (per tab):

- Timestamp.
- Tab and session ID.
- Model.
- Input, output, cache write and cache read tokens.
- Estimated cost.

These feed each tab's per-turn chart, the "which tab is burning the most" view, and the detailed header's busiest tabs.

**Tab names** (per tab): each tab's last known name, so its rows are still named once it's closed. It's kept when a turn is recorded and whenever the name changes after that, and only for a tab with turn records. Turns and names are written in the order they happen, so a rename just after a turn isn't lost. A name goes when its tab's last turn record does (retention or **Clear usage history**). Turns recorded before names were kept show as "A closed tab".

**Not stored:** prompts, replies, code or any other conversation content. That stays in Claude Code's transcripts and the session library. A tab's name is kept, although it's often the title Claude Code gave the conversation ([§4](#4-tabs--sessions)): it's what the sidebar, History and the session library show, and it never leaves this machine.

**Retention.** Settings → Usage → **Keep usage history** with these options:

- 1 day
- 1 week
- 1 month (the default)
- 1 year
- Forever

Records older than the chosen period are deleted at launch and once a day. With **1 day**, the weekly chart only covers the last day. The header meters and projection are unaffected, since they only need the current 5-hour window and the latest weekly value.

**Clear usage history.** A button beside the retention option. After confirming, it deletes every stored sample, per-turn record and tab name.

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
- A prompt from inside a subagent names it (*"Asked by the Explore subagent: Find the auth code"*), and the agent map highlights that subagent ([§18](#agent-map)).
- A prompt answered in the Claude app, on a tab connected with Remote Control, is withdrawn by Claude Code; its card closes and reads *"Answered in the Claude app"* ([§18](#remote-control-the-claude-app)).
- A tab with a waiting prompt gets the "Needs input" status. If Claudette isn't focused or the tab isn't selected, it also sends an OS notification ([§10](#10-notifications)).
- Keyboard: `Ctrl/Cmd+Enter` allows, `Ctrl/Cmd+Backspace` denies the oldest waiting prompt in the tab.
  - Not while typing in one of the prompt's own fields, and `Ctrl/Cmd+Backspace` still deletes a word in a field with text.
  - A request Claude Code marks `defaultToNo` can't be allowed from the keyboard.
- **Permission mode picker** per tab: Manual, Accept edits, Plan, Auto, Bypass permissions. Manual is Claude Code's `default` mode, named as its terminal and VS Code extension name it now.
  - Picking a mode changes it for this session only. The mode a tab starts in comes from Tab settings or the New tabs default, and without either, from Claude Code ([Starting mode](#starting-mode) below).
  - Auto is offered only while the tab's model supports it (`supportsAutoMode` on the model in the `initialize` reply; Claude Code leaves it out for Haiku) and no settings file Claudette reads sets `disableAutoMode`. Claude Code refuses the switch otherwise ("auto mode unavailable for this model").
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

### Starting mode

A tab starts in auto mode, like a Claude Code session in a terminal or VS Code, unless something chooses otherwise. Since 2.1.283 auto mode is Claude Code's built-in starting mode there, but not for `claude -p` or the Agent SDK, which start in Manual, and a tab is a `claude -p` session. So Claudette asks for auto mode itself (`StartingPermissionMode`):

- **Chosen in Claudette:** a mode in Tab settings, else the New tabs default, is passed with `--permission-mode`, as before.
- **Left to Claude Code** (New tabs → **Claude Code's default**, the default): Claudette reads the settings files Claude Code would, in its precedence order: `managed-settings.json` and its `managed-settings.d` drop-ins, the project's `.claude/settings.local.json` and `.claude/settings.json`, then `settings.json` in Claude Code's config folder.
  - No file sets `permissions.defaultMode`: the tab is launched with `--permission-mode auto`.
  - A file sets it: no flag, and Claude Code applies it, as it does for `-p`. As in a terminal, a project's files can't set `auto` (the built-in default applies instead, so the tab gets auto) or `bypassPermissions` (Manual).
  - A file sets `disableAutoMode` to `"disable"`: no flag, and Auto isn't in the picker. Claude Code would start in Manual anyway.
  - When auto mode turns out to be unavailable (the model doesn't support it, or Anthropic has turned it off), Claude Code starts the session in Manual by itself.
- **A resumed tab** (restored, restarted into a new build, or after a sign-in) isn't given the flag. Without `--permission-mode`, `claude -p --resume` brings back plan mode for a session that ended in it. Once it has started, a tab that came back in Manual is switched to auto with `set_permission_mode`, quietly: if Claude Code refuses, it stays in Manual. The mode picked in the last run isn't restored otherwise, as before.
- **Tab settings → Default** switches back to the starting mode, auto included. Its label names that mode, "Default (Auto)", once the tab has started, and Settings → New tabs names the mode the user's and managed settings give: "Claude Code's default (Auto)".
- Not read: managed settings from MDM, the Windows registry or the claude.ai console, and `--settings` in Settings → Advanced's extra arguments. An organization that sets a starting mode only there still gets auto mode in Claudette's tabs, but `disableAutoMode` from any of them is enforced by Claude Code.

## 8. File Changes / Diff View

- A collapsible side panel lists the files changed in the selected tab's session: added, modified or deleted, with `+/−` line counts. It is built from the session's Edit/Write tool calls, live and when a transcript is replayed.
  - The **Files (n)** button in the composer bar opens it.
  - The counts compare Claude's "before" with the file on disk now, so later edits by you show up too.
  - A file that's back to how it was shows as unchanged.
- Selecting a file opens a diff view (side-by-side or inline) with syntax highlighting.
  - The view is a window of its own, so it can stay open beside the conversation.
  - It shows the changes with a few lines of context, or the **Whole file**.
  - Long lines scroll sideways with a scroll bar along the bottom, Shift and the mouse wheel, or a touchpad. Every line scrolls together, both sides at once when they're side by side, and the line numbers stay where they are. The rows scroll up and down as usual.
  - Highlighting uses TextMate grammars, by file extension, with the dark or light theme to match the app. Files over 20,000 lines, and binary files, are shown without it.
  - A leading byte order mark isn't counted as a change.
- Actions: open in external diff tool, open in external editor (the app the OS uses for that file type), reveal in Finder/Explorer, copy path.
- If the folder is a git repo, a toggle switches to **working tree vs HEAD**. This also shows changes made by Bash commands or by the user.
  - It covers the whole repository, not only the tab's folder.
  - Untracked files count as added.
  - Git runs with `--no-optional-locks`, so refreshing never takes git's index lock.
- **Reviewed.** Each file in the panel has a box to tick once you've reviewed it, in both views. A file has one reviewed state, so ticking it in one view ticks it in the other.
  - A reviewed file is drawn faintly, and the panel's summary counts them: *"5 files changed · 2 reviewed"*.
  - It stays reviewed until Claude changes the file again with Edit, Write, MultiEdit or NotebookEdit. Changes you make, or commands make, don't untick it, so a file only you changed (in working tree vs HEAD) stays reviewed until Claude changes it.
  - The built-in diff view has a **Reviewed** button, which ticks the file and closes the view. It marks the version the view showed: if Claude changed the file while the view was open, the file stays unreviewed. The button shows a check when the file is already reviewed.
  - A tick remembers Claude's latest change to the file by its tool call's id. The id is the same when a transcript is replayed, so ticks survive restoring the tab and opening the session on another machine.
  - Ticks are saved with the tab ([§9](#restore-on-launch)), and in the session record of a tab that syncs, with paths relative to the session's folder ([§9](#session-library-sync-across-machines)). **Start a new session** clears them.
- **Before content.** Claude Code reports it. The result of every Edit and Write tool call (the `tool_use_result` field on the `user` message that carries the tool result) includes:
  - `originalFile`: the file's full content before the change, or `null` for a new file.
  - `structuredPatch`: the change as diff hunks.
- The "before" side of a file's diff is the `originalFile` from Claude's first change to that file in the session, so diffs are exact even outside a git repo. The spike confirmed this in Accept edits mode too. Claudette never has to snapshot files itself, so there's no race with the tool writing the file.
- **Large files in transcripts.** Live, `originalFile` is always the whole file. The transcript (`toolUseResult`) writes it as `null` when it's over 10,000 characters, so a replay can't tell it from a new file (2.1.284; seen in the source and confirmed by `RealCliTests`). Most source files are over that size.
  - So when the first change to a file brings an `originalFile` over 10,000 characters, Claudette saves it in `before-content` in the data folder, compressed, named by the tool call's id. A restored tab, or a session opened again from History, finds it there. Saved content that hasn't been used for 30 days is deleted at launch.
  - An Edit's `null` means a new file only when its `oldString` is empty; otherwise the "before" is unknown, as it is for a Write over a file too large for Claude Code to diff.
  - A file whose "before" is unknown, such as one from a session Claudette didn't run live, is listed as modified, without counts. The diff view shows it as it is now, with nothing marked as changed, and says what it held before isn't known; it opens there even when an external diff tool is set.

### External diff tool

- In Settings → Diff tool, the user chooses how diffs open: **Built-in** (the default), a **preset**, or a **custom command**.
- Once a tool is set, **Open in diff tool** appears on every changed file. Double-clicking a file in the changed files panel uses the external tool instead of the built-in view, and a single click only selects it; the built-in view stays in the file's menu.
- **Presets** are found automatically: Claudette looks in each tool's standard install locations and on `PATH` (the login shell's, [§13](#login-shell-environment)), and a preset only appears if its tool is found.

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
  - Folder, name, pinned state, whether it syncs, and group order.
  - Session ID, model, effort and per-tab overrides.
  - Suffixes kept on the tab, and token stats.
  - The changed files ticked as reviewed ([§8](#8-file-changes--diff-view)).
- Tabs come back in the same order and resume their sessions, with the earlier conversation loaded so you can scroll back.
- **Starting fast.** Restored tabs don't start their `claude` process until you first select them or send them a message. Launching with many pinned tabs is quick, and tabs you don't touch use no resources.
- **Old sessions.** Claude Code deletes local transcripts after 30 days by default. A pinned tab you haven't used in a while could lose its transcript, so Claudette resumes from its session library copy, which isn't affected by that cleanup. Only a tab that syncs, or once did, has a library copy. If there is one, it's used whether or not the tab syncs now. If neither copy exists, the tab says so and waits: **Start a new session** starts one in the same folder, keeping the tab's name, pinned state, overrides and suffixes, and **Unpin and close** (**Close tab**) closes it. It doesn't start a new session by itself.
- **Missing folder.** If a restored tab's folder no longer exists (for example a deleted clone), the tab shows an error as soon as it's restored, with **Choose folder…** and **Unpin and close** (**Close tab** for an unpinned tab).
  - **Choose folder…** is for a folder that moved, or another clone of the same project. The tab moves to the chosen folder's group and its session carries on there.
  - Claude Code finds sessions by folder, so Claudette copies the transcript to its local working folder and resumes from that file, as for a session from another machine ([Session library](#session-library-sync-across-machines)).
- Pinned tabs belong to this machine. On another machine, the same sessions appear in History ([below](#history)) instead.

### Starting at login

**Start Claudette when I log in** (Settings → General, off by default) starts Claudette when the user logs in to this computer. The setting lives in the OS rather than in `settings.json`, so it's this machine's, doesn't sync, and **Reset to defaults** leaves it.

- **How it opens.** Minimized to the taskbar or Dock, without taking focus, and with tabs restored as on any launch ([above](#restore-on-launch)). If the window was maximized, it comes back maximized. A login start while Claudette is already running does nothing.
- **The entry**, one per user:
  - **MSIX:** the package's startup task (`windows.startupTask` in the manifest, task `ClaudetteAtLogin`), turned on and off with `StartupTask`. It's listed as Claudette in Task Manager's Startup apps. Windows starts it without arguments; Claudette tells from its activation (`AppInstance.GetActivatedEventArgs`, `ActivationKind.StartupTask`).
  - **Other Windows builds:** a `Claudette` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
  - **macOS:** a LaunchAgent, `~/Library/LaunchAgents/com.reapazor.claudette.login.plist`, run once at login. For `Claudette.app` it runs `open -a <the app> --args --login`. A LaunchAgent works on macOS 12, which the package still supports, and for a source build, which has no bundle; `SMAppService` needs macOS 13 and a bundle.
  - **Linux:** `claudette.desktop` in the XDG autostart folder (`$XDG_CONFIG_HOME/autostart`, or `~/.config/autostart`).
  - Every entry but the MSIX's starts Claudette with `--login`, which says it was started at login.
- **Which Claudette starts.** A machine can have an installed release, another build (an unpackaged Windows one, a Linux one) and source builds in one or more checkouts. The entry starts the best one: a release (MSIX or `Claudette.app`), then another build, then a source build.
  - Each Claudette that isn't a source build notes itself in this machine's state when it starts: the MSIX by its package family, `Claudette.app` by its path, another build by its folder. That's how a source build finds the release. A release that was never opened isn't known. A better release that's still installed keeps its place; between two of the same kind, the one that started last wins.
  - Turning it on from a source build starts the release when there is one, and the switch says so: *"It starts the installed Claudette 0.3.0 rather than this source build."*
  - A source build's entry starts its build output, not the copy it runs from, so at login it copies and runs the newest build ([Working on Claudette](#working-on-claudette)). Between source builds, the entry keeps the checkout it has while that checkout is there.
  - Any Claudette started with `--login` hands over to a better copy when one is installed: it starts that copy with `--login` and exits.
  - When a Claudette starts, it tidies the entry without turning it on or off. A release replaces an entry that starts a source build, and an entry whose copy is gone (a deleted checkout, an uninstalled release) starts the best copy left.
- **The MSIX.** Only the package can turn its own startup task on or off.
  - A source build turning it on while the MSIX's task is off writes the Run value for itself. At login it hands over to the MSIX (`IApplicationActivationManager`, with `--login`). The MSIX then turns on its task and removes the Run value.
  - While the MSIX's task is on, the switch in a source build is on but disabled: *"The installed Claudette 0.3.0 starts at login. Turn it off in that Claudette, or in Task Manager's Startup apps."* The source build knows the task is on because the MSIX notes it with itself.
- **Turned off outside Claudette.** Only the user can turn these back on, so the switch shows them off and disabled, and says where to turn them on:
  - Task Manager's Startup apps can turn off the MSIX's task (`DisabledByUser`) or the Run value (a value of the same name under `Explorer\StartupApproved\Run` whose first byte is odd). Removing the Run value also removes that note, so a later entry starts out turned on.
  - An organization's policy can hold the MSIX's task on or off; the switch says which.
  - On Linux, a file with `Hidden=true` or `X-GNOME-Autostart-enabled=false` is off.
  - macOS can turn off the LaunchAgent under **Allow in the Background** in System Settings → General → Login Items. Claudette doesn't read that.
- **Not with `CLAUDETTE_HOME`.** A Claudette started with `CLAUDETTE_HOME` (to try things against the mock) leaves the entry alone and disables the switch, since the entry would start Claudette without it.

> **Not yet tested on a real machine:** the MSIX's startup task, its activation and the handover to it, which need an installed package; and the LaunchAgent on macOS. The manifest passes `makeappx`'s schema check. Choosing the copy, the Run value (in a test key), the LaunchAgent and autostart files, and the minimized start are covered by tests.

### History

- **History** (`Ctrl/Cmd+Shift+H`, or from the new tab menu) lists past sessions, grouped by folder. Each entry shows the name/title, the machine it was last used on, last activity time, first prompt and message count.
- Search by title, folder and prompt text: every prompt of a session, not just the first (up to about 1,000 characters of each and 16,000 per session, kept in History's cache).
- Opening an entry resumes that session in a new tab. The earlier conversation is loaded into the view so you can scroll back through it. A session that's already open in a tab just selects that tab. The tab syncs if the session came from the library or has a library record ([Which tabs sync](#session-library-sync-across-machines)).
- History combines two sources:
  - Claude Code's own session storage on this machine, so it includes sessions started in the terminal.
  - Claudette's session library (below), which can include sessions from other machines.
- **Merging the sources.** A session in both is one entry: the library adds its name and, when another machine used it more recently, that machine. A session only in the library (from another machine, or older than Claude Code's cleanup) opens through "Restoring on another machine" below, and so does a session in both that another machine carried on since this machine's copy: the library's copy is the newer one.
- Opening a session applies its record's per-tab overrides (model, effort, mode, check-ins, process monitor). Records written before overrides were kept apply their model and effort.
- It also brings back the record's reviewed files ([§8](#8-file-changes--diff-view)), found in this machine's copy of the session's folder. **Open a copy** keeps them too, since the copy has the same changes.
- **Speed.** History reads every transcript line by line, skipping lines cheaply before parsing them, and caches each file by size and date, so opening it again only reads what changed.

### Session library (sync across machines)

Claudette keeps its own **session library** in a folder the user chooses (Settings → Sessions). By default it's in the app data folder. Pointing it at a folder that a sync client keeps up to date, such as Google Drive for desktop, Dropbox or OneDrive, lets other machines see and restore the same sessions. Claudette doesn't talk to Google Drive or any cloud API; it just reads and writes files, and the sync client does the rest.

**Which tabs sync.** Syncing is per tab, and opt-in. It's for work you know you'll pick up on another machine, without bringing everything over.

- A tab that doesn't sync never writes to the library: no transcript copy, no record and no lease.
- **Sync to other machines**, in the tab's menu and in **Tab settings…**, turns it on or off ([§4](#4-tabs--sessions)). It's the tab's own state, saved with it, not a per-tab override.
- **Turning it on** copies the session to the library straight away, or when the turn ends if one is running. The tab takes the lease with that copy, as a new library session does. If another machine has the session open (a live lease), sync stays off and the tab says so, since copying from here would overwrite what the other machine wrote.
- **Turning it off** stops the copying and releases this tab's lease. The copy already in the library stays as it was, so other machines can still open it.
- **Sync now**, in the menu of a tab that syncs, copies the session to the library straight away rather than after the next turn (see **Writing**). It's for catching the library up without sending a message: after a copy that failed because the library folder wasn't available (those fail quietly, into the log), after renaming the tab, or before leaving the machine.
  - It copies every file again, even one that looks unchanged, and takes the lease as any copy does.
  - It's disabled, with the reason as its tip, while a turn runs (the copy follows when it ends), before the session's first message (and a copy's, until it has its own id), and while the tab is read-only.
  - Leases are only refreshed once a minute, so it checks the lease first. If another machine has taken the session over, it writes nothing, and the tab becomes read-only as the refresh would have made it.
  - It says in the conversation how it went: *"Copied this session to the session library."*, or a warning with why it couldn't (the folder isn't available, or this machine no longer has the transcript).
- **New tabs** follow **Sync new tabs to the session library** (Settings → Sessions, off by default), however they're opened: the new tab picker, a group's `+`, `--folder`, Open Recent and the jump list, or a dropped folder.
- **Opening from History.** A session from the library (another machine's, or one continued elsewhere) or one with a library record opens with sync on: someone chose to sync it, so it keeps syncing wherever it's opened. So does **Open a copy** of one, under the copy's own id once its first turn gives it one. A session that only ever lived on this machine opens with sync off.
- A tab that doesn't sync also doesn't get the library's longer-term archive: once Claude Code cleans up its transcript, the session is gone ([Old sessions](#restore-on-launch)).

**What's in the library.** One folder per session holding:

- A **session record** (JSON): the tab name, model, effort, per-tab overrides, token stats, which machine last used it and when, the project identity (below), and the changed files ticked as reviewed ([§8](#8-file-changes--diff-view)). Their paths are relative to the session's folder, since that folder's path differs between machines; a file with no relative path from there, such as one on another drive, is left out.
- A **copy of Claude Code's transcript** (`.jsonl`), plus subagent transcripts.

Claude Code's credentials and settings are never copied.

**Writing.**

- For a tab that syncs, Claudette copies the transcript into the library after each turn finishes, never while Claude Code is writing it. It waits a second after the turn's result, so Claude Code has finished writing. Turning sync on, and **Sync now**, wait the same second.
- Each file is written to a temporary name, then renamed, so a sync client never uploads a half-written file. The record is written last, so a record in the library means its transcript is there too. On Windows the rename fails while anything has the file open, such as History reading a record, so it's tried again for a moment, and so is a read that meets a rename under way.
- A file that hasn't changed (same size, and a modified time within 2 seconds, since some synced drives store coarse times) isn't copied again, except by **Sync now**.
- Ticking a changed file as reviewed ([§8](#8-file-changes--diff-view)) copies the session 2 seconds after the last tick, so ticking several files writes the record once. Only the record has changed, so the transcript isn't copied again. During a turn, the copy as the turn ends carries the ticks.
- Library copies aren't affected by Claude Code's own cleanup of local transcripts (30 days by default), so the library also works as a longer-term archive for the sessions that sync. It has its own retention setting.

**Restoring on another machine.**

1. **Find the project.** Folder paths differ between machines (`D:\Repos\api` vs `/Users/me/src/api`), so the session record stores a project identity: the git remote URL, the branch, and the path inside the repo.
   - Remote URLs are compared in a normal form, so `git@github.com:Owner/Repo.git` and `https://github.com/owner/repo` match.
   - Claudette looks for a matching folder among the folders it remembered, recent and favorite folders, and open tabs, and prefers a clone on the recorded branch. Then it tries the recorded path, in case it exists here too.
   - If it can't find one, it asks the user to pick the folder and remembers the answer for that machine.
   - The identity is read from the `.git` folder's files (including worktrees), without running git. Whether there were uncommitted changes needs `git status`, so that part runs git.
2. **Check the code.** The library moves the conversation, not the code. If the branch or commit on this machine differs from what the other machine had, or the other machine had uncommitted changes, Claudette warns: *"This session was last used on DESKTOP-01 on branch `feature/auth` at `a1b2c3d`. This folder is on `main`. Claude's earlier file changes may not be here."* The user can continue anyway or cancel and sync the code first (push/pull).
3. **Resume.**
   - Claudette copies the library's `<session-id>.jsonl` to a local working folder (`<app data>/sessions/`) and resumes with `claude --resume <local path>`.
   - After each turn, it copies the file back to the library. The tab syncs, as any session opened from the library does, until it's turned off.

**How resuming from a file behaves** (confirmed by the spike):

- `--resume <path>` loads the full history and keeps the same session ID.
- Claude Code then writes the continued transcript, complete and not just the new turns, to `<session-id>.jsonl` in the **same folder** as the file it was given.
- Because the local working copy already has that name, Claude Code keeps writing to it. Resuming straight from the library folder would make Claude Code write live into the synced folder in the middle of a turn, which the library is designed to avoid.

> **Not yet tested:** resuming on macOS a transcript recorded on Windows, and the reverse. Transcripts store absolute paths (`cwd`, file paths in tool calls), so check this once a Mac is available.

**One machine at a time.**

- While a tab that syncs has its session open, Claudette keeps a small lease file next to it ("in use on DESKTOP-01", refreshed every minute). The tab takes the lease when it starts a session that's in the library; a new session gets one with its first library copy. A tab that doesn't sync ignores leases.
- A restored tab that syncs, whose session another machine holds a live lease on (it was taken over while Claudette was closed), becomes read-only instead of starting, and says where the session continued. A restored tab that doesn't sync starts from this machine's transcript as usual.
- Opening a session from History that another machine is actively using, whether its transcript is on this machine or only in the library, asks the user to either:
  - **Open a copy**, which forks it into a new session with `--fork-session`, or
  - **Take over**, after which the other machine's tab becomes read-only on its next sync.
- A lease that hasn't been refreshed in 10 minutes counts as stale.
- The lease names the machine and a random id for this run of Claudette, so two copies of Claudette on one machine are told apart.
- When a refresh finds another machine's name in the lease, the session was taken over. The tab stops its `claude` process, becomes read-only, and says where the session continued.
- If the sync client creates conflict copies (for example `session (1).jsonl`), Claudette shows them in History as separate, forked entries. It never merges them.
  - It recognizes the numbered copies Google Drive and OneDrive make, Dropbox's "conflicted copy", Syncthing's `.sync-conflict-…`, and `<id>-<machine>.jsonl`.
  - Opening one copies it to a folder of its own and resumes it with `--fork-session`, so it can't overwrite the working copy of the original.
- Library retention (Settings → Sessions) never deletes a session that's open here in a tab that syncs, or held by a live lease. An old library copy of a tab that no longer syncs is pruned like any other.

**Privacy.** Transcripts contain code, command output and anything else Claude read in the project. When the user picks a library folder inside a known cloud-sync location, Claudette says so and asks them to confirm.

**Rejected alternative.** Pointing Claude Code's whole config folder at the cloud drive (`CLAUDE_CONFIG_DIR`) would also sync credentials and settings, and have several machines writing the same live files at once. The library copies only transcripts, only between turns.


### Working on Claudette

Claudette can host the Claude Code session that works on Claudette's own source. When it runs from a source build, its state survives the rebuilds that session makes: it restarts into each new build with every tab as it was. An installed Claudette uses the same handover to restart into a new release ([§2](#updating-claudette)).

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
  - Every open tab, pinned or not, in order, with its session, name, overrides, whether it syncs, kept suffixes, token stats and reviewed files.
  - The message typed in each tab, with its one-off suffixes and attached images.
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
- A project action finishes: a build or other long job ended or failed (see [§18](#project-tools)).

Clicking a notification brings Claudette to the front and goes to the relevant tab or screen. Notifications are skipped when Claudette is focused and that tab is already selected. The Dock (macOS) and taskbar (Windows) show a badge with the number of tabs needing input.

**Pushes to the phone.** Every `claude` Claudette starts gets `CLAUDE_CLIENT_PRESENCE_FILE`, naming `presence` in the data folder. The file exists only while Claudette's window is in front, so Claude Code's Remote Control pushes reach the phone only while you're away from Claudette ([§18](#remote-control-the-claude-app)).

- **What each one says.** Tab notifications carry the tab's name as their title:
  - **Finished:** the first line of Claude's reply. Only a turn that ends normally counts; one you stopped, or that ended with an error, doesn't.
  - **Needs input:** what's waiting, such as *"Allow this command? npm test"*, *"Claude has a question: Which database?"* or *"Claude has a plan for you to review."* Perforce uses it too ([§18](#perforce-ticket-handling)): *"Perforce needs your password to log in as matt @ ssl:perforce:1666."*, or *"Perforce needs you to log in: run p4 login in a terminal, or log in with P4V."*
  - **Errors:** *"Claude Code stopped unexpectedly (exit code 3)."*, or why it couldn't start.
  - **Project actions:** *"Build editor failed (exit code 6)."* Only while Claudette isn't in front, whichever tab is selected, since the sidebar already shows the job; a job the user stopped doesn't notify.
  - **Check-ins:** Settings → Check-ins → **Notify me when a check-in is sent** (off by default), which Tab settings can override ([§5](#check-ins-on-long-turns)).
- **Skipping.** App-wide notifications (usage alerts, sign-in, updates) are skipped while Claudette is focused, because the header, the sign-in banner or the sign-in screen already shows them. Usage alerts also keep their line under the header.
- **One per subject.** A newer notification replaces an older one of the same kind for the same tab. A tab's notifications are taken away once you look at it; a waiting-prompt notification also goes once the prompt is answered. An update is announced once per version; the version last announced is saved with this machine's state, so a restart doesn't announce it again.
- **Clicking.**
  - A tab notification selects the tab, expanding its group if it's collapsed.
  - A usage alert opens the Usage panel, an update opens the update dialog, and the sign-in notification opens the sign-in dialog ([§11](#signing-in)).
  - A project action's notification selects its tab and opens its Project page on the log of the run it's about.
- **Badge.** Settings → Notifications → **Show the number of tabs needing input on the Dock or taskbar icon**. On Windows it's an overlay icon on the taskbar button, drawn by Claudette.
- **The icon while tabs work.** Settings → Notifications → **Animate the Dock or taskbar icon while tabs are working** (on by default).
  - **Windows:** while any tab is working, the taskbar button's overlay shows Claude's spark, pulsing. The overlay holds one image, and the number of tabs needing input comes first: the spark comes back once no tab needs input, or at once with the badge off. Windows takes the button's own icon from the package (or, unpackaged, from Claudette's AppUserModelID), so only the overlay can move.
  - **macOS:** the whole Dock icon moves: Claudette types while tabs work, and waves while a tab needs input, under the badge's number.
  - The frames are drawn with the icon ([§2](#packaging-and-signing)).
- **Flashing.** When a tab starts needing input while Claudette isn't in front, the Windows taskbar button flashes until Claudette comes to the front, or no tab needs input any more. It follows **A tab needs permission or an answer**. The Dock has nothing like it; the waving does that job.
- **How each OS does it** (the code is in `Claudette.Platform/Notifications`):
  - **Windows:** WinRT toasts (`ToastNotificationManager`), called through source-generated COM interop so the app stays a plain `net10.0` build. A click raises the toast's `Activated` event in the running Claudette. An MSIX install has package identity. Run unpackaged, Claudette sets its AppUserModelID (`reapazor.Claudette`) and registers it under `HKCU\Software\Classes\AppUserModelId`, as the Windows App SDK does. The badge and the spark use `ITaskbarList3::SetOverlayIcon`, and the flash `FlashWindowEx`.
  - **macOS:** `UNUserNotificationCenter` through the Objective-C runtime, with a delegate that reports clicks and lets notifications show while Claudette is in front. It needs the app bundle's identifier, so a build run with `dotnet run` has no notifications and Settings says so. The badge is the Dock tile's `badgeLabel`, and the animation sets `NSApplication`'s `applicationIconImage` to each frame (nil gives the bundle's icon back).
  - **Linux:** `notify-send --wait` with a default action, which reports a click. Without `notify-send`, there are no notifications.

> **Not yet tested on a real machine:** showing and clicking notifications on Windows and macOS, and the badges. CI builds a real toast through WinRT on Windows (without showing it), and checks the Objective-C string calls on macOS.

## 11. Sign-in

Claude Code keeps its own credentials. Claudette never reads or stores them; it only detects when Claude Code needs a sign-in and runs Claude Code's own sign-in flow.

### Detecting

- **On launch**, before any tab starts, Claudette runs `claude auth status`. It prints JSON and exits with 0 when signed in and 1 when not. The spike confirmed these fields:
  - `loggedIn` and `authMethod` (`claude.ai`, `none`, …; 2.1.284's source also has `api_key`, `api_key_helper`, `oauth_token` and `third_party`).
  - `email`, `orgName` and `subscriptionType`.
  - `configDirectory`, and `projectsDirectory`, which is where Claude Code keeps its transcripts. History uses it.
- **While running**, any of these from a session means "needs sign-in":
  - An `assistant` message with `error: "authentication_failed"`, or `"oauth_org_not_allowed"` (an account from an organization that isn't allowed, which signing in with another account fixes). This is what a signed-out Claude Code sends: it starts normally and answers the first message with *"Not logged in · Please run /login"* (the `signed-out` protocol fixture).
  - An `auth_status` message with an `error`. Claude Code 2.1.284 only sends `auth_status` with the hidden `--enable-auth-status` flag, which Claudette doesn't pass, to report cloud credential helpers such as `awsAuthRefresh`, so in practice it doesn't arrive.
  - A session that fails to start with an authentication error. Its wording isn't documented, so Claudette matches the error and the end of the process's error output loosely: `/login`, "Not logged in", "Invalid API key", "OAuth token" and the like. Anything else stays an ordinary start error with **Restart**.

### Signing in

- At launch, Claudette shows a sign-in screen instead of the tabs. In the middle of a session, it shows a banner across all tabs, which stay open: *"Claude Code needs you to sign in."*, with a **Sign in** button. If Claudette isn't focused, it also sends an OS notification, once however many tabs find Claude Code signed out. Clicking the notification opens the sign-in dialog. The account menu runs `claude auth status` again, so it shows what Claude Code now reports.
- The banner's **Sign in** opens the same sign-in screen as a dialog over the window, and starts signing in straight away. The dialog can be closed.
- **Sign in** (main flow) uses the utility session's control protocol, as the Agent SDK does:
  1. Claudette sends `claude_authenticate` with `loginWithClaudeAi: true`. Claude Code replies with two URLs and doesn't open a browser itself:
     - `automaticUrl` redirects back to a local port Claude Code is listening on, so sign-in finishes without copying anything.
     - `manualUrl` redirects to a page that shows a code to copy.
  2. Claudette opens `automaticUrl` in the default browser and sends `claude_oauth_wait_for_completion`, which returns when sign-in finishes: with the new `account`, or with the sign-in's error.
  3. If that doesn't work (for example a browser on another device, or a firewall blocking the local port), the user can switch to **Enter a code instead**. Claudette opens `manualUrl`, shows a code field, and sends the code with `claude_oauth_callback`, as `authorizationCode` and `state`: the two halves of the `code#state` the page shows. The sign-in keeps waiting meanwhile, so it finishes whichever way the browser gets there.
- These control requests are **undocumented**. The TypeScript SDK sends them from `claudeAuthenticate`, `claudeOAuthCallback` and `claudeOAuthWaitForCompletion` (SDK 0.3.284, which bundles Claude Code 2.1.284), and the answers above are from Claude Code 2.1.284's source. Claude Code refuses `claude_authenticate` when managed settings' `forceLoginMethod` rules out the kind of account asked for. No real sign-in has completed through them yet.
- **Fallback: `claude auth login`** (documented). Claudette runs it when `claude_authenticate` is rejected or returns no address, and for SSO. What it does with no terminal attached was confirmed from 2.1.284's source and the spike, but not yet with a completed sign-in:
  - It opens the browser itself, at the address that finishes on its own, and prints the other one: `If the browser didn't open, visit: <url>`, then `Paste code here if prompted >`. Claudette takes the first web address in its output, with terminal escape codes removed.
  - **Open browser again** and **Enter a code instead** open that printed address, whose page shows a code, and show the code field. The code field writes the code to the command's input, which reads one `code#state` per line. A line without both halves gets *"Invalid code. Please make sure the full code was copied."* on its error output, which Claudette shows while the command keeps waiting.
  - It exits with 0 and prints `Login successful.` once signed in, and exits with 1 and prints `Login failed: …` (or the organization's message) if not. Claudette goes by the exit code, and `claude auth status` then has the last word.
  - It's started through `IProcessLauncher` with an environment from `ClaudeEnvironment`, like every `claude` ([§13](#login-shell-environment)), and stopped after 10 minutes.
- While it waits, Claudette shows *"Finish signing in in your browser"* and stays responsive. The screen has:
  - **Open browser again**, which reopens the same URL, in case the browser didn't open or the tab was closed.
  - **Enter a code instead**, as described above.
  - **Cancel**, which also ends `claude auth login`.
  - **More options**: sign in with an Anthropic Console account for API usage billing (`claude_authenticate` with `loginWithClaudeAi: false`, or `claude auth login --console`), or with SSO (`claude auth login --sso`; the control request has no option for it). The screen offers them before signing in, too.
- **I've already signed in**, for a sign-in done in a terminal, runs `claude auth status` again, and says so if Claude Code still reports no sign-in.
- When sign-in succeeds, Claudette runs `claude auth status` again, shows the account, and restarts the utility session, so plan usage comes from the new sign-in. Any tab that failed is restarted with `--resume`. A tab that couldn't start isn't tried again until then, but still takes messages.
- **Messages sent while signed out** stay queued, with their attached images, and are delivered, in order, once sign-in completes. The message that found Claude Code signed out is one of them: a message nothing came back for before the sign-in error never reached the model, so it's sent again. One that was answered before the error isn't.
- If sign-in fails (timed out, cancelled, organization not allowed), Claudette shows Claude Code's message, from the control request or the command, and a **Try again** button, which repeats the same kind of sign-in.
- **Account menu** (in the header, on the right): the signed-in email, plan and organization from `claude auth status`, or how Claude Code is signed in when there's no plan (an API key, say), and **Sign out…**, which runs `claude auth logout`. Signed out, it offers **Sign in**.
- **Plan and billing.** In the header, the plan beside the email (*"Max plan"*) is a link to the plan's usage on claude.ai, `claude.ai/new#settings/usage` ("Plan usage"). The account menu has a link to where the account's billing is managed; the header's plan goes there too for an account with no usage page on claude.ai (an API key or Console account). Both open in the browser:
  - a Claude plan, or a Claude account without one: `claude.ai/settings/billing`, **Plan and billing**;
  - an API key or Console account: the Claude Console's billing page, **Console billing**;
  - a cloud provider (Bedrock, Vertex, Foundry) bills through that provider, so there's no link, and the header shows the account as one piece.
  - Signing out asks for confirmation first, because every tab will stop working, and Claude Code is signed out in the terminal too.
  - Afterwards Claudette runs `claude auth status`. If Claude Code still reports a sign-in (an API key in the environment, which logging out doesn't remove), it says so. Otherwise the banner shows, without a notification, messages are held, and every tab that was running restarts on its session after the next sign-in, since that may be a different account.
- **Settings → Claude Code** shows the same account with **Sign in** and **Sign out…**, wired to the same flows. Because Settings is a separate window, the sign-in screen and the confirmation show inside it.

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
  - **Dismiss** hides it until a newer version comes along, across restarts too: the dismissed version is saved with this machine's state. It also goes away once no open tab runs an older version.
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
│  Claude Code updates         │                                  │  Credential store │
│  Source builds: copies, new  │                                  │  Update installers│
│   builds, restart snapshots  │                                  │  (MSIX, .app)     │
│  Claudette releases, updates │                                  │  Login shell's    │
│  Git: identity, working tree │                                  │   environment     │
│  Auth, install checks        │                                  └───────────────────┘
│  Perforce: tickets, CLs      │
│  Claude's service status     │
│  Settings, state, sync       │
└───────────────┬──────────────┘
                │ stdin/stdout (JSON lines)
        ┌───────▼───────┐
        │  claude CLI   │  × one per tab
        └───────────────┘
```

- **Claudette.Core** has no UI dependencies, so it can be unit tested and could be reused by another front end. External diff tools live here rather than in Platform: they only look for files and start processes through `IProcessLauncher`. So does running a source build from a copy and restarting it into new builds ([§9](#working-on-claudette)), which is plain file copying and process starting on every OS.
- **Claudette.Usage** holds the usage engine, with no UI: parsing, the SQLite history, the burn rate and projection, alerts and the polling schedule.
- **Claudette.Platform** holds the OS-specific code: the process monitor, notifications with the Dock and taskbar badge ([§10](#10-notifications)), the OS credential store for a stored Perforce password ([§18](#perforce-ticket-handling)), the installers for Claudette's own updates: the MSIX update through `PackageManager` on Windows, and swapping `Claudette.app` on macOS ([§2](#updating-claudette)), reading the login shell's environment ([below](#login-shell-environment)), and keeping the computer awake while tabs are connected to the Claude app ([§18](#remote-control-the-claude-app)). Their interfaces, `ICredentialStore`, `IAppInstaller`, `ILoginShell` and `ISleepBlocker`, are in Core, with the release feed, the downloader and `UserEnvironment`.
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
          [--append-system-prompt <the Perforce workspace note, §18>]
```

- `--permission-prompt-tool stdio` sends permission prompts to Claudette as control requests. The TypeScript SDK passes this flag when a `canUseTool` callback is set.
- `--thinking-display summarized` makes newer models return thinking text; by default they send empty thinking blocks. The flag isn't in `claude --help`, but the Agent SDKs pass it.
- `--forward-subagent-text` includes subagents' text and thinking in the stream, so subagent groups can show them.
- **Clean environment.** Claude Code sets session variables for the processes it starts, such as `CLAUDECODE`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_ENTRYPOINT` and `CLAUDE_CODE_MESSAGING_SOCKET`. If Claudette was started from a terminal inside Claude Code, those variables make `claude` behave as a child session; in the spike it ignored the API key and reported "Not logged in".
  - Claudette removes exactly those variables. The full list is `ClaudeEnvironment.SessionVariables`, tracked in `compat/surface.yaml`.
  - It doesn't strip by prefix, because variables like `CLAUDE_CONFIG_DIR` and `CLAUDE_CODE_USE_BEDROCK` are user configuration.
  - The real-CLI tests run from inside Claude Code confirmed that the list is enough.
  - The environment it removes them from is the user environment, which on macOS and Linux can hold the login shell's variables ([below](#login-shell-environment)).

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
| Send a message, with images | A `user` message as one JSON line on stdin. Images are base64 `image` content blocks before the text ([§5](#attachments)). See "Messages sent while Claude is working" below. | Yes |
| Receive output | JSON lines on stdout: `system/init`, `system/status`, `assistant`, `user` (tool results, with `tool_use_result`), `stream_event` (partial text), `result`, `rate_limit_event`, `auth_status`, `permission_denied`, `api_retry`, `conversation_reset`, `task_started` / `task_progress` / `task_updated` / `task_notification`, `tool_progress`, `thinking_tokens`, `autocompact_state` | Yes |
| Stop the current turn | `interrupt`. The reply lists `still_queued` messages; the turn ends with a `result` of `error_during_execution` / `aborted_streaming`. SIGINT is a fallback. Never SIGTERM: it leaves the turn unfinished with no result. | Yes |
| Permission prompts | Incoming `can_use_tool`; reply allow, allow with `updatedPermissions`, or deny with a message ([§7](#7-permission-prompts)) | Behavior yes, wire format no |
| Hook callbacks | `hooks` in `initialize`; incoming `hook_callback`, answered with the hook's output (below) | Behavior yes, wire format no |
| Change model | `set_model` with `model`. Applied in place, even mid-turn, and the conversation is kept. Claude Code also emits a `user` message containing `<local-command-stdout>Set model to …</local-command-stdout>`, which Claudette shows as a small system note. | Yes |
| Change effort | `apply_flag_settings` with `settings: { effortLevel }`. Applies from the next request, which carries `output_config.effort`. | Yes |
| Change permission mode | `set_permission_mode` with `mode`; also reported as a `system/status` message | Yes |
| Context window usage | `get_context_usage`. Claude Code calls the API's token-counting endpoint for this, which costs nothing. | Yes |
| Stop a background task | `stop_task` with `task_id` | Yes |
| Stop one subagent | `stop_task` with the subagent's task id, in the foreground too ([§18](#agent-map)) | **No** (documented for background tasks) |
| Plan usage limits | `get_usage` ([§6](#data-source)) | **No** (marked experimental) |
| A turn stopped at a usage limit | A `rate_limit_event` with `status: rejected`, `resetsAt` and no `errorCode`, and a `result` with `is_error` and `api_error_status` 429 ([§6](#continuing-after-a-limit-resets)) | Yes (`rateLimitType`, which only names the limit, no) |
| Sign-in | `claude_authenticate`, `claude_oauth_wait_for_completion`, `claude_oauth_callback` ([§11](#signing-in)) | **No** |
| Session title | `generate_session_title`, `rename_session` (below) | **No** |
| Remote Control | `remote_control` with `enabled` and `name`; `system/bridge_state` reports the connection, and `system/worker_shutting_down` its end ([§18](#remote-control-the-claude-app)) | **No** (`worker_shutting_down` yes) |
| Resume | `--resume <session-id>`, or `--resume <path to a .jsonl>` ([§9](#session-library-sync-across-machines)) | Yes |
| Feature detection | The `capabilities` array on `system/init`. Check this instead of comparing version numbers. | Yes |

Every **No** row has a fallback, listed in its section, and is marked `undocumented` in `compat/surface.yaml` ([§16](#16-tracking-claude-code-changes)).

**Hook callbacks.** Claudette can register hooks that Claude Code calls back over the control protocol, the way the Agent SDKs register hook callbacks. Perforce ticket handling uses a PreToolUse hook for Bash ([§18](#perforce-ticket-handling)). The registration rides on `initialize`, numbered as the Python Agent SDK numbers them, with the timeout in seconds:

```
→ {"type":"control_request","request_id":"req_1","request":{"subtype":"initialize",
     "hooks":{"PreToolUse":[{"matcher":"Bash","hookCallbackIds":["hook_0"],"timeout":300}]}}}
← {"type":"control_request","request_id":"<uuid>","request":{"subtype":"hook_callback","callback_id":"hook_0",
     "input":{"session_id":…,"transcript_path":…,"cwd":…,"prompt_id":…,"permission_mode":"default",
              "hook_event_name":"PreToolUse","tool_name":"Bash",
              "tool_input":{"command":"p4 info","description":…},"tool_use_id":"toolu_…"},
     "tool_use_id":"toolu_…"}}
→ {"type":"control_response","response":{"subtype":"success","request_id":"<uuid>","response":{"continue":true}}}
```

Confirmed against Claude Code 2.1.284 with the mock Messages API (2026-09-29):

- The callback comes before the permission check: the `can_use_tool` request follows the hook's answer, and waits for it.
- `{"continue": true}` and `{}` both let the tool run, and the permission prompt still happens. The hook output is the documented hook JSON ([hooks](https://code.claude.com/docs/en/hooks)); Claudette never sends a decision or changed input.
- An error answer is logged on standard error, and the tool runs as if there were no hook.
- A hook that isn't answered within its `timeout` gets a `control_cancel_request`, and **the tool isn't run**: its result is an error, *"PreToolUse hook did not respond before its timeout (host client may be unreachable). The tool call was not executed…"*.
- Interrupting the turn while a hook waits also sends a `control_cancel_request` for it, and the tool call is rejected.

`ClaudeSession` runs each callback off the read loop and answers with its output; an unknown callback or a failing one gets an error answer, and a withdrawn one is cancelled and not answered. The initialize request still sends `"hooks": null` when there are none.

**Messages sent while Claude is working.**

- **During a tool call:** the message is delivered inside the running turn, together with the next tool result, as a note that says *"The user sent a new message while you were working"*. This is what check-ins rely on ([§5](#check-ins-on-long-turns)).
- **During a text-only reply:** there's no tool boundary, so the message waits and runs as the next turn.

**Tool results.** The `user` message that carries a tool result also has a `tool_use_result` field with structured details:

- Edit and Write: `originalFile`, `structuredPatch`, `oldString` / `newString`. Transcripts keep `originalFile` only up to 10,000 characters ([§8](#8-file-changes--diff-view), "Large files in transcripts").
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

### Login shell environment

An app started from the Dock, Finder or a desktop launcher gets a minimal environment: on macOS its `PATH` is `/usr/bin:/bin:/usr/sbin:/sbin`. Homebrew's `PATH`, nvm, pyenv and the like are usually set in `~/.zprofile` or `~/.bashrc`. Claude Code's Bash tool sources the shell's startup file for aliases and functions, but environment variables come from Claude Code's own environment ([tools reference](https://code.claude.com/docs/en/tools-reference#what-persists-between-commands)). So Claude's commands in such a Claudette couldn't find `node`, `dotnet` or Homebrew's tools, which work in a terminal `claude`. Claudette reads the login shell's environment, as VS Code does.

- **When.** On macOS and Linux, when **Use my login shell's environment** is on (Settings → Claude Code, on by default) and Claudette wasn't started from a terminal. Windows apps get the user's full environment, so Windows never needs it.
  - **Started from a terminal** means `TERM` is set or Claudette has a controlling terminal (`/dev/tty` opens). Its environment came from a shell then, `dotnet run` included. An app started from the Dock, Finder, `open` or a desktop launcher has neither.
  - Either one is enough. `TERM` without a terminal is left by a terminal that has closed, or by a desktop session started with `startx`, whose environment came from a login shell. And an interactive shell run while Claudette has a terminal would take that terminal over (bash opens `/dev/tty` for job control).
- **How.** `LoginShellReader` (Claudette.Platform, behind `ILoginShell` in Core) runs the shell once per run:
  - `$SHELL`, or `/bin/zsh` on macOS and `/bin/bash` on Linux.
  - As a login, interactive shell: `-i -l -c` for bash, zsh, sh, dash, ksh, mksh and yash; `-l -i -c` for fish; `-i -c` for tcsh and csh. Other shells (nushell, xonsh, PowerShell) aren't run, and Diagnostics says so.
  - The command prints the environment between random markers, so anything the rc files print is ignored. It prints it twice: `/usr/bin/env -0`, NUL-separated so values with line breaks stay whole, then plain `/usr/bin/env` for an `env` without `-0`, read a line at a time.
  - It starts in the home folder, with Claudette's own environment minus Claude Code's session variables, plus `CLAUDETTE_RESOLVING_ENVIRONMENT=1`. An rc file can check that to skip slow or interactive setup, as with VS Code's `VSCODE_RESOLVING_ENVIRONMENT`.
  - Through `IProcessLauncher`, with a 10-second timeout on the injected `TimeProvider`. A shell that times out is stopped. On a timeout or a failure Claudette logs a warning and carries on with its own environment.
- **Not holding up the window.** Reading starts in the background at launch. The first `claude` start waits for it, up to the timeout: in practice the locator's `claude --version`, before the utility session and the first tab.
- **The merge** (`UserEnvironment`, Core): Claudette's own environment with the login shell's on top, so the shell's values win. Except:
  - `CLAUDETTE_*` variables always come from Claudette's own environment.
  - Claude Code's session variables are never taken from the shell. `ClaudeEnvironment` strips them for `claude` anyway.
  - The shell's bookkeeping about itself (`_`, `PWD`, `OLDPWD`, `SHLVL`) is left out.
  - For `claude`, `ClaudeEnvironment` then removes the session variables and applies the changes for that launch on top.
- **What gets it.** Every `claude` (tabs, the utility session, `--version`, `doctor`, `update` and `auth`), and the other programs Claudette runs for the user: git, `p4` and P4V, diff tools, Homebrew, and project tools' builds, editors and actions ([§18](#project-tools)).
  - `claude` and the diff tool presets are looked for on the login shell's `PATH`. A bare program name (`git`, `p4`, a custom diff command's program) is found on it too, since .NET would search Claudette's own.
  - That way `p4` sees the same `P4CONFIG` and `P4PORT` as Claude's own `p4` commands ([§18](#perforce-ticket-handling)), and git finds Git LFS and credential helpers installed with Homebrew.
  - Claudette's own helpers keep Claudette's environment: `ps`, `notify-send`, `secret-tool`, the update installers and restarting Claudette. They're at fixed paths and don't depend on the user's setup.
- **The setting** applies to processes started after the change. Turning it on reads the login shell then, if this run hasn't yet. Turning it off gives new processes Claudette's own environment. The `claude` found at launch stays until the next start.
- **Diagnostics** (Settings → Advanced) say whether it was used, which shell, how long it took and the names of the variables it added or changed. Otherwise they say why not: turned off, started from a terminal, Windows, or the shell timed out, failed or isn't supported. Never a variable's value, since those can be secrets.

> **Not yet tested on a real machine:** a Claudette started from the Dock on macOS, or from a desktop launcher on Linux. The tests run real bash and dash (and zsh and fish where installed) with a made-up home folder.

## 14. Settings

A **Settings** window opens with `Ctrl+,` on Windows or `Cmd+,` on macOS, where it is also **Settings…** in the app menu. It follows each platform's conventions:

- A sidebar lists the categories. Below them, after a divider, the selected tab's project has a group of its own ([below](#the-projects-pages)).
- Changes apply immediately; there is no Save button.
- Each category has **Reset to defaults**. In Sessions it leaves the library folder and settings sync as they are, since changing either moves where sessions and settings live; in New tabs it leaves favorite and recent folders, which are this machine's data rather than settings; in General it leaves **Start Claudette when I log in**, which the OS keeps. No **Reset to defaults** touches a project's files or its remembered choices, and the project's pages have none.
- A search box filters settings by name.
- The foot of the sidebar shows Claudette's version and **Report an issue** ([Version](#version)).

### Categories

| Category | Settings |
|---|---|
| General | Start Claudette when I log in (off by default; this machine's, kept by the OS, and left by **Reset to defaults**; [§9](#starting-at-login)). Confirm before closing a working tab. Also rename the session in Claude Code when a tab is renamed. Show Claude's service status (on by default): the header's dot and the incident banner ([§18](#service-status)). Claudette's version and updates: check for updates automatically (on by default), include pre-releases (off), **Check now**, and the update's actions. See [Updating Claudette](#updating-claudette). |
| Sessions | Also restore unpinned tabs on launch (off by default; pinned tabs are always restored). Session library folder (with **Browse…** and **Move library…**, which copies existing sessions to the new folder). Sync new tabs to the session library (off by default; each tab can be switched with **Sync to other machines** in its menu). Name for this machine, as shown in History. How long to keep sessions in the library. Sync Claudette's settings through the library (off by default). See [§9](#session-library-sync-across-machines) and [Settings sync](#settings-sync-optional). |
| Processes | Show the process monitor. Refresh interval. Show command lines. See [§4](#process-monitor). |
| Claude Code | Path to `claude` (auto-detected, with **Browse…**). Installed version and install method, from `claude doctor`. Signed-in account (email, plan and organization), with **Sign in** / **Sign out…**, the same as the header's account menu ([§11](#signing-in)). Check for Claude Code updates automatically. Use my login shell's environment (macOS and Linux only, on by default; [§13](#login-shell-environment)). **Claude app (Remote Control)**: Connect new tabs to the Claude app (off by default; each tab has its own switch), with what it does, the privacy note and how to get pushes on the phone, and Keep this computer awake while tabs are connected (on by default). Disabled, with the reason, when the account can't use it ([§18](#remote-control-the-claude-app)). |
| New tabs | Default model, effort level and permission mode. The permission mode is **Claude Code's default** unless chosen, named with the mode it gives, usually Auto ([Starting mode](#starting-mode)). The model and effort lists are what Claude Code offered in its last `initialize` reply on this machine (the models and each one's effort levels, kept with the machine's state), with a built-in list only until a session has started; Tab settings… lists them the same way. Number of recent folders to keep (default 20), and **Clear recent folders**. Favorite folders (**Add folder…**, **Move up**, **Move down**, **Remove**), in the order the new tab picker shows them. See [Opening a tab](#opening-a-tab). |
| Appearance | Theme: follow system, light or dark. Style: Standard (the default) or Claude, the Claude apps' look ([Visual style](#visual-style)). Font and size for the conversation, and for code: pick an installed font or type a name; empty means the default (the app's own font, and Cascadia Mono, Consolas or Menlo for code), and a font that isn't installed falls back to it. Markdown follows these too (LiveMarkdown brings its own Arial and Consolas otherwise). Show thinking expanded or collapsed by default. Show fun words while Claude works, and show what Claude is doing while it works (both on by default; [Working line](#working-line)). **Detailed usage header** (off by default): the same switch as the header's chevron, kept on this machine rather than synced ([Detailed header](#detailed-header)). Show context on tab rows (on by default; [§4](#sidebar)). **Density**: Comfortable (the default) or Compact, which tightens the conversation's spacing, message and card padding and tool rows, the sidebar's rows, and the composer's padding. It applies at once and syncs with the other Appearance settings. |
| Usage | Warning thresholds (default 75% and 90%). Burn rate window (default 30 minutes). Show model-specific weekly meters, and read them from `/usage` if `get_usage` stops working (off by default). Continue tasks when a usage limit resets (on by default; each tab can override it; [Continuing after a limit resets](#continuing-after-a-limit-resets)). Keep usage history: 1 day, 1 week, 1 month (default), 1 year or forever, with a **Clear usage history** button beside it. See [Usage history](#usage-history). |
| Quick suffixes | The list of suffixes: label, text and optional shortcut. Add, edit, reorder, delete. See [§5](#quick-suffixes). |
| Check-ins | On/off. Run time before checking in. Quiet time before checking in. Check-in message text. Notify me when a check-in is sent. See [§5](#check-ins-on-long-turns). |
| Diff tool | Built-in, a preset or a custom command, with **Test**. See [§8](#external-diff-tool). |
| Project tools | Unreal's default editor configuration (Development or DebugGame). Project files for Visual Studio, VS Code or Xcode (the OS's own by default). Tell Claude about Unreal projects (on by default). Unity's default code optimization (Release or Debug), and Tell Claude about Unity projects (on by default). The Godot executable (**Browse…**, **Detect**), and Tell Claude about Godot projects (on by default). Open solutions with the OS's app, Rider, Visual Studio, VS Code or another program (**Browse…**). See [§18](#project-tools). |
| Notifications | On/off for each type in [§10](#10-notifications), including **A project action finishes**. Dock/taskbar badge on/off, and animating the icon while tabs work. |
| Keyboard | List of shortcuts, each one rebindable ([below](#keyboard-shortcuts)). |
| Perforce | Off by default. Keep Perforce logins fresh. Password source. Renew-before time. Tickets for all hosts. Show changelist on tabs. The stored password (**Save** / **Forget**). Per-folder server and user. See [§18](#perforce-ticket-handling). |
| Advanced | Protocol logging and **Open log folder**. **Diagnostics** page ([§16](#staying-tolerant-at-runtime)). Extra command-line arguments passed to `claude`. Minimum supported Claude Code version (read-only). |

### Keyboard shortcuts

Settings → Keyboard lists every shortcut Claudette handles, with its default from the section that describes it: new tab, close tab, next and previous tab, go to tab 1–9, History, Settings, collapsing the sidebar, Stop, the quick suffixes menu, allowing or denying the waiting prompt, and running the project's main action (`Ctrl/Cmd+Shift+E`, [§18](#project-tools)).

- **Rebinding.** Click a shortcut and press the new keys; Esc cancels. **Reset** puts one back, **Remove** clears it, and **Reset to defaults** restores them all.
- **One key for both OSes.** Shortcuts are stored with a *Primary* modifier: Ctrl on Windows and Linux, Cmd on macOS. That way a shortcut synced between a Windows machine and a Mac means the same thing on both. Ctrl is its own modifier only on macOS; elsewhere it is Primary.
- **Refused shortcuts.** A shortcut already used by another command or a quick suffix is refused, and the row names the conflict. So is a letter, digit or punctuation key without Ctrl, Alt or Cmd, since it would get in the way of typing. Escape, Tab, Enter, Backspace, Delete and function keys are allowed on their own.
- **Go to tab 1–9** is one shortcut for all nine digits; rebinding it takes any digit and keeps its modifiers.
- **Fixed keys**, listed on the page but not rebindable: Enter sends and Shift+Enter starts a new line; in the new tab picker and the quick suffixes menu, 1–9 pick an entry.
- **Quick suffixes** each get their own optional shortcut in Settings → Quick suffixes ([§5](#quick-suffixes)), checked for conflicts the same way.
- Tooltips and the composer's placeholder show the current shortcuts.

**Search.** The box above the categories filters settings by name: it lists matching settings with their category, and picking one opens that category. It finds the project's pages too, named with their group ("NightOwl → Links"), by what's on them ("web links", "add an action", "engine", "editor configuration") or by the project's name.

### The project's pages

Below the categories, a divider and a group headed by the selected tab's project: its name when a provider found one ("NightOwl"), otherwise the folder's name, with the folder's path in a tooltip. It has three pages, **Links**, **Actions** and **Tools**, and resolves [issue #9](https://github.com/reapazor/Claudette/issues/9) (adding web links from Settings).

- **Which tab.** The Settings window follows the tab that was selected when it opened (the window is modal, so that can't change while it's open), and each page says so in a small line at the top: "For the tab in D:\Games\NightOwl". With no tab open, the group is hidden. **Open** in another tab's **Tab settings…** selects that tab first.
- **Saving.** Like the rest of Settings, a change is saved as it's made: each add, edit, removal or move rewrites the file it belongs to, and every tab in the folder reads its files again, so the project's menu follows at once. Each page says that saving rewrites the file, so comments in it are dropped. A file that isn't valid JSON is never rewritten: the page shows its reason and the dialogs refuse to save to it. When a save fails, the page says why and goes back to what the files hold.
- **Links.** The links of both files in the order the project's menu shows them, `claudette.json`'s first, each with its name, its address and its file: "Shared (claudette.json)" or "Just me (claudette.local.json)". A link the menu won't open is listed with why, and one that can't be read with its reason (it can only be removed). **Add…**, **Edit…**, **Remove**, **Move up** and **Move down**; a link moves within its own file. **Add a link…** in the project's menu opens Settings on this page with a new link started. The dialog has the name (empty shows the address), the address, and for a new link which file it goes in, just the user's by default. The address is checked as the menu opens links: only `https`, `http` and `mailto`, a full address, and only the placeholders, which a one-line hint explains: `{branch}`, `{changelist}` and `{folderName}` are filled in from the tab ([§18](#project-tools)).
- **Actions.** The actions editor that was in **Tab settings…**: a choice of the two files, and the chosen file's actions with **Add…**, **Edit…**, **Remove**, **Move up**, **Move down** and **Open file**. The one change is that each edit saves as it's made (see **Saving**), where Tab settings waited for Apply. **Add an action…** in the project's menu opens Settings on this page with a new action started, its dialog asking which file it goes in. **Tab settings…** keeps a line, "Project actions are in Settings → NightOwl → Actions", with **Open**.
- **Tools.** This machine's choices for the tab's project, the same ones the project menus make, and only those its provider has: the project, when the folder has several; the per-project choice (Unreal's editor configuration, Development or DebugGame; Unity's code optimization, Release or Debug); and what's picked with **Choose…** (Unreal's engine folder, the Unity editor, the Godot executable), with what's in use now, whether it was chosen or found, and **Clear** to forget the pick and find it again. It shows the project file or folder they're remembered by. Without a project it says "No Unreal, Unity or Godot project in this folder"; either way it links to Settings → Project tools for the defaults every project starts with.

### Version

The foot of the Settings sidebar shows which Claudette this is, on every page: "Claudette 0.1.0".

- **The version** is the one being worked on, set in `Directory.Build.props` and tagged `vX.Y.Z` when it's released ([§2](#updating-claudette)); releases pass it to the build too. The first release is `v0.1.0`.
- **A source build** ([§9](#working-on-claudette)) adds the commit it was built from ("Claudette 0.1.0 · 842169b"), taken from the informational version the .NET SDK writes from the checkout, so a build of a checkout can be told from the release with the same version. Its tooltip also says which configuration it was built in, from the assembly's configuration attribute: "Claudette 0.1.0 (source build 842169b), a Debug build from this checkout." An installed Claudette shows only the version.
- **Clicking it** copies the versions for a bug report, one per line, and it says "Copied" for two seconds: Claudette's version and how it was installed (MSIX, `.dmg`, source build and its commit), Claude Code's version, the OS and its runtime identifier, and the .NET runtime. Only versions: never paths, names or account details, since it's meant to be posted. **Copy diagnostics** ([§16](#staying-tolerant-at-runtime)) starts with the same Claudette line.
- **Report an issue** opens a new issue on Claudette's GitHub repository in the browser, with an outline (what happened, what you expected, steps to reproduce) and the same versions filled in. Nothing is sent until the user submits it there.

### Per-tab overrides

Some settings can be changed for a single tab from the tab's right-click menu, under **Tab settings…**: model, effort level, permission mode, the process monitor ([§4](#process-monitor)), **Auto-continue** (continuing a task when a usage limit resets: Default, On or Off; [§6](#continuing-after-a-limit-resets)), and the check-in settings. The folder's custom project actions belong to the folder, not the tab, so they're edited in Settings, on the tab's **Actions** page ([above](#the-projects-pages)); **Tab settings…** says so and has **Open**. A tab with overrides shows a small dot next to its settings entry, and **Use defaults** clears them. Overrides are saved with the tab.

**Tab settings…** also has **Sync to other machines** ([§9](#session-library-sync-across-machines)) and **Connect to the Claude app** ([§18](#remote-control-the-claude-app)), the same switches as the tab menu's. Neither is an override: the new-tab settings only apply when a tab opens, **Use defaults** leaves them as they are, and they don't count toward the dot.

### Storage

- Claudette's settings are stored as JSON in the app data folder: `%APPDATA%\Claudette\settings.json` on Windows, `~/Library/Application Support/Claudette/settings.json` on macOS, and `~/.config/claudette/settings.json` on Linux.
- Settings files have a version number so later releases can migrate them.
- Claudette's settings are separate from Claude Code's. Claudette doesn't edit `~/.claude/settings.json` or a project's `.claude/` settings except where this document says it does.

### Settings sync (optional)

**Sync settings through the session library** (Settings → Sessions, off by default) keeps Claudette's settings the same on every machine that uses the same library folder ([§9](#session-library-sync-across-machines)).

- **What syncs:** appearance, new-tab defaults, usage settings (thresholds, and continuing when a limit resets), check-ins, quick suffixes, notifications, keyboard shortcuts and process monitor options.
- **What stays on each machine:** whether Claudette starts at login (the OS keeps it, [§9](#starting-at-login)), the path to `claude`, the login shell setting, the Claude app settings, this machine's name, the library folder itself, the diff tool and Settings → Project tools (program paths and installed IDEs differ between machines), recent and favorite folders, folder mappings, pinned tabs, window sizes and positions, the sidebar's and the usage header's collapsed or detailed state, and the Perforce settings (servers, workspaces and stored passwords belong to the machine). A stored Perforce password is never in `settings.json` at all ([§18](#perforce-ticket-handling)). The main window comes back where it was, with its size and maximized state, unless that position is no longer on a screen (a monitor unplugged since), when the OS places it.
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
- All time-based code (burn rate, check-ins, leases, usage retention, update checks, service status checks, sampling) uses .NET's `TimeProvider`. Tests move the clock forward with `FakeTimeProvider` instead of waiting.
- File locations (app data, the session library, Claude Code's config folder) are injected, so tests use temporary folders.

### Test layers

| Layer | What it covers | How | Tokens |
|---|---|---|---|
| Unit | Pure logic: burn rate and projection, check-in timers, usage retention, settings sync merging, suffix composition, diff command templates, recent folders, tab grouping, project identity matching, lease files | Plain unit tests with a fake clock and temporary folders | None |
| Protocol replay | Turning Claude Code's output into events, and what Claudette writes back: messages, interrupts, permission replies, model and effort changes | Recorded stream-json traffic from real sessions, checked in as fixture files and replayed through a fake transport | None |
| Fake CLI | Process handling: launch flags, stdin/stdout, interrupts, crashes, hangs, sign-in failures, `--version` and `doctor` output, child processes for the process monitor | A small `fake-claude` test program that speaks the stream-json protocol, scripted by the prompt and environment variables. Claudette points at it through the "path to `claude`" setting. | None |
| Real CLI, fake model | End to end against the real `claude` binary: real tools, permission prompts, file edits, transcripts and resume | A local mock server that implements the Anthropic Messages API and returns scripted replies. `claude` points at it with `ANTHROPIC_BASE_URL` and a dummy `ANTHROPIC_API_KEY`. | None |
| UI | View models, and views: sidebar, composer, chips, permission cards, meters | View-model tests with no UI; Avalonia.Headless for rendering and input; snapshot tests with Verify. A snapshot is a text form of what a view shows (text, buttons, fields, meters, images by their accessible name, in tree order), not pixels, so it reads the same on every OS and only changes when what the user sees changes. | None |
| Live (opt-in) | What only the real service can confirm: `rate_limits` data, sign-in, real model output | Tests tagged `Live`, excluded by default and run manually before a release | A few cents |

### Protocol fixtures

- Recorded from real sessions by a **record** mode, from a protocol log ([§13](#logging)):
  - `ProtocolRecordingTests` (`RealCli`, against the mock Messages API, so no tokens) and the Live suite write fixtures when `CLAUDETTE_RECORD_FIXTURES` names a folder.
  - A developer session with protocol logging turned on: `dotnet run --project tools/Claudette.Fixtures -- <protocol.log> <fixture.jsonl> --root <project folder>`.
- Before they're checked in, they're cleaned of paths (under the given roots, the home folder, and Claude Code's path-named project folders), emails, account details and session IDs (`session-1`, `session-2`…). The cleaning doesn't cover what prompts or files said, so check a fixture before committing it.
- Stored under `tests/Claudette.Core.Tests/Fixtures/protocol/<claude-code-version>/`. Every fixture must parse with no unknown message types or fields (`MessageParserTests`).
- Scenarios covered (2.1.284):
  - A simple reply with streaming text (`01-mock-basic`).
  - Tool calls: a write allowed through the control protocol (`02`), an edit with its original file (`06`), and accepting edits mid-turn (`03`).
  - Subagents (`12-subagents`: two in parallel, one nested, and a permission prompt from inside one).
  - A permission prompt that is denied (`07-permission-denied`).
  - An interrupt (`08-interrupt`).
  - `rate_limit_event` in its API-key form, with only `status`, in every recording since `07`; and an authentication failure (`signed-out`).
  - `/clear` (`09-clear`), compaction (`10-compact`) and API errors retried (`11-api-retry`, from the mock's `API_ERROR`).
- When a new Claude Code version comes out, recording the same scenarios again and diffing them against the old fixtures shows protocol changes before users hit them.

### Fake CLI (`fake-claude`)

- The prompt picks what the fake does, rather than a scenario file: `ASK_PERMISSION`, `SLOW` (streams for about 10 seconds), `CRASH` (exit code 7), `SPAWN [seconds] [busy]` (a child process that outlives the turn, for the process monitor), `SILENT` (quiet until a message arrives mid-turn, as a check-in does, then answers with a status), `HANG` (ignores everything until interrupted) and `AUTH_FAIL`. Environment variables set its version, sign-in, `doctor` and `update` output, and `get_usage` answers; the header of its `Program.cs` lists them.
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

- A handful of tests using the cheapest settings: Haiku, low effort, one-line prompts (`LiveTests`, tagged `Live`).
- Runs with a separate API key that has a spend limit, not someone's personal subscription: `CLAUDETTE_LIVE_API_KEY`.
- What an API key can't show (`get_usage`'s plan limits and the account) comes from a signed-in Claude Code config folder, `CLAUDETTE_LIVE_CONFIG_DIR`. Those checks make no model calls.
- Each test skips when its variable isn't set. Also used to record new protocol fixtures (`CLAUDETTE_RECORD_FIXTURES`).

### Tools and CI

- xUnit v3, Avalonia.Headless.XUnit, Verify (snapshot testing, `Verify.XunitV3`) and Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`). xunit.v3 stays on 3.2.x until Avalonia.Headless.XUnit supports 4.x, and so Verify.XunitV3 stays on 32.0.x, the last built against it.
- GitHub Actions (`.github/workflows/ci.yml`):
  - A build-and-test job on Windows, macOS and Linux runs everything except `RealCli` and `Live`, for every push and pull request.
  - A second Linux job installs Claude Code and runs the `RealCli` tests.
  - A test that hangs fails its job after 10 minutes, naming the test (`--blame-hang-timeout`), and no job runs longer than 30 minutes.

### Where things are

| Piece | Location |
|---|---|
| Fake transport and replay transport | `tests/Claudette.Core.Tests/Support/` |
| Protocol fixtures | `tests/Claudette.Core.Tests/Fixtures/protocol/2.1.284/`: `01`–`06` and `signed-out` from the spikes, `07`–`11` from `ProtocolRecordingTests`, and `12-subagents` recorded from the mock's `SUBAGENTS` |
| Record mode | `tools/Claudette.Fixtures/` (`ProtocolFixtureWriter`: a protocol log to a cleaned fixture), used by `ProtocolRecordingTests` and `LiveTests` |
| `fake-claude` | `tools/Claudette.FakeClaude/`. Scripted by the prompt (`ASK_PERMISSION`, `SLOW`, `CRASH`, `SPAWN`, `SILENT`, `HANG`, `AUTH_FAIL`, `LIMIT`, `RUN_BASH`, `SUBAGENTS`) and by environment variables, rather than scenario files; see the header of its `Program.cs`. Its sign-in (`auth login`, `auth logout`, the sign-in control requests) is kept in a file in `CLAUDE_CONFIG_DIR`, so a sign-in sticks. Its reply to a message with images names their media types. |
| Mock Messages API | `tools/Claudette.MockApi/`. Runs in-process in tests, or on its own with `dotnet run`. |
| Tests against `fake-claude` and the real CLI | `tests/Claudette.IntegrationTests/`. The real-CLI tests are tagged `RealCli`. |
| View model tests | `tests/Claudette.App.Tests/`. `Support/TabTestHarness.cs` gives a tab a scripted Claude Code connection, a fake clock, a temporary data folder and a fake process tracker. |
| Rendered UI tests | `tests/Claudette.App.UiTests/`: Claudette's own views on Avalonia's headless platform, driven by keyboard and mouse, with the view model tests' harness (on Avalonia's dispatcher) and `*.verified.txt` snapshots (`UiText` turns a view into text). |
| Live suite | `LiveTests` in `tests/Claudette.IntegrationTests/`, tagged `Live` |
| Usage engine tests (parsers, store, burn rate, alerts, poller) | `tests/Claudette.Usage.Tests/`, with recorded `get_usage`, `rate_limit_event` and `/usage` fixtures |
| Process monitor tests | `tests/Claudette.Platform.Tests/`. Some start real process trees on the current OS; the Linux ones skip elsewhere. |
| History, library, leases, settings sync, diffs, git | `tests/Claudette.Core.Tests/{History,Library,Settings,Diffs,Git}`. Git tests use the real `git` in a temporary repo and skip without it. |
| Real-CLI checks of questions and plans | `RealCliTests`, with the mock's `ASK_QUESTION` and `EXIT_PLAN` scripts |
| Real-CLI checks of attachments, `@` mentions and slash commands | `RealCliTests`; the mock records the images that reach it, with a PNG's size |
| Hook callbacks | `HookCallbackTests` (protocol), `PerforceIntegrationTests` (`fake-claude`'s `RUN_BASH`), and `RealCliTests` against the real CLI |
| Perforce | A pretend `p4` (`tests/Claudette.Core.Tests/Support/FakeP4.cs`, also compiled into the App tests), `Perforce*Tests` in the Core and App tests, and a shell-script `p4` for a real pipe in `PerforceIntegrationTests`. Credential stores: `CredentialStoreTests`. |
| Real-CLI checks of subagents and stopping one | `RealCliTests`, with the mock's `SUBAGENTS` and `LONG_AGENT` scripts; the `12-subagents` fixture was recorded from `SUBAGENTS` |
| Login shell environment | `LoginShellTests` in `tests/Claudette.Platform.Tests/LoginShell/`: reading the output, the terminal rule, timeouts and failures with a fake launcher and `FakeTimeProvider`, and real bash, dash, zsh and fish (each where installed) with a made-up `HOME`, never the user's rc files. `UserEnvironmentTests` in Core (the merge, waiting, and the callers) and `LoginShellSettingsTests` in the App tests. |
| Starting at login | `StartAtLoginTests` in the Core tests (which copy starts, the MSIX handover, turned off outside Claudette, tidying at launch) with `FakeLoginItems` (`tests/Claudette.Core.Tests/Support/`, also compiled into the App tests), and `LoginCommandTests`; `LoginItems/` in the Platform tests (the Run value in a test key of its own, on Windows only; the LaunchAgent and autostart files on every OS); `StartAtLoginTests` in the App tests (the switch and `--login`) and `StartAtLoginUiTests` (the minimized start). |
| Service status | `ServiceStatusTests` in the Core tests (the summary, levels and names, dismissals, which API errors count, the feed) and the App tests (the schedule with `FakeTimeProvider`, the banner, the setting), and `ServiceStatusUiTests`. The status page is `FakeHttpHandler` with a real summary from an incident, trimmed, in `tests/Claudette.Core.Tests/Fixtures/status/`. |

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
- Types Claudette has seen and has no use for are skipped without being counted, so Diagnostics only shows what's new: `active_goal` in 2.1.284. Each is listed in `compat/surface.yaml`.
- A line that fails to parse never ends a session. It's logged, and Claudette moves on.
- With protocol logging on, a skipped message appears in the conversation as a collapsed *"Unsupported message from Claude Code"* row that shows the raw JSON.
- Features are detected with the `capabilities` list from `system/init`, not by comparing version numbers.
- Settings → Advanced has a **Diagnostics** page. It shows the Claude Code version, counts of unknown messages and fields seen, whether the login shell's environment was used ([§13](#login-shell-environment)), and whether the computer is being kept awake for tabs connected to the Claude app, or why not ([§18](#remote-control-the-claude-app)), and has **Copy diagnostics** for bug reports.

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
     - The per-tab switch for the process monitor: built 2026-09-29.
     - Running the process code for real: done on Linux 2026-09-29, in CI's Ubuntu job too. macOS still needs a Mac; its code compiles and its `ps` parsing is tested.
     - Resuming a transcript recorded on the other OS ([§9](#session-library-sync-across-machines)).
     - **Choose folder…** and **Unpin and close** for a restored tab whose folder is gone: built 2026-09-29.
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
     - The replacement for the placeholder icon: done 2026-09-29 ([§2](#packaging-and-signing)).
8. **Working on Claudette.** ✅ Built 2026-09-29. A source build runs from a copy of its build output, notices new builds, and restarts into them with every tab, draft and the window as they were, taking its tabs back if the new build doesn't start ([§9](#working-on-claudette)). Checked end to end on Linux under Xvfb: rebuilding while it ran, the automatic restart, and a broken build being refused.
   - **Still to verify on Windows:** rebuilding while a copy runs, which is what the copy is for, and starting the new build from Explorer and from `dotnet run`.
9. **Filling the gaps.** ✅ Built 2026-09-29. What an audit of §2–§16 found still missing:
   - **Sign-in ([§11](#11-sign-in)):** the banner across the tabs mid-session, messages held while signed out and delivered after, resuming failed tabs, a start that fails for a sign-in, **More options** (Console account, SSO), the `claude auth login` fallback, **Try again**, the header's account menu with **Sign out…**, and Sign in / Sign out in Settings → Claude Code.
   - **Composer ([§5](#5-conversation-view)):** `/` and `@` autocomplete, attaching images and files (drop, paste, the attach button) with thumbnails on sent and restored messages, tool icons on tool cards, and **Open diff** on Edit and Write cards.
   - **Tabs ([§4](#4-tabs--sessions)):** a failed tab's row shows its error, a tab whose check-ins go unanswered says *Possibly stuck*, and the info card shows when the session started.
   - **Usage and context ([§6](#6-token-burn-awareness)):** tokens count up during a turn from per-call usage, the context indicator is estimated when `get_context_usage` isn't available, and the Usage panel lists every past session and week.
   - **Sessions ([§9](#9-sessions-history-restore--sync)):** a restored tab whose transcript is gone offers a new session, leases are taken on open and checked for sessions on this machine too, session records keep per-tab overrides, and History searches every prompt.
   - **Settings ([§14](#14-settings)):** **Reset to defaults** everywhere, favorite folders, fonts for the conversation and code, the overrides dot, model and effort lists from Claude Code, and the window's place and size remembered.
   - **Remembered on this machine:** a dismissed update badge and the once-per-version update notification ([§12](#applying-it)).
   - **Diagnostics ([§13](#13-architecture), [§16](#staying-tolerant-at-runtime)):** protocol logging with **Open log folder**, the Diagnostics page with **Copy diagnostics**, and skipped messages shown in the conversation while logging.
   - **Testing ([§15](#15-testing)):** rendered UI tests with Verify snapshots, record mode and fixtures for a denied permission, an interrupt, `/clear`, compaction and API retries, the Live suite (built, not run), and `fake-claude` child processes, quiet and hung turns, sign-in and `get_usage`.
   - **Still to verify:** a real sign-in through both paths with a throwaway account; pasting and dropping from Finder, Explorer and screenshot tools; running the Live suite with a spend-limited key.
10. **Perforce.** ✅ Built 2026-09-29. Ticket handling and the changelist in the tab title ([§18](#18-future-features)), with hook callbacks through `initialize` (confirmed against 2.1.284, [§13](#integration-with-claude-code)) and the OS credential stores.
    - **Still to verify:** a real Perforce server (including SSO and multi-factor), P4V, and the Windows and macOS credential stores in the running app.
11. **Agent map.** ✅ Built 2026-09-29 ([§18](#agent-map)): the Agents page of the side panel and its own window, with live status, activity, running time, tool calls and tokens for each subagent, the prompt it was given and the report it returned, clicking through to its group or its waiting prompt, Stop for one subagent through `stop_task`, the info card's Agents row, and restored tabs replaying the finished tree from the subagents' own transcripts.
    - **Still to verify:** clicking through it in a real window, and `subagent_retry` against real API errors.
12. **Updates, syncing by choice, and the working line.** ✅ Built 2026-09-29.
    - **Updating Claudette ([§2](#updating-claudette)).** An installed Claudette checks its GitHub releases, downloads the package for its platform, checks it, and restarts into it with every tab as it was, through the source builds' handover. Settings → General has the version, the checks and pre-releases.
    - **Per-tab sync ([§9](#session-library-sync-across-machines)).** Syncing to the session library is opt-in per tab: **Sync to other machines** in the tab menu and **Tab settings…**, a sync icon on the tab's row, and **Sync new tabs to the session library** in Settings → Sessions (off by default). Sessions opened from the library keep syncing; a tab that doesn't sync writes nothing to the library and ignores leases.
    - **Working line ([§5](#working-line)).** A twinkling glyph, a fun verb (or what the running tool is doing), the turn's time and tokens above the composer while Claude works, with Claude Code's `spinnerVerbs`, and both options in Settings → Appearance.
    - **Login shell environment ([§13](#login-shell-environment)).** On macOS and Linux, a Claudette not started from a terminal reads the login shell's environment once in the background, and `claude`, git, `p4`, diff tools and Homebrew start with it merged in. **Use my login shell's environment** in Settings → Claude Code (on by default), and a Diagnostics line saying which shell was used, or why not.
    - **The Claude style ([§3](#visual-style)).** Settings → Appearance → Style: Standard (the default) or Claude, the Claude apps' look: their ivory, warm greys and orange, your messages in bubbles on the right, serif replies, a big rounded composer with a round Send arrow, in light and dark, switching without a restart. Code blocks and inline code now follow the light or dark theme in both styles (they were always dark).
      - **Still to verify:** how it looks on a real Windows, macOS and Linux desktop, and next to the Claude iOS app. It has only been rendered headlessly so far.
    - **Version ([§14](#version)).** The foot of the Settings sidebar shows Claudette's version (0.1.0, the first release), with the commit for a source build; clicking it copies the versions for a bug report, and **Report an issue** opens a new GitHub issue with them filled in.
    - **Detailed header ([§6](#detailed-header)).** The header's chevron, or **Detailed usage header** in Settings → Appearance, draws the header taller: charts of the session and the week with the thresholds, projections and a mark where the session crosses the critical threshold, the burn rate, the time to the limit and the busiest tabs. Remembered on this machine.
      - **Still to verify:** how it looks on real screens. So far it has only been rendered headlessly (Skia, light and dark, at several widths). Check it on Windows with Mica, on macOS, on high-DPI displays and with other accent colors.
    - **Conversation and sidebar polish.**
      - A context ring on each tab's row, with **Show context on tab rows** in Settings → Appearance ([§4](#sidebar), [§6](#per-tab-context)).
      - **Copy** on code blocks, user messages and replies, and each message's time on hover, from the clock live and from the transcript when restored ([§5](#copy-and-times)).
      - **Density** in Settings → Appearance: Comfortable or Compact ([§14](#categories)).
    - **Still to verify:** installing an update on a real Windows and Mac, which needs signed packages from a published release; the login shell's environment in a Claudette started from the Dock on macOS and from a desktop launcher on Linux.
13. **Project tools.** ✅ Built 2026-09-29 ([§18](#project-tools)).
    - **Unreal Engine:** finding the project (in the folder, below and above it) and its engine (a parent folder, `LauncherInstalled.dat`, the registry, `Install.ini`, a chosen folder), Launch editor with Development or DebugGame per project, Generate project files, Build editor, Build and launch, Open solution with a chosen IDE (Open in Rider, with the `.uproject`, for Rider), Open latest log, Clean intermediates and Kill all Unreal editors.
    - **Unity:** the editor for the project's version from Unity Hub's folders and lists, Open in Unity with Release or Debug code optimization, EditMode tests with their counts, regenerating the C# solution, the solution and logs, Clean Library, Kill all Unity editors, and the lock file rules.
    - **Godot:** finding the executable (a pick, Settings, the `PATH`, `Godot.app`, Scoop and WinGet) and checking for the .NET build, Open in Godot, Run project, Build C#, Open solution, Clean `.godot` or `.import`, and Kill all Godot editors.
    - **The chip** in the composer bar (since moved to the sidebar's foot, milestone 15), the project's submenu in the tab menu (since removed, milestone 15), the **Project** page with the job's output and Stop, the notification, and `Ctrl/Cmd+Shift+E`.
    - **Runs in the sidebar:** each job gets an entry under its tab's row with its own log, which stays until it's closed, with Stop while it runs.
    - **claudette.json** and **claudette.local.json:** a folder's own actions (shared ones run on a click, without a confirmation) and links, the links in the sidebar (since moved into the project's menu, milestone 15), and the in-app editor.
    - **Settings → Project tools**, and the notes to Claude about Unreal, Unity and Godot projects.
    - **Still to verify:** everything on machines with Unreal, Unity and Godot installed, on Windows, macOS and Linux (see [§18](#project-tools)).
14. **Remote Control.** ✅ Built 2026-09-29 ([§18](#remote-control-the-claude-app)).
    - **The switch:** **Connect to the Claude app** per tab, in its menu and **Tab settings…**, saved with the tab so it reconnects after restarts; **Connect new tabs to the Claude app** in Settings → Claude Code; disabled with the reason for an account that can't use it.
    - **Connecting:** the `remote_control` control request, as SDK hosts send it, right after the session starts and before any prompt, or when the running turn ends; `enabled: false` to disconnect, with a restart on the same session when that fails; the hidden `/remote-control` command as the fallback for a Claude Code without the request.
    - **What the tab shows:** the connection from the answer, `bridge_state` and `worker_shutting_down`; a note with the session's link, a phone icon on the row, the info card's **Claude app** row, and **Open in the Claude app**; prompts answered in the app read *"Answered in the Claude app"*.
    - **Around it:** `CLAUDE_CLIENT_PRESENCE_FILE` for every `claude`, present while Claudette is in front; keeping the computer awake while a tab is connected (Windows, macOS, Linux); the Diagnostics line.
    - **Still to verify** (needs a claude.ai subscription, a phone and the Claude app; nothing here has connected for real):
      - A real connection: the answer's `session_url` and `connect_url`, the `bridge_state` sequence (ready, connected, reconnecting), and the session showing in the Claude app under the tab's name.
      - The connected answer's exact content, and whether the session's title follows a later rename (`rename_session`) or an AI title.
      - A permission prompt, question and plan answered on the phone closing their cards here.
      - Push delivery to the phone, and the presence file holding pushes off while Claudette is in front.
      - `enabled: false` disconnecting cleanly, and a restarted or restored tab reconnecting.
      - Keeping the computer awake on real Windows, macOS and Linux machines (the Windows call and `caffeinate` only run in CI).
15. **Running tasks, service status and project tools follow-ups.**
    - **Service status ([§18](#service-status)).** ✅ Built 2026-09-29. Claude's status from status.claude.com, at launch, every 5 minutes and straight away when a tab's API requests fail on Anthropic's side: a dot before the account name (green, amber, red or grey) with each watched service (Claude Code, the Claude API, claude.ai) in its tooltip, and a banner across the top while an incident concerns them, with **Status page** and **Dismiss**, remembered on this machine. Settings → General → **Show Claude's service status**.
      - **Still to verify:** a real incident seen live in the running app, and how the dot and banner look on real Windows, macOS and Linux desktops (so far rendered headlessly, both styles, light and dark).
    - **Running tasks ([§5](#running-tasks)).** ✅ Built 2026-09-29. A chip in the composer bar counts the work Claude Code keeps going in the background (shell commands, background subagents, Monitor watches, remote agents), and lists each with its icon, running time, **Stop** and a link to its card; the tab's row counts them once the turn is over, and the info card lists them.
      - **Still to verify** against a real Claude Code: the task messages for a backgrounded command, a Monitor watch (whether it sets `is_backgrounded`, and that its events don't end it), a command moved to the background by its timeout, a remote agent and a workflow; `ambient` tasks; and what `/clear` does to running tasks. Only foreground tasks have been recorded so far (the `03` and `12` fixtures).
    - **Project settings ([§14](#the-projects-pages)).** ✅ Built 2026-09-29. Below Settings' categories, a group for the selected tab's project, named after it, with three pages: **Links**, to add, edit, remove and reorder the links of `claudette.json` and `claudette.local.json`, with the address checked as the sidebar opens links (resolves [issue #9](https://github.com/reapazor/Claudette/issues/9)); **Actions**, the actions editor moved from **Tab settings…**, which **Add an action…** now opens; and **Tools**, the project's remembered choices (Unreal's configuration and engine folder, Unity's editor and code optimization, the Godot executable). Found by the search box.
      - **Still to verify:** how the group and pages look on real Windows, macOS and Linux desktops, in both styles and themes; so far they've only been rendered headlessly.
    - **The project's menu moves to the sidebar.** The project chip left the composer's bar, which had grown crowded, for a row at the sidebar's foot ([§18](#project-tools)). ✅ Built 2026-09-29.
    - **Links move into the project's menu ([§18](#project-tools)).** The sidebar's Links section went. The project's row shows for every tab instead, named after the folder when no project is recognized, and its menu lists the links and has **Add a link…**. The tab's menu lost its project submenu, which the row made redundant. ✅ Built 2026-09-29.
    - **Open in Rider ([§18](#project-tools)).** With Rider chosen for solutions, an Unreal project opens in Rider by its `.uproject`, with no project files to generate; engines before 4.25.4 (Windows) or 4.26 keep Open solution ([issue #6](https://github.com/reapazor/Claudette/issues/6)). ✅ Built 2026-09-29.
    - **Claudette's icon, and the icon while tabs work ([§2](#packaging-and-signing), [§10](#10-notifications)).** ✅ Built 2026-09-29. A female Clawd, with a ponytail, replaced the placeholder icon, drawn by `packaging/icon/build-icons.mjs`, which also writes Rider's project icon. While tabs work, the Windows taskbar overlay pulses Claude's spark (the count of tabs needing input comes first) and the macOS Dock icon shows Claudette typing, then waving while a tab needs input; the taskbar button flashes when a tab needs input while Claudette is in the background. Settings → Notifications → **Animate the Dock or taskbar icon while tabs are working**.
      - **Still to verify on real machines:** the spark, the count taking its place and the flash on the Windows taskbar, installed and unpackaged; the Dock animation and the icon coming back on macOS.
    - **Sync now ([§9](#session-library-sync-across-machines)).** A tab that syncs has **Sync now** in its menu, which copies its session to the library straight away, every file again, and says in the conversation how it went; it checks the lease first ([issue #16](https://github.com/reapazor/Claudette/issues/16)). ✅ Built 2026-09-29.
    - **Reviewed files ([§8](#8-file-changes--diff-view)).** A box on each changed file ticks it as reviewed, in both views, until Claude changes the file again; reviewed files are drawn faintly and counted in the panel's summary. The built-in diff view's **Reviewed** button ticks the file and closes the view. Ticks are saved with the tab and synced through the session record. ✅ Built 2026-09-29.
      - **Still to verify:** how the box and the faint rows look on real Windows, macOS and Linux desktops, in both styles and themes; so far they've only been rendered headlessly.
    - **Starting at login ([§9](#starting-at-login)).** Settings → General → **Start Claudette when I log in** starts Claudette minimized at login: the MSIX through its startup task, other Windows builds through the Run key, a LaunchAgent on macOS and an XDG autostart file on Linux. The entry prefers an installed release to other builds, and those to source builds; a source build hands over to the MSIX, whose task only the MSIX can turn on. ✅ Built 2026-09-29.
      - **Still to verify on real machines:** the MSIX's startup task, its activation and the handover to it; the LaunchAgent on macOS; and the minimized start on real Windows, macOS and Linux desktops.
    - **Continuing after a limit resets ([§6](#continuing-after-a-limit-resets)).** When a plan usage limit stops a task, the tab waits for the reset and sends *"Continue from where you left off."*, as Claude Code does in its terminal but not in `-p` runs. A bar over the composer says when, with **Don't continue**; the tab's row and info card say so too. It holds for a reset more than a day away, after three continues in a row that hit the limit again, and for a reset missed by more than 30 minutes (asleep, or Claudette closed), offering **Continue when it resets** or **Continue**. On by default in Settings → Usage, with **Auto-continue** per tab in **Tab settings…**; the wait is saved with the tab. ✅ Built 2026-09-29.
      - **Still to verify** against a real subscription at its limit: the order of the rejected `rate_limit_event` and the failed `result` (either works), `api_error_status` 429 and `terminal_reason` `api_error` on the result, and `rateLimitType` naming the weekly, Opus and Sonnet limits. So far it has been tested against messages shaped from the SDK reference and others' reports; no fixture has been recorded at a real limit.
16. **Later.** New features go in [§18](#18-future-features) first.

## 18. Future Features

Features beyond v1. All six below are built (milestones 10, 11, 13, 14 and 15); new ones go here first, each with a fuller design before it's built.

### Perforce ticket handling

**The problem.**

- In a Perforce workspace, Claude runs `p4` commands through Bash.
- Perforce login tickets expire (often after 12 hours). After that, every `p4` command fails with *"Your session has expired, please login again."*
- Claude can't log in by itself, because `p4 login` asks for a password, so a long-running tab gets stuck.

**The goal.** Claudette keeps each Perforce tab logged in, so Claude can query and use Perforce without stopping. Claude never sees the password.

**Turning it on.** Settings → Perforce → **Keep Perforce logins fresh**, off by default. It applies to tabs started after the change. The code is in `Claudette.Core/Perforce` (the `p4` runner, the ticket keeper, the changelist tracker), `TabViewModel.Perforce.cs` and `Services/PerforceService.cs`.

**Detecting a Perforce workspace.**

- Before a tab starts its `claude`, Claudette runs `p4 -ztag info` in the tab's folder, with `p4 set -q P4PORT` and `p4 set -q P4LOGINSSO`. Perforce resolves the server, user and workspace from its usual sources (`P4CONFIG` files, `p4 set`, `P4ENVIRO`, environment variables), the same way Claude's own `p4` commands will. The output is read tolerantly: unknown fields are ignored.
- The folder is a Perforce workspace when `p4` answers, the client exists (not `*unknown*`) and the folder is under the client's root. Detection holds up the tab's start, so it gives up after 10 seconds; a missing `p4` or an unreachable server just means no Perforce handling.
- The server Claudette shows and logs in to is the P4PORT `p4 set` resolves in the folder. The server's own `serverAddress` from `p4 info` can differ (behind a proxy or broker, or `ssl:1666` with no host), so it's only shown when no P4PORT is set; then `-p` is left out of Claudette's commands and `p4` uses its default, as Claude's do.
- If it is a Perforce workspace:
  - The tab info card shows a **Perforce** row, for example *"matt @ ssl:perforce:1666, ticket expires in 11h"*. Other states read "ticket expired", "not logged in", "server unreachable", "log in yourself (single sign-on or a second factor)", and a failed login adds its reason.
  - Claudette adds a short note to the session with `--append-system-prompt`: the folder is in a Perforce workspace, with its server, user, workspace name and root; use `p4` for source control rather than assuming git; the app keeps the login fresh, so never run `p4 login` or pass a password; and after an expired-session error, wait for the renewal message and retry.
  - The session registers the PreToolUse hook below.

**Keeping the ticket fresh.** A ticket keeper per tab (`PerforceTicketKeeper`) does this, on the injected clock.

- **Ahead of time.** `p4 -ztag login -s` reports whether the ticket is valid and how long it has left (`TicketExpiration`, in seconds; the plain *"User matt ticket expires in 11 hours 59 minutes."* is read too). Claudette checks when the tab's session starts, when each turn starts (alongside the message, not holding it up), and every 15 minutes. When the ticket has expired, doesn't exist, or has less than 30 minutes left (Settings → Perforce), Claudette logs in again.
- **Just in time.** Claudette registers a `PreToolUse` hook for Bash through the `hooks` field of the `initialize` request, with a 5-minute timeout, and Claude Code calls back with a `hook_callback` control request before each Bash command ([§13](#integration-with-claude-code), "Hook callbacks").
  - Only commands that run `p4` are checked, anywhere in the command line (`cd src && p4 edit …` counts); the hook answers the others straight away.
  - A check from the last 5 minutes that found more than the renew-before time left is trusted, so a turn full of `p4` commands doesn't run `p4 login -s` before each one.
  - The hook never changes the command: it always answers `{"continue": true}`, so permission prompts work as before.
  - A hook that times out stops the command without running it (confirmed against 2.1.284). So when a login is still waiting on the user, the hook answers by itself 15 seconds before its timeout, and the command runs and fails; recovery then takes over.
- **Recovery.** If a `p4` command still fails with an expired-session or *"Perforce password (P4PASSWD) invalid or unset."* error (in the Bash tool result's text, `stdout` or `stderr`), Claudette logs in again. It then sends a mid-turn message, the way a message queued during a tool call is sent: *"Perforce login renewed. Retry the last p4 command."* The conversation shows a note saying so.
  - At most one such message per turn, so a command failing for another reason (another user or server on its command line, say) can't loop.
  - If the login can't happen yet (a prompt still open, single sign-on), a note says so, and the message goes once the ticket is valid again, if the turn is still running.
- **One login at a time.** A tab's checks and logins are shared: whoever arrives while one runs waits for it. Logins for the same server and user are serialized across tabs, and a tab re-checks after waiting, so tabs in one workspace log in once.
- **After a failure.** When a login fails or the prompt is cancelled, hooks, recovery and the 15-minute check don't try again for 5 minutes. A new turn does.

**Logging in.**

- Claudette runs `p4 -p <server> -u <user> login` in the tab's folder and writes the password to the command's standard input, then closes it. The password never goes on the command line, where other processes and the process monitor could see it, and it's never logged. (`-p` is left out when no P4PORT is set; see above.)
- *"User matt logged in."* is success. When Perforce refuses a password (*"Password invalid."*) and the source can ask again, the prompt shows the reason, up to three tries.
- Optional setting: request a ticket valid on all hosts (`login -a`).
- Perforce writes the ticket to its own tickets file (`P4TICKETS`) as usual; Claudette doesn't touch that file.

**Where the password comes from** (Settings → Perforce → Password source):

1. **Stored by Claudette** (recommended, and the default): saved in the OS credential store under `perforce/<server>/<user>`. It's never written to `settings.json`, never synced ([§14](#settings-sync-optional)), and never logged. The stores are in `Claudette.Platform/Credentials`, behind `ICredentialStore` in Core:
   - **Windows:** Credential Manager, a generic credential named `Claudette/perforce/…`, kept for this user on this machine (`CRED_PERSIST_LOCAL_MACHINE`, so it doesn't roam), through `CredReadW`, `CredWriteW` and `CredDeleteW`.
   - **macOS:** the Keychain, a generic password with service `Claudette`, through Security.framework's `SecItemAdd`, `SecItemCopyMatching` and `SecItemDelete`.
   - **Linux:** the Secret Service through `secret-tool`, with the secret on its standard input. Without `secret-tool`, Settings says to install it (the `libsecret-tools` package), and passwords can only be asked for.
   - When nothing is stored yet, or Perforce refuses the stored password, the tab asks, with **Save it in <store>** ticked; the password is saved once the login has worked. Settings → Perforce can also save or forget a password for a server and user, filled in from the workspaces the tabs found.
2. **Perforce's own configuration**: `P4PASSWD` from the `P4CONFIG` file, `p4 set` or the environment. Claudette only reads it, with `p4 set -q P4PASSWD` in the tab's folder, and passes it to `p4 login` on standard input. Settings notes that these sources store the password in plain text. If it isn't set, or Perforce refuses it, the tab says so and Claudette doesn't ask for another.
3. **Ask each time**: when a login is needed, the tab shows a password prompt (**Log in** / **Cancel**) and stores nothing. The tab shows "Needs input" and counts in the Dock/taskbar badge, and a notification goes out if the user isn't looking ([§10](#10-notifications)). A `p4` command waits in the hook until the user answers or the hook is about to time out.

**Settings → Perforce** (off by default): turn ticket handling on or off, password source (with the plain-text note for Perforce's configuration), renew-before-expiry time in minutes, all-hosts tickets, **Show changelist on tabs**, the stored password (**Save password** / **Forget password**), and per-folder overrides for server and user (the deepest folder containing the tab's wins). **Reset to defaults** keeps the per-folder overrides and stored passwords. None of it syncs between machines.

**Not covered.** Servers that sign in through SSO (`P4LOGINSSO` set, or the server requiring it) or multi-factor authentication. For those, Claudette doesn't try a password: it adds a note to the conversation and sends a notification, *"Perforce needs you to log in: run p4 login in a terminal, or log in with P4V."*, then checks every minute until the ticket is valid.

**Testing.** No test uses a real Perforce server. A pretend `p4` (`FakeP4`) answers `info`, `set`, `login -s` and `login` from test state, and a shell-script `p4` checks the password crossing a real pipe. The Linux credential store is tested with `secret-tool` faked; the Windows store round-trips for real on Windows, and the macOS one builds its Keychain queries on macOS (the round trip runs only with `CLAUDETTE_TEST_KEYCHAIN=1`, since a locked keychain would ask).

> **Not yet tried for real:** a real Perforce server, P4V, single sign-on, and the Windows and macOS credential stores in the running app.

### Perforce changelist in the tab title

When Claude is working in a specific Perforce changelist, Claudette shows its number. It's always a row in the tab info card ([§4](#4-tabs--sessions)), and optionally (a setting) a badge on the tab itself, for example *"fix login bug · CL 12345"*. It works in every tab, whether or not ticket handling is on, since it only reads Claude's Bash calls (`ChangelistTracker` in Core).

- **Detecting the changelist.** Claudette watches the tab's Bash tool calls and their results, subagents' too, but only commands that run `p4`:
  - Commands that name a changelist with `-c` (as `-c 12345` or `-c12345`): `p4 edit`, `add`, `delete`, `reopen`, `shelve`, `unshelve`, `submit`, `integrate`, `copy`, `merge`, `move`, `rename`, `undo`, `lock` and `resolve`. Also `p4 change -o 12345` and `p4 change 12345`. A `-c` before the subcommand is the client, not a changelist.
  - Output that creates or updates one: *"Change 12345 created."* (or *"… created with 2 open file(s)."*), *"Change 12345 updated."*
  - The most recently used changelist wins. The default changelist isn't shown.
  - A command that failed doesn't count for the changelist it names (it may not exist), but its output still counts.
- **Showing it.**
  - A "Changelist" row in the tab info card: *"CL 12345"*, then *"earlier: CL 12001 · submitted, …"* when the session used several.
  - If **Show changelist on tabs** is on (Settings → Perforce, off by default), also a `CL 12345` badge at the end of the tab's name line in the sidebar. The badge is separate from the name, so renaming the tab doesn't drop it.
- **Actions.** Clicking the badge offers **Copy changelist number** and **Open in P4V**; the ⓘ info card in the composer bar lists every changelist with **Copy** and **Open in P4V**. P4V opens with `p4v -p <server> -u <user> -c <workspace> -cmd "open changelist 12345"`, found on the PATH or where its installers put it.
- **Saved with the tab** (its `changelists` in `state.json`, and in restart snapshots), so a restored tab shows it again.
- **Resetting it.**
  - When the changelist is submitted (*"Change 12345 submitted."*), the badge says `CL 12345 · submitted`. A submit that renumbers it (*"Change 12345 renamed change 12350 and submitted."*) shows the new number.
  - When it's deleted (*"Change 12345 deleted."*), the badge goes away, even if an older changelist is still pending, and the info card no longer lists it. Using another changelist brings the badge back.

### Agent map

✅ Built 2026-09-29.

A live view of what a tab's subagents are doing. When Claude fans work out to several subagents, possibly nested, the conversation shows each one as a collapsed group ([§5](#5-conversation-view)). That's fine for one agent at a time, but hard to follow when several run in parallel.

- **What it shows.** A tree list of the tab's agents: the main agent at the root, and each subagent as a node under the agent that started it, with nesting kept. Nodes start expanded and can be collapsed.
- **Each row** has two compact lines, following the VS Code extension's compact rows ([§3](#visual-style)):
  - A status mark, the task description, and the running time at the right.
  - The agent type (for example `Explore` or `general-purpose`), then what it's doing right now: its latest tool call, summarized the way the conversation's tool rows are (for example `Grep auth in src/`), or the first line of its latest text. Once it has finished: the first line of its report, or how it ended.
  - Statuses: running (a pulsing dot), waiting on a permission prompt (`!`, and the row is highlighted like the prompt card), done (a green dot), failed (`✕`), stopped (`■`). A background subagent reads "Running in the background".
- **Details.** Selecting a node shows, below the tree:
  - Its status, and its type, model, running time, tool calls and tokens.
  - **The prompt it was given**: the `prompt` input of its `Agent` call, rendered as Markdown, with **Copy**.
  - **What it returned**: the report its parent received, as Markdown with **Copy**, once it has finished.
  - **Show in conversation** (**Go to the prompt** while it waits) and **Stop subagent…**. The same actions, and **Copy prompt** / **Copy result**, are on the row's right-click menu.
  - The prompt and result are one click away rather than always expanded, as in the VS Code extension.
- **Where it lives.**
  - The **Agents** page of the side panel, between Changed files and Processes. The page button shows a busy dot while a subagent runs, and the page's header sums the tree up, for example *"2 agents running (1 waiting on you); 2 done"*.
  - **A window of its own**, from the button at the top of the page: the tree beside the details, for wide fan-outs. One per tab; it closes with the tab.
  - An **Agents** button in the composer bar while the tab has subagents (*"2 agents running"*, or *"Agents (3)"* once they've finished) opens the side panel on the Agents page.
  - An **Agents** row in the tab info card ([§4](#4-tabs--sessions)): for example *"3 agents running"*, *"3 agents running (1 waiting on you)"* or *"4 agents: 3 done, 1 stopped"*. No row while a tab has no subagents.
  - The main agent's row shows whether a turn is running, and its latest tool call.
- **Interaction.**
  - Clicking a node (or Enter) scrolls the conversation to that subagent's group and expands it, along with any groups it's nested in.
  - A node waiting on a permission prompt is highlighted, and clicking it goes to the prompt instead. The prompt card itself names the subagent asking, for example *"Asked by the Explore subagent: Find the auth code"* ([§7](#7-permission-prompts)).
  - **Stop subagent…** stops that subagent, and any subagents it started, after a confirmation. It goes through `stop_task` with the subagent's task id, so Claude is told it was stopped and the rest of the turn carries on. A subagent Claude Code hasn't given a task id offers **Stop turn…** instead, the whole-turn Stop.
- **Running time** comes from the app's clock and ticks every second while an agent runs. When a subagent finishes, the duration Claude Code reports replaces it.
- **Tokens** are the numbers Claude Code reports: from `task_progress` while it runs, then from its result or task notification. They're the subagent's latest request (roughly its context), not a running total, and the details' tooltip says so.
- **Restored tabs.** A transcript replay shows the finished tree, with no live status, running clock or Stop.
  - Claude Code keeps each subagent's own transcript in `<session-id>/subagents/agent-<id>.jsonl`, with a `.meta.json` naming the `Agent` call that started it (`toolUseId`). Claudette merges them into the replay in time order, so restored subagent groups get their tool calls back and nested subagents their place in the tree. The session library already copies that folder ([§9](#session-library-sync-across-machines)).
  - A background subagent's result is read from the `<task-notification>` entry Claude Code recorded for the model. That entry isn't shown as something the user typed, in the tab or in History.
  - A subagent with no recorded result reads *"Didn't finish"*.
- **Decisions.**
  - **A tree list, not a graph layout.** Compact indented rows fit the side panel and read like the rest of the app. The window gives wide fan-outs more room.
  - **Agent teams (teammates) aren't covered.** Claude Code only spawns teammates in interactive sessions: in `-p` mode, as Claudette runs it, "a subagent that Claude names runs as an ordinary subagent even with agent teams enabled" (the agent teams docs), and it shows in the map like any other. Revisit if Claudette ever runs sessions that can have teammates.
- **How it works.** Confirmed against Claude Code 2.1.284 with the mock Messages API, and recorded as the `12-subagents` protocol fixture:
  - Subagent traffic carries `parent_tool_use_id`, the `Agent` call that started it, at every depth, plus `subagent_type` and `task_description`. With `--forward-subagent-text` that includes their text; the subagent's prompt also arrives as a `user` message inside it. Partial `stream_event`s are for the main agent only.
  - `task_started` (`task_type: "local_agent"`) follows each `Agent` call, with `task_id`, `tool_use_id`, `subagent_type`, `is_backgrounded`, `spawn_depth` and the `prompt`. Then come `task_progress` while it works (`usage.total_tokens`, `usage.tool_uses`, `last_tool_name`, and a one-line `description`), and `task_updated` and `task_notification` (`completed`, `failed` or `stopped`, with `summary` and `usage`) when it ends. This is true of foreground subagents too, not only background ones.
  - A permission request from inside a subagent carries `agent_id`, the subagent's task id, and arrives just before the tool call it's for.
  - A top-level foreground subagent's result has `tool_use_result` with `status: "completed"`, `content` (its report), `totalTokens`, `totalToolUseCount`, `totalDurationMs`, `agentType` and `resolvedModel`. Its tool result text wraps the report in a "[Subagent hand-back]" frame with an `agentId` and `<usage>` trailer. A nested subagent's result has only that text, so Claudette reads the report out of the frame, and falls back to the subagent's last text.
  - In `-p` mode, a subagent Claude doesn't explicitly run in the foreground runs in the background: its `Agent` call returns at once with `status: "async_launched"`, and it ends with a `task_notification`, which starts a new turn. Its group in the conversation stays running until then.
  - `stop_task` with a subagent's task id stops it, in the foreground or the background, along with the subagents it started: `task_updated` (`killed`), `task_notification` (`stopped`), and its `Agent` call returns an error marked `tool_result_meta: [{ non_execution_kind: "interrupted" }]`. The turn carries on. The Agent SDK documents `stopTask` for background tasks only, so this is marked undocumented in `compat/surface.yaml`, with the whole-turn Stop as the fallback. An interrupt stops running subagents the same way.
  - `tool_progress` for a foreground subagent: a heartbeat every 30 seconds (`heartbeat: true`), and `subagent_retry` while it waits out an API error, which its row shows as *"Retrying after a rate limit (attempt 2 of 10)…"*.
  - `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`, user configuration that passes through to `claude` ([§13](#integration-with-claude-code)), limits nesting (three layers by default), so on some machines the tree is shallower.
- **Tests.** `AgentMapTests` (the view model, including the recorded fixture replayed through a tab), `ReplayTests`, `TranscriptReaderTests`, `FakeClaudeTests` (`fake-claude`'s `SUBAGENTS` prompt, and stopping a subagent while it waits on a prompt) and `RealCliTests` (the mock's `SUBAGENTS` and `LONG_AGENT` scripts against the real CLI).

### Project tools

✅ Built 2026-09-29.

A tab can do things for the project in its folder: launch the editor, generate project files, build, open the solution. Which things depends on the project. Claudette knows Unreal Engine, Unity and Godot projects; a folder can also have its own actions and links, in a `claudette.json` beside the project.

**Where the actions are.**

- **The project's row** at the sidebar's foot ([§4](#sidebar)), for the selected tab: a cube icon and the project's name and version, for example `NightOwl · UE 5.4 ›`. It's there for every tab, whatever its folder holds, so the first action or link can be added from it. When no provider recognized a project it reads the folder's name, with the folder's path in its tooltip. While a job runs, a busy dot replaces the icon and the job's name replaces the project's (`● Build editor… ›`). In the rail it's the icon alone. A click opens its menu beside the sidebar. (It was a chip in the composer bar at first; it moved here to leave the composer's bar to the conversation's own controls.) Its menu has:
  - a header: the project's name and kind, the engine's version and kind, and the engine's folder, or what's wrong ("The engine for EngineAssociation "5.9" wasn't found on this machine"); without a project, the folder's name and path;
  - entries of `claudette.json` that were skipped, and why;
  - **Projects in this folder**, when there are several, as radio items; the pick is remembered per tab folder on this machine;
  - the project's actions;
  - the project's choice, such as Unreal's **Editor configuration** (Development or DebugGame), as radio items;
  - **Choose engine folder…** (or **Choose another engine folder…**);
  - the folder's custom actions, then its **Links**;
  - **Show output…**, which opens the Project page (only with project tools: a project, custom actions or skipped entries; without them there's no Project page), **Add an action…**, **Add a link…** and **Refresh**.
  - Disabled items say why in their tooltip ("Generate project files first"). The menu looks at the project's files again each time it opens, so it's current.
  - (The tab's menu had the same entries in a submenu until the row showed for every tab, which made it redundant.)
- **The Project page** of the side panel ([§3](#3-main-window)), beside Changed files, Agents and Processes. Its button shows a busy dot while a job runs. It shows:
  - the project's details: its file, the engine's version, folder and kind, the editor target and the configuration;
  - a button per action;
  - the selected run (see **Runs**, below): its status (running, succeeded, failed with its exit code, stopped), **Stop**, which ends the job's whole process tree, and **Copy**;
  - its output, monospace and scrollable, following the newest line. Each run keeps its last 5,000 lines and says how many were dropped.
- **Runs.** Each job is a run: a build, generating project files, a custom action with output, Clean's deletion, or one that couldn't start, whose log says why. Launch and Open actions start a program that runs on its own, with no output, so they don't make one.
  - **In the sidebar**, each run is an entry under its tab's row, newest last ([§4](#sidebar)): its state, its name, and how long it has run or how it ended. Clicking one selects the tab and opens the Project page on its log.
  - **Entries stay** until the user closes them, whatever the result: × on a finished one takes it and its log away. A running one has **Stop** in the × button's place, so a stray click never ends a build.
  - **Which log the page shows:** a run that starts shows itself; clicking an entry shows that one; closing the one showing shows the newest left. With no runs, the page has only the project's details and actions.
  - Closing the tab takes its runs away. They aren't saved: they're gone when Claudette quits or restarts.
- **The keyboard.** **Run the project's main action**, `Ctrl/Cmd+Shift+E` by default and rebindable ([§14](#keyboard-shortcuts)): Launch editor for Unreal, Open in Unity, Open in Godot, else the folder's first custom action.

**How it's built.** `Claudette.Core/ProjectTools`:

- An `IProjectToolProvider` finds its kind of project for a folder and describes it as a `ProjectInfo`: kind, name, root, details, actions, a per-project choice, a fix (**Choose engine folder…**) and the note for Claude.
- A `ProjectAction` has an id, a label, a description and one of four kinds:
  - **Launch**: a program that outlives Claudette, such as the editor. It starts detached, and nothing of it is tracked.
  - **Run**: a long job. It gets an entry in the sidebar with its own log, shown on the Project page; **Stop** ends it, and its end is notified. One job runs at a time per tab; the other jobs are disabled until it ends.
  - **Open**: a file or folder, opened with the OS's app, or for a solution with the IDE chosen in Settings.
  - **Destructive**: confirmed first, such as deleting folders.
- Every command is built by a pure function that takes the OS, so all three OSes' commands are tested on any machine.
- **Detection** runs off the UI thread when a tab opens and before its session starts, when its folder changes, when the project's menu opens, and on **Refresh**. It's cheap: a bounded walk and a few small files.
  - The tab's folder, its subfolders two levels down (for `Game/NightOwl/NightOwl.uproject`), and its parent folders up to the repository's root: a folder with `.git` (a folder, or a worktree's file), `.p4config` or the file `P4CONFIG` names, or six levels up (for a tab opened on `Source/`).
  - It never looks into `Intermediate`, `Saved`, `DerivedDataCache`, `Binaries`, `Content`, `Plugins`, `Source`, `Config`, `Engine`, `Templates`, `Library`, `Temp`, `obj`, `bin`, `node_modules`, hidden folders or links, and at most 2,000 folders.
  - Several projects are listed nearest first: the folder, then its subfolders, then its parents.
- **Jobs** start through `IProcessLauncher` with `TrackProcessTree`, so they show in the process monitor ([§4](#process-monitor)) and **Stop** ends everything they started (UnrealBuildTool starts children). Closing the tab stops a running job with the tab's other processes, unless they're kept.
- **Environment.** Jobs and launches get the user's environment: on macOS and Linux, with the login shell's merged in ([Login shell environment](#login-shell-environment)), so a build finds `dotnet`, the editor or a Homebrew tool on the same `PATH` as a terminal, and a bare program name is looked up on that `PATH`. Claude Code's session variables are removed ([§13](#integration-with-claude-code)), so a command that runs `claude` behaves as it would in a terminal. `ProjectToolEnvironment` is the one place this is built.
- **Remembered on this machine**, in `state.json`: the project a folder uses when it has several, each project's choices (Unreal's configuration and engine folder, keyed by its `.uproject`; Unity's code optimization and editor; Godot's executable). None of it syncs.
- **The note to Claude.** Each provider has its own note, added through `--append-system-prompt` when Settings says so, before Perforce's note when both apply.

**Unreal Engine.**

- **Finding the project.** `*.uproject` files, found as above.
- **The `.uproject`** is JSON, read tolerantly: comments, trailing commas and unknown fields are fine, and a file that isn't JSON still counts as a project.
  - `EngineAssociation` says which engine it uses.
  - The editor target comes from `Source/*Editor.Target.cs`: `<Name>Editor` when there's one, else the first; `<Name>Editor` when there are none.
  - A project with no `Source` targets and no `Modules` has no C++ code: it has no project files to generate, and nothing to build with an installed engine.
  - Enabled `Plugins` are listed on the Project page.
- **Finding the engine** (`UnrealEngineLocator`). An engine only counts when it has `Engine/Build/Build.version`.
  - A folder chosen for the project with **Choose engine folder…** comes first. The pick is checked for `Build.version` (the `Engine` folder itself is accepted too) and remembered per project.
  - **Empty or missing association:** the engine is in a parent folder, the "native" layout. Claudette walks up from the project looking for `Engine/Build/Build.version`.
  - **A version such as `5.4`:** a launcher install. On Windows, `%ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat` (JSON: an `InstallationList` of `AppName` `UE_5.4` and `InstallLocation`), then the registry, `HKLM\SOFTWARE\EpicGames\Unreal Engine\5.4`, value `InstalledDirectory`, in the 64-bit and then the 32-bit view. On macOS, `~/Library/Application Support/Epic/UnrealEngineLauncher/LauncherInstalled.dat`. Linux has no launcher.
  - **Anything else, usually a GUID:** a build registered by UnrealVersionSelector. On Windows, `HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds`, where each value's name is a build's id and its data the engine's folder. On Linux, `~/.config/Epic/UnrealEngine/Install.ini`, and on macOS `~/Library/Application Support/Epic/UnrealEngine/Install.ini`: the `[Installations]` section's `{GUID}=path` lines. GUIDs are compared with and without braces, ignoring case.
  - The registry is read in `Claudette.Platform` (`WindowsUnrealEngineRegistry`), behind `IUnrealEngineRegistry`.
  - **The version** is `Build.version`'s `MajorVersion`, `MinorVersion` and `PatchVersion` (and `BranchName`), shown as "Unreal Engine 5.4.2". The project's row shows `UE 5.4`, from the association when the engine isn't found.
  - **Its kind:** "launcher install", "source build", "installed build" (one with `Engine/Build/InstalledBuild.txt` that isn't the launcher's), "engine in a parent folder", or "chosen by you".
  - **Unreal Engine 4** calls its editor `UE4Editor`; the build scripts are the same.
  - **Not found:** the menu says so, the engine's actions are disabled ("The engine wasn't found: choose its folder in this menu"), and **Choose engine folder…** is offered.
- **Actions.**
  - **Launch editor** (the main action), detached: `Engine/Binaries/Win64/UnrealEditor.exe "<uproject>"`, `Engine/Binaries/Mac/UnrealEditor.app/Contents/MacOS/UnrealEditor "<uproject>"` or `Engine/Binaries/Linux/UnrealEditor "<uproject>"`. DebugGame adds `-debug`, which loads the DebugGame module DLLs, and the label says "Launch editor (DebugGame)". Disabled when the editor isn't built yet.
  - **Generate project files**, a job: `Engine/Build/BatchFiles/Build.bat -projectfiles -project="<uproject>" -game -progress`, or `Mac/Build.sh` or `Linux/Build.sh` under `BatchFiles`. This is what UnrealVersionSelector runs, and `Build.bat` is in launcher installs as well as source builds (`GenerateProjectFiles.bat` is only in source builds). VS Code adds `-vscode`. Visual Studio and Xcode are UnrealBuildTool's defaults on Windows and macOS, so they add nothing, which leaves the user's `BuildConfiguration.xml` in charge.
  - **Build editor**, a job: `Build.bat <EditorTarget> Win64 <Development|DebugGame> -Project="<uproject>" -WaitMutex`, with `Build.sh` and `Mac` or `Linux` elsewhere. A project without code builds `UnrealEditor` with a source build.
  - **Build and launch**: Build editor, then Launch editor if the build succeeded.
  - **Open solution**: `<Name>.sln` for Visual Studio, `<Name> (Mac).xcworkspace` or `<Name>.xcworkspace` for Xcode (a folder, which macOS opens as a document), `<Name>.code-workspace` for VS Code, in the project's folder. Disabled with "Generate project files first" when it's missing. It opens with the OS's app, or the IDE chosen in Settings:
    - Rider: `rider64.exe` on the `PATH`, the Toolbox's `rider.cmd`, or the newest `%ProgramFiles%\JetBrains\JetBrains Rider*\bin\rider64.exe` on Windows; `open -a Rider` on macOS; `rider` or the Toolbox's script on Linux. For Unreal, Rider opens the `.uproject` instead: see **Open in Rider**, below.
    - Visual Studio: the newest `%ProgramFiles%\Microsoft Visual Studio\*\*\Common7\IDE\devenv.exe` on Windows; `open -a "Visual Studio"` on macOS.
    - VS Code: `Code.exe` in `%LOCALAPPDATA%\Programs` or `%ProgramFiles%`, else `code.cmd` on the `PATH`, on Windows; `open -a "Visual Studio Code"` on macOS; `code` on Linux.
    - Another program, given the solution's path.
  - **Open in Rider**, in place of Open solution when Settings → Project tools → Open solutions with is Rider ([GitHub issue #6](https://github.com/reapazor/Claudette/issues/6)): Rider is given the `.uproject`, on every OS. Rider has its own Unreal project model: it reads the `.uproject`, runs UnrealBuildTool's Rider generator itself (JSON files under `Intermediate/ProjectFiles/.Rider`) and builds through UnrealBuildTool, so there are no project files to generate first, and none to regenerate when source files or modules are added. It's always enabled, and **Generate project files**' tip says Rider doesn't need them. There's no separate entry for the `.sln`: the engine programs and mobile targets that Rider's `.uproject` model doesn't cover yet open from Rider's own **File → Open**.
    - Rider reads a `.uproject` from Unreal Engine 4.25.4 on Windows and 4.26 on macOS and Linux. With an older engine, the action stays **Open solution**, as above. An engine whose version isn't known is taken to be new enough.
    - When Rider can't be found or started, the `.uproject` isn't handed to the OS's app, which would start the Unreal editor instead: a note says Rider wasn't found and points at **Another program…** in Settings → Project tools. (On macOS, `open -a Rider` finds Rider wherever it is.)
    - When the IDE isn't found, the OS's app opens it and a note says so.
  - **Open latest log**: `Saved/Logs/<Name>.log`, disabled when there's none yet.
  - **Clean intermediates…**, destructive. It deletes `Binaries` and `Intermediate` in the project, and in each plugin under `Plugins/` (a folder with a `.uplugin`, however deeply nested). Nothing else: not `Saved`, `DerivedDataCache`, `Content` or `Config`.
    - The confirmation lists the folders and their total size. It warns when an editor seems to have the project open: an `UnrealEditor`, `UnrealEditor-Cmd`, `UE4Editor` or `UE4Editor-Cmd` process whose command line names the `.uproject`. Where command lines can't be read, it doesn't guess.
    - The folders are deleted off the UI thread, as a job with its own entry and log, like a build's. Read-only files are made writable first, and links inside are removed without following them.
  - **Kill all Unreal editors…**, destructive. It ends every running Unreal editor, not just this project's, and each one's process tree, which takes ShaderCompileWorker and the like with it.
    - The editors are `UnrealEditor`, `UnrealEditor-Cmd`, `UE4Editor` and `UE4Editor-Cmd` (`.exe` on Windows; on macOS, the executable inside `UnrealEditor.app`).
    - The confirmation says how many are running and lists each with its PID and, when its command line shows it, its project: "End 2 Unreal editors?", "• UnrealEditor (PID 501): NightOwl".
    - It's disabled with "No Unreal editor is running" when none is found. The menu checks each time it opens; where processes can't be listed, it stays enabled and doesn't guess.
- **Processes by name** (`ISystemProcesses` in Core, `SystemProcesses` in `Claudette.Platform`), shared by every provider, with the process monitor's own ways of reading processes: a Toolhelp snapshot and `NtQueryInformationProcess` for command lines on Windows, `/proc` on Linux (the `exe` link, else the first argument, since `comm` is cut to 15 characters), and one `ps` run on macOS, where a program's path can have spaces (`/Users/Shared/Epic Games/…`), so its name is taken from the longest start of the command line that is a file. Claudette itself is never listed. Ending a tree uses .NET's `Process.Kill(entireProcessTree: true)`.
- **Running `.bat` files on Windows.** They run as `cmd.exe /d /s /c ""<bat>" <args>"`: `/d` skips AutoRun, and `/s` takes off only the outer quotes. cmd doesn't read its command line by the rules .NET quotes arguments with, so `CommandLines.BatchFile` builds it and the launcher passes it as it is (`ProcessStartSpec.CommandLine`). An argument is quoted when it has a space or a character cmd treats specially; an option such as `-project=<path>` has only its value quoted, as Unreal's tools write it. An argument with a quote or a line break is refused. `.sh` files run through `/bin/bash`.
- **Telling Claude.** With **Tell Claude about Unreal projects** on (the default), a session that starts in a detected project gets a note through `--append-system-prompt`, before Perforce's note when both apply ([§18](#perforce-ticket-handling)):
  > This is an Unreal Engine 5.4 project, NightOwl, at D:\Games\NightOwl\NightOwl.uproject.
  > The engine is at C:\Program Files\Epic Games\UE_5.4 (a launcher install).
  > To build the editor, run: "C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" NightOwlEditor Win64 Development -Project="D:\Games\NightOwl\NightOwl.uproject" -WaitMutex
  > To regenerate project files, run: "C:\…\Build.bat" -projectfiles -project="D:\Games\NightOwl\NightOwl.uproject" -game -progress
  > Don't start the editor or packaging unless asked.
  - It's about 550 characters for typical paths. The build command uses the project's configuration as the session starts.
  - The tab info card ([§4](#4-tabs--sessions)) gets a **Project** row: "NightOwl: Unreal Engine 5.4.2 · launcher install. Claude was told how to build it."

**Unity.**

- **Finding the project.** A folder with `ProjectSettings/ProjectVersion.txt` and `Assets/`, found as above. Its name is the folder's (the C# solution is named after it); `productName` and `companyName` from `ProjectSettings/ProjectSettings.asset` are shown and name the player log's folder.
- **The version** is `m_EditorVersion` (`2022.3.20f1`), with the changeset from `m_EditorVersionWithRevision` when it's there. The project's row shows `Unity 2022.3`.
- **Finding the editor** for that exact version (`UnityEditors`). Only an editor that exists counts.
  - A pick remembered for the project, from **Choose Unity editor…**: the executable, or a version's folder or `Unity.app` that holds it.
  - Unity Hub's default folder: `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`, `/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity`, `~/Unity/Hub/Editor/<version>/Editor/Unity`.
  - Hub's custom install folder, `secondaryInstallPath.json` (a JSON string) in Hub's config folder: `%APPDATA%\UnityHub`, `~/Library/Application Support/UnityHub` or `~/.config/UnityHub`.
  - Editors added to Hub by hand: `editors-v2.json` (`{ "data": [ { "version", "location" } ] }`) or the older `editors.json` (`{ "<version>": { "version", "location" } }`) in the same folder, read tolerantly; a location can be a string or a list.
  - When the version isn't installed, the project menu's header says so and offers **Choose Unity editor…**.
- **Whether Unity has the project open.** `Temp/UnityLockfile` exists and a `Unity` process's command line names the project. A lock left by a crashed editor doesn't count. Where processes can't be listed, or a Unity process's command line can't be read, the lock file alone counts, since Unity refuses a locked project anyway.
- **Actions.**
  - **Open in Unity** (the main action), detached: `Unity -projectPath "<path>"`. The per-project radio **Code optimization: Release / Debug** (its default from Settings, Release) adds `-debugCodeOptimization` for Debug, and the label says "Open in Unity (Debug)". While the project is open it reads **Unity has this project open**, disabled.
  - **Run EditMode tests**, a job: `Unity -batchmode -projectPath "<path>" -runTests -testPlatform EditMode -testResults "<results>" -logFile -`. Unity quits by itself when the tests are done. The results file is in Claudette's data folder (`project-jobs/`), deleted before each run; when the job ends, its status adds the counts from the NUnit XML: *"Run EditMode tests failed (exit code 2). 11 passed, 1 failed."*
  - **Regenerate the C# solution**, a job: `Unity -batchmode -quit -projectPath "<path>" -executeMethod <method> -logFile -`. The method comes from the IDE package in `Packages/manifest.json`: `com.jetbrains.rider`'s `Packages.Rider.Editor.RiderScriptEditor.SyncSolution`, or `com.unity.ide.visualstudio`'s `Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor.SyncAll`, which a project with the old `com.unity.ide.vscode` package also uses when it has it. Rider's comes first when Settings opens solutions with Rider. Without either package, it's disabled and says why.
  - **Open solution**: `<FolderName>.sln` in the project's folder, disabled when missing, opened like Unreal's with the IDE from Settings.
  - **Open Editor log**: `%LOCALAPPDATA%\Unity\Editor\Editor.log`, `~/Library/Logs/Unity/Editor.log` or `~/.config/unity3d/Editor.log`.
  - **Open Player log**: `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Player.log`, `~/Library/Logs/<company>/<product>/Player.log` or `~/.config/unity3d/<company>/<product>/Player.log`. Disabled when missing.
  - **Clean Library…**, destructive: deletes `Library`, `Temp` and `obj` in the project, and nothing else. The confirmation warns that Unity reimports every asset next time, which can take a long while, and shows the size.
  - **Kill all Unity editors…**, destructive: every `Unity` process (`Unity.exe` on Windows) and its tree, with the project from its `-projectPath`, as Unreal's. Not Unity Hub, and not Unity's own helpers such as `UnityShaderCompiler`, whose names differ.
  - While Unity has the project open, EditMode tests, regenerating the solution and Clean are refused: "Unity has this project open, and locks it while it does: close the editor first."
- **Telling Claude.** With **Tell Claude about Unity projects** on (the default):
  > This is a Unity 2022.3.20f1 project, NightOwl, at /g/NightOwl.
  > The editor is at /opt/Unity/Hub/Editor/2022.3.20f1/Editor/Unity.
  > To run the EditMode tests, with the editor closed, run: /opt/Unity/Hub/Editor/2022.3.20f1/Editor/Unity -batchmode -projectPath /g/NightOwl -runTests -testPlatform EditMode -testResults /g/NightOwl/Logs/EditModeResults.xml -logFile -
  > Library/, Temp/ and obj/ are generated: don't edit them.
  > A .meta file must move and be renamed with its asset.
  > Don't open the editor unless asked.

> **Still to verify with Unity installed:** each Hub layout and file as Hub writes them; the lock file with a real editor; regenerating the solution with each IDE package in batch mode (the methods are the packages' public entry points, but running them with `-executeMethod` hasn't been tried); the results file of `-runTests`; and the logs' places on each OS.

**Godot.**

- **Finding the project.** A folder with `project.godot`, found as above. It's a Godot `ConfigFile`, read line by line and tolerantly:
  - `config_version`: 5 is Godot 4, 4 is Godot 3.
  - `config/name` under `[application]`, else the folder's name.
  - `config/features`, such as `PackedStringArray("4.3", "C#", "Forward Plus")` (`PoolStringArray` in Godot 3): its first version is the project's (`Godot 4.3`), else `config_version` gives `Godot 4` or `Godot 3`.
  - It's a C# project when its features have `C#`, it has a `[dotnet]` or `[mono]` section, or its folder has a `.sln` or `.csproj`.
- **Finding Godot** (`GodotExecutables`). There's no standard install, so in order:
  1. a pick remembered for the project, from **Choose Godot executable…**;
  2. the path in Settings → Project tools;
  3. `godot`, `godot4`, `Godot` or `godot-mono` on the `PATH`;
  4. on macOS, `/Applications/Godot.app` or `/Applications/Godot_mono.app`;
  5. on Windows, Scoop's shims (`~\scoop\shims\godot.exe`, `godot-mono.exe`), WinGet's `Links\godot.exe`, and the newest `Godot*.exe` in WinGet's `GodotEngine.GodotEngine*` package folder, not the console one.
  - **Detect** in Settings runs 3–5 and fills in the path.
  - A C# project needs the .NET ("mono") build of Godot: one whose name has `mono` in it, or with a `GodotSharp` folder beside it (in `Contents/Resources` on macOS). When the Godot found isn't one, the project menu's header says so.
- **Actions.**
  - **Open in Godot** (the main action), detached: `godot --editor --path "<folder>"`.
  - **Run project**, detached: `godot --path "<folder>"`.
  - **Build C#**, a job, for C# projects: `dotnet build "<Name>.sln"`, or the `.csproj` when there's no `.sln`. Disabled until Godot has made them.
  - **Open solution**, for C# projects: the `.sln`, disabled when missing.
  - **Clean .godot…** (Godot 4) or **Clean .import…** (Godot 3), destructive: deletes that folder, with a confirmation that Godot reimports every asset next time.
  - **Kill all Godot editors…**, destructive: every process whose name starts with `godot` (any build, such as `Godot_v4.3-stable_mono_win64`), with the project from its `--path`, never Claudette itself.
- **Telling Claude.** With **Tell Claude about Godot projects** on (the default):
  > This is a Godot 4.3 project (C#), Night Owl, at /g/owl.
  > To check it without the editor, run: /opt/godot-mono --headless --path /g/owl --quit
  > To check one script, run: /opt/godot-mono --headless --path /g/owl --check-only --script res://path/to/script.gd
  > To build the C# code, run: dotnet build '/g/owl/Night Owl.sln'
  > Don't open the editor unless asked.

> **Still to verify with Godot installed:** each place Godot is looked for, the .NET build check, `--check-only` on Godot 3 and 4, and Kill all Godot editors with real editors.

**claudette.json.** A folder's own actions and links are in two files in the tab's folder (only there; they aren't looked for elsewhere):

- `claudette.json` is shared: committed with the project.
- `claudette.local.json` is personal, and belongs in `.gitignore`. It has the same shape, and its entries come after the shared file's.
- Both are read tolerantly, like everything else Claudette reads: comments and trailing commas are fine, unknown fields are ignored, and a bad entry is skipped with a reason, which the project's menu and the Project page show ("claudette.json: actions[2] has no command, so it was skipped."). A file that isn't JSON is skipped whole, with the parser's reason. A missing file means nothing.

```json
{
  "actions": [
    { "name": "Run tests", "command": "dotnet test", "folder": "src", "mode": "output" },
    { "name": "Open Grafana", "command": "start https://grafana.example", "mode": "launch", "os": ["windows"] }
  ],
  "links": [
    { "name": "Board", "url": "https://example.atlassian.net/jira/software/projects/ABC/boards/1" },
    { "name": "Pull request", "url": "https://github.com/org/repo/compare/{branch}?expand=1" }
  ]
}
```

- **Actions.**
  - `name` and `command` are required.
  - `folder` is relative to the tab's folder; the tab's folder by default.
  - `mode` is `output` (the default: a run in the sidebar with its log on the Project page, **Stop**, and the notification) or `launch` (started and left alone).
  - `os` is optional: `windows`, `macos` or `linux`. An action whose `os` leaves out this machine isn't shown.
  - Commands run through the user's shell: `cmd.exe /d /s /c "<command>"` on Windows, `$SHELL -c` (or `/bin/sh -c`) elsewhere. Otherwise they run like built-in actions.
  - An action's id is its file and position, such as `shared:0`, which Stop and the notification use.
  - They're listed in the project's menu after the project's actions, under a separator.
- **Picking up edits.** The files are read again when the tab is selected, when Claudette comes to the front, when a turn ends (Claude may have edited them), when the project's menu opens, and after the in-app editor saves. Reads are numbered as they start, and one that finishes after a later one has been shown is dropped, so quick saves in a row never leave a tab showing an older file (and a detection from before a choice never undoes it). Claudette doesn't watch them with the file system: a watcher per tab runs into the OS's limits (128 inotify instances per user on many Linux machines) and doesn't work on some network drives, and these moments cover when an edit can matter.
- **The in-app editor.**
  - It's in Settings, on the tab's **Actions** page ([§14](#the-projects-pages)); it was in **Tab settings…** until milestone 15, which now points there.
  - **Add an action…** opens Settings on that page with a small dialog: name, command, working folder, **Run with output** or **Launch and forget**, and which file it goes in, **Just me (claudette.local.json)** (the default) or **Shared with the project (claudette.json)**.
  - The page has a choice of the two files, and the chosen file's actions with **Add…**, **Edit…**, **Remove**, **Move up**, **Move down** and **Open file**. Entries for other OSes are listed, marked "only on …", and entries that can't be read are listed with their reason and can only be removed. Like the rest of Settings, each change is saved as it's made.
  - Saving reads the file as JSON, replaces only `actions`, keeps every other key and each entry's other fields (such as `os`), and writes it indented. The page says so: saving rewrites the file, so comments in it are dropped. A file that isn't valid JSON is never overwritten; the editor says to fix it by hand first.
- **No confirmation.** Actions from `claudette.json` run on a click, exactly like those from `claudette.local.json`: the user chose not to be asked first, although a project from someone else can put any command behind a friendly name. Nothing in either file runs on its own: only an explicit click runs an action, and hovering one shows its whole command first.
  - `claudette.local.json` is the user's own file, so its actions run without asking.
  - Nothing from these files runs except on an explicit click or the main action's shortcut: no automatic runs and no hooks.

**Links.**

- **In the project's menu**, the menu of the project's row at the sidebar's foot, under a **Links** heading after the folder's custom actions: the shared file's, then the local file's. The row is there for every tab, so a folder with links and nothing else has them too. (Until milestone 15 they also had a section of the sidebar above History; the menu made it redundant.)
- **Opening.** A click opens the link in the browser (`IPlatformServices.OpenUrlAsync`). Hovering shows the address it opens. Links are marked ↗; favicons would need network requests, so there are none.
- **Allowed schemes.** Only `https`, `http` and `mailto`. Anything else (`file:`, `javascript:`, an app's own scheme) is shown disabled with the reason, and never opened. So is an address that isn't absolute.
- **Placeholders**, filled from the selected tab and URL-escaped: `{branch}` (the git branch), `{changelist}` (the Perforce changelist Claude is working in, [§18](#perforce-changelist-in-the-tab-title)) and `{folderName}`. A link whose placeholder can't be filled right now is disabled and says why ("No git branch").
- **Updates.** Links follow the selected tab, and are read again as its files are.
- **Editing.** Settings has a **Links** page for the selected tab ([§14](#the-projects-pages)): add, edit, remove and reorder the links of either file, with the address checked by the rules above. Saving reads the file as JSON, replaces only `links`, keeps every other key (the actions among them) and each entry's other fields, and never rewrites a file that isn't valid JSON, as the actions editor does. A link saved without a name has none in the file, and the menu shows its address. **Add a link…** in the project's menu opens that page with a new link started.

**Notifications.** When a job finishes or fails and Claudette isn't in front, a notification says so: *"Build editor failed (exit code 6)."* A stopped job doesn't notify. Clicking it selects the tab and opens its Project page on that run's log. Settings → Notifications → **A project action finishes**, on by default ([§10](#10-notifications)).

**Settings → Project tools** ([§14](#14-settings)), kept on each machine like the diff tool:

- Default editor configuration for Unreal: Development (the default) or DebugGame. A project's own choice in the project's menu wins.
- Project files for: Visual Studio, VS Code or Xcode. The default is Visual Studio on Windows, Xcode on macOS and VS Code on Linux.
- Open solutions with: the OS's default app, Rider, Visual Studio, VS Code, or another program (with **Browse…**).
- Tell Claude about Unreal projects: on by default.
- Unity: the default code optimization, Release (the default) or Debug; a project's own choice wins. Tell Claude about Unity projects: on by default.
- Godot: the executable's path, empty to find it, with **Browse…** and **Detect**. Tell Claude about Godot projects: on by default.
- **Reset to defaults**, and search entries for each.

**Testing.** No test runs a real engine: engines are a few files in temporary folders, the registry and running processes are fakes, and every process is a fake launcher's. `ProjectDiscoveryTests`, `UnrealEngineTests`, `UnrealCommandTests`, `CommandLineTests`, `UnrealProviderTests`, `UnityProviderTests`, `GodotProviderTests`, `ProjectJobTests` and `ProjectFileTests` in Core; `SystemProcessesTests` in Platform; `ProjectToolsTests` and `ProjectRunsTests` for the tab; `ProjectSettingsTests` for Settings' project pages; and `ProjectToolsUiTests` and `ProjectSettingsUiTests` for the rendered project row and its menu with the links, the runs in the sidebar, and the project group and Links page in Settings.

> **Still to verify on a machine with Unreal installed:**
> - Launching the editor and building from a launcher install and a source build on Windows, macOS and Linux, including DebugGame and `-debug`.
> - `Build.bat -projectfiles` in an installed build, and the format each OS gets without a switch.
> - The registry entries and `Install.ini` as UnrealVersionSelector writes them.
> - That Stop ends UnrealBuildTool and its compilers, and the process monitor lists them.
> - Opening a `.xcworkspace` and each IDE for real.
> - The editor-open warning, and Kill all Unreal editors, with real editors on each OS (on macOS, reading their command lines through `ps`).

### Remote Control (the Claude app)

✅ Built 2026-09-29.

A tab can be used from the Claude app on a phone, or at claude.ai/code, while Claudette keeps running it on this computer: at home on the desk, say, with the phone on the couch. Claude Code's [Remote Control](https://code.claude.com/docs/en/remote-control) does the work. The tab's `claude` connects out to claude.ai over HTTPS (no inbound ports), and the app shows the conversation, sends messages and answers prompts, while every tool keeps running here. The phone can get a push when a tab needs an answer.

- **Why per tab.** Claude Code gives each `claude` process one remote session, and Claudette runs one process per tab, so each tab is its own session in the app. While a tab is connected, its transcript is stored on Anthropic's servers, so connecting is a choice each tab makes; it's off unless the user turns it on.
- **The switch.**
  - **Connect to the Claude app** in the tab's menu (a check item) and in **Tab settings…** ([§4](#4-tabs--sessions)). It's `TabState.RemoteControl`, saved with the tab and in restart snapshots, so a connected tab reconnects by itself whenever its session starts: restored, resumed, or restarted after a sign-in or into a new build. It's the tab's own state like **Sync to other machines**, not an override, so **Use defaults** leaves it.
  - Turning it on connects straight away, or when the running turn ends; a tab that isn't running connects when it starts. Turning it off disconnects the same way.
  - Turned off while Claude works, the tab says so with a note, *"Disconnecting from the Claude app when Claude finishes this turn."*, and its row's phone icon fades further until Claude Code has closed the connection. Turned back on before the turn ends, it stays connected with the note *"Staying connected to the Claude app."* Turned back on once Claude Code is already closing the connection, it connects again when that's done.
- **Settings → Claude Code → Claude app (Remote Control)**, kept on this machine and not synced, like the rest of the category:
  - **Connect new tabs to the Claude app** (off by default) sets the switch for tabs opened afterwards (the picker, History, `--folder`), like **Sync new tabs**. Turning it on or off never changes open tabs.
  - What it does, the privacy note, and that it needs a claude.ai subscription sign-in.
  - How to get pushes: turn on **Push when actions required** (`inputNeededNotifEnabled`), and if you like **Push when Claude decides** (`agentPushNotifEnabled`), in Claude Code's `/config`, with a link to the docs' [mobile push notifications](https://code.claude.com/docs/en/remote-control#mobile-push-notifications). Claudette doesn't edit `~/.claude/settings.json` for them.
  - **Keep this computer awake while tabs are connected** (on by default; see "Keeping the computer awake" below).
- **An account that can't use it.** From `claude auth status` and the environment Claudette gives `claude`: signed in with an API key or an API key helper, through a cloud provider (`authMethod` `third_party`, or an `apiProvider` other than `firstParty`), or with `ANTHROPIC_BASE_URL` pointing somewhere other than `api.anthropic.com`. The setting and every tab's switch are disabled then, with the reason, and a tab whose switch is on says why and doesn't ask; it can still be turned off. Claude Code checks everything else itself when a tab connects (the plan, an organization's policy, feature flags), and the tab shows its reason.

**How a tab connects.** Confirmed against Claude Code 2.1.284 with the mock Messages API; a real connection hasn't been made yet (below).

- **The request.** As SDK hosts such as the VS Code extension do, Claudette sends the `remote_control` control request right after `initialize`, before any prompt goes out: `{"subtype":"remote_control","enabled":true,"name":"<the tab's name>"}`. The name becomes the session's title in the app.
  - Claude Code runs its eligibility checks, registers the session with claude.ai and answers with `session_url`, `connect_url`, `environment_id`, `bridge_session_id` and `bridge_epoch`. Claudette uses `session_url` (else `connect_url`) and ignores the rest.
  - When it can't connect, the answer is an error with Claude Code's reason. Against the mock that's *"Remote Control is only available when using Claude via api.anthropic.com. ANTHROPIC_BASE_URL is set…"*.
  - `{"subtype":"remote_control","enabled":false}` disconnects, and the session carries on here. If that fails, Claudette restarts the tab's `claude` on the same session (the conversation carries on) with the switch off, so it doesn't connect again.
- **Why not `/remote-control`.** The docs have VS Code users type `/remote-control`, but the extension sends the request above. Sent as a message in `-p` mode, 2.1.284 answers the command locally with *"/remote-control isn't available in this environment."*, whatever the account: the reply's `local_command_outcome.kind` is `unavailable_headless`, "an interactive-panel command this session cannot open". The `--remote-control` flag is accepted and does nothing visible in `-p` mode.
- **Undocumented, so there's a fallback.** A Claude Code that rejects the request as unsupported (*"Unsupported control request subtype: remote_control"*) gets `/remote-control <the tab's name>` instead, as a message of its own, sent only while Claude isn't working so the next turn to end is its answer. It isn't shown as something the user sent, and its reply is shown as a note rather than a reply, read tolerantly: a claude.ai/code address means connected; *isn't available*, *requires*, *disabled* and the like mean not available, with the reply as the reason; anything else counts as connected, with the reply as the note. With 2.1.284 the fallback only says why the tab isn't connected.
- **What Claude Code reports afterwards.**
  - `system/bridge_state` (undocumented), with `state` and `detail`: `ready` and `connected` bring a dropped connection back; `reconnecting` keeps the tab connected, with its icon dimmed; `failed` disconnects it, with the reason; `policy_disabled` makes it not available. A state Claudette doesn't know changes nothing.
  - `system/worker_shutting_down` (documented): a connected tab is disconnected, with its reason (`host_exit`, `remote_control_disabled`…). One that arrives while the tab isn't connected is ignored, since a resumed session can replay old ones.
  - The process exiting disconnects the tab.

**States.** Not connected, Connecting, Connected (with the session's address, when Claude Code gave it) and Not available (with Claude Code's reason). What the tab shows:

- A note in the conversation: *"Connected to the Claude app."* with the session's address as a link, or *"Couldn't connect to the Claude app: <reason>"*, *"Disconnected from the Claude app."* and so on.
- A phone icon on the tab's row while it's connected, dimmed while it connects or reconnects, with the tip *"Connected to the Claude app"* ([§4](#sidebar)). Once the switch is off it's fainter still until the tab has disconnected, with the tip *"Connected to the Claude app. Disconnects when Claude finishes this turn."* while it waits for the turn, then *"Disconnecting from the Claude app…"* (from its handler, Claude Code 2.1.284 archives the session on claude.ai before it answers, which can take a moment).
- A **Claude app** row on the tab info card: *"Connected: <address>"*, *"Connecting…"*, *"Connects when Claude finishes this turn"*, *"Connects when the tab starts"*, or why it isn't connected. A tab that's switched off but still connected adds *"Disconnects when Claude finishes this turn."* or *"Disconnecting…"*.
- **Open in the Claude app** in the tab's menu while it's connected with an address. It opens the session at claude.ai/code in the browser; on a phone, the same link opens the app.
- **Renaming.** A rename still goes to Claude Code as `rename_session` when **Also rename the session in Claude Code** is on ([§13](#integration-with-claude-code)). The docs say the remote title follows `/rename`, but also that a name given when connecting comes first, and the tab gives one; which wins hasn't been seen yet.

**Prompts answered on the phone.** When the app answers a permission prompt, question or plan, Claude Code 2.1.284 withdraws Claudette's copy with a `control_cancel_request` for its `can_use_tool` request (from its source: the app's answer is injected as if Claudette had answered). The card closes, and reads *"Answered in the Claude app"* while the tab is connected and Claudette didn't stop the turn or a subagent itself; otherwise *"No longer needed"*, as before. It isn't an error: the tab's *Needs input* status and notification go. Permission prompts and questions have no deadline; `dialogExpiry` only governs other dialogs Claude Code forwards.

**Pushes and the presence file.** Claude Code pushes to the phone when the user turned that on in `/config` (above). It skips pushes while the file named by `CLAUDE_CLIENT_PRESENCE_FILE` exists, so every `claude` Claudette starts (tabs, the utility session, `auth`, `--version`, `update`) gets it, naming `presence` in the data folder (`AppPaths.PresenceFile`). Claudette creates the file while its main window is active and deletes it when the window isn't, at exit, and at launch (one left by a Claudette that didn't close cleanly). So the phone buzzes only when you're away from Claudette. Claudette's own OS notifications ([§10](#10-notifications)) are unchanged.

**Keeping the computer awake.** A sleeping computer can't be reached from the phone, so while at least one tab is connected and **Keep this computer awake while tabs are connected** is on, Claudette holds off system sleep. The display can still sleep. It lets go when no tab is connected, when the setting is turned off, and at exit. `ISleepBlocker` in Core; the implementations are in `Claudette.Platform/Power`:

- **Windows:** `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` on a thread of its own that lives until Claudette closes, since the state belongs to the thread that set it; `ES_CONTINUOUS` alone clears it.
- **macOS:** `caffeinate -i -w <Claudette's pid>` through `IProcessLauncher`, ended to let go. `-w` ends it with Claudette, even one that crashed.
- **Linux:** `systemd-inhibit --what=sleep --who=Claudette --why="Tabs are connected to the Claude app" --mode=block sleep infinity`, ended to let go. Without `systemd-inhibit`, nothing is kept awake. A helper that stops by itself (logind refusing, say) no longer blocks anything. A Claudette that's killed rather than closed leaves it running, as it does the tabs' processes ([§4](#process-monitor)).
- Diagnostics (Settings → Advanced) says how many tabs are connected and whether the computer is being kept awake, or why not.

**Requirements.** From the docs: a claude.ai subscription (Pro, Max, Team or Enterprise; on Team and Enterprise an Owner turns Remote Control on), signed in through claude.ai with a full-scope login. Not an API key, `ANTHROPIC_AUTH_TOKEN`, a long-lived token from `claude setup-token`, Bedrock, Google Cloud or Foundry, a custom `ANTHROPIC_BASE_URL`, or `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`. Claudette checks what it can know beforehand, and Claude Code's own answer covers the rest.

**Privacy.** While a tab is connected, its transcript (messages, replies and tool activity) is stored on Anthropic's servers, under the same data usage policy. Execution and files stay on this computer. That's why the switch is per tab and off by default, and the setting says so.

**Code.** `Claudette.Core/RemoteControl` (the request and its replies, eligibility, the presence file, `ISleepBlocker`), `ClaudeSession.EnableRemoteControlAsync` and `DisableRemoteControlAsync`, `Services/RemoteControlService.cs` (eligibility, the presence file and keeping awake, shared by the tabs), and `TabViewModel.RemoteControl.cs`.

**Tests.** `RemoteControlProtocolTests` in Core (the wire format, replies, states and eligibility); `SleepBlockerTests` in Platform (fakes, the process mechanics with a harmless `sleep`, and the Windows call and `caffeinate` on their own OS); `RemoteControlTests` for the tab with the scripted transport (the switch, connecting before any prompt, the fallback, `bridge_state`, `worker_shutting_down`, the restart, restoring, eligibility, prompts answered in the app, the presence file and keeping awake); `RemoteControlUiTests` for the rendered menu, row icon, Tab settings and Settings block; and the RealCli tests: the real `claude` answering `remote_control` with its eligibility check and `/remote-control` with *"isn't available"* (`RealCliTests`), and a tab whose switch is turned on ending *Not available* with the reason (`RemoteControlRealCliTests`). No test connects for real.

> **Not yet tried for real** (needs a claude.ai subscription, a phone and the Claude app): a real connection and what its answer and `bridge_state` messages hold; the session's title in the app, and whether it follows renames; prompts answered on the phone closing their cards here; pushes arriving, and the presence file holding them off; disconnecting with `enabled: false`; a restarted or restored tab reconnecting to a new session in the app; and keeping real Windows, macOS and Linux machines awake. A later version could pass `bridge_session_id` back as `reattach_session_id` so a restarted tab keeps its session in the app.

### Service status

✅ Built 2026-09-29.

When Claude itself has trouble, tabs fail in ways that look like Claudette's fault or the user's: retries, overloaded errors, a sign-in that won't finish. Claudette reads Claude's public status page and says so: a dot in the header, and a banner across the top while an incident concerns what Claudette uses.

- **The source.** `https://status.claude.com/api/v2/summary.json`, the page's Atlassian Statuspage v2 summary (status.claude.ai redirects there). A plain `GET` with Claudette's User-Agent (`Claudette/<version>`) through `AppServices.Http`, the client the update check uses ([§2](#updating-claudette)). No sign-in, and nothing else is sent. The fields it reads, tolerantly (unknown fields are ignored, and unknown values read as unknown):
  - `status.indicator`: `none`, `minor`, `major` or `critical` (or `maintenance`).
  - `components[]`: `id`, `name` and `status`: `operational`, `degraded_performance`, `partial_outage`, `major_outage` or `under_maintenance`.
  - `incidents[]`, the unresolved ones: `id`, `name`, `status` (`investigating`, `identified`, `monitoring`, `resolved`, `postmortem`), `impact`, `shortlink`, `updated_at`, and `components` when it names them.
  - `scheduled_maintenances[]`: `id`, `name`, `status` (`scheduled`, `in_progress`, `verifying`, `completed`), `shortlink`, `scheduled_for`, `scheduled_until` and `components`.
  - A failed request, an error status, or an answer that isn't a JSON object is *Status unknown*, never an error dialog.
- **What's watched.** What Claudette relies on: **Claude Code**, the **Claude API** (Claude Code's model calls) and **claude.ai** (sign-in, and [Remote Control](#remote-control-the-claude-app)). Components are matched by name, tolerantly, ignoring case and surrounding spaces: "Claude Code"; a name containing "Claude API" or "api.anthropic.com" (today "Claude API (api.anthropic.com)"); and "claude.ai". Others, such as the Console, Cowork and Claude for Government, don't count.
- **One level**, for the dot:
  - The worst of the watched services: *operational*; *degraded* (`degraded_performance`); *outage* (`partial_outage` or `major_outage`: a partial outage is an outage for whoever it hits); *maintenance* (`under_maintenance`).
  - An open incident that concerns a watched service makes it at least degraded, and maintenance in progress at least maintenance.
  - When none of the watched services is listed (renamed, say), the page's own indicator decides: `none` is operational, `minor` degraded, `major` and `critical` an outage. Anything else is unknown.
- **Which incidents.** Unresolved ones that name a watched component (by name or id), or name no components at all, newest update first. Maintenance counts while it's under way (`in_progress`, `verifying`) on a watched service or names none; scheduled maintenance isn't shown until it starts.
- **When it checks.**
  - At launch, before Claude Code is found or signed in (an incident can be why those fail), then every 5 minutes, on `TimeProvider` timers.
  - Straight away when a tab's request fails on Anthropic's side, at most once a minute: `system/api_retry` with a 5xx `error_status` (529 is an overload) or an `overloaded` or `server_error` category, the same in a subagent's `subagent_retry`, or a turn that ended with a 5xx `api_error_status`. Within a minute of the last check, the next one is brought forward to a minute after it. A rate limit (429), a sign-in problem or a bad request is the user's own, and doesn't count.
  - A failed check backs off: 5 minutes, then 10, 20, and 30 from then on. Meanwhile the dot says *Status unknown*. The first good answer goes back to every 5 minutes.
- **The dot**, at the header's right, before the account name: green when operational, amber when degraded or under maintenance, red for an outage, and grey when unknown or not checked yet.
  - Its tooltip: the level, each watched service and its status, the latest incident's name and status, any maintenance under way, and "as of HH:mm" (the check's time); after a failed check, why it failed.
  - Clicking it opens status.claude.com. Its accessible name says the level.
- **The banner**, across the top under the header, while a watched service isn't operational or an open incident concerns one:
  - *"Claude is having problems: Elevated errors on claude.ai, Claude Code, Claude Cowork and the Claude API (investigating)"*: the latest incident and where it's at, with "and 1 more incident" when there are others. Without an incident, the services that aren't well: *"claude.ai (partial outage)"*.
  - It uses the caution colors, like the usage alert. Maintenance alone gets a quieter banner in the subtle surface colors: *"Claude maintenance in progress: <its name>"*.
  - **Status page** opens the incident's (or maintenance's) short link, else status.claude.com.
  - **Dismiss** hides it until a different incident or maintenance arrives, or the level gets worse. What was dismissed (the ids and the level) is kept with this machine's state (`AppState.DismissedServiceStatus`) and forgotten once everything is operational with nothing open.
  - It goes by itself when everything is operational again; the dot stays, green. A failed check hides it too, since the status is then unknown.
- **The setting.** Settings → General → **Show Claude's service status**, on by default. It's kept on this machine (General doesn't sync). Off hides the dot and banner and stops the checks; on checks straight away.
- **Both styles.** Only existing tokens: `OkTextBrush`, `MeterWarningBrush`, `MeterCriticalBrush` and `MutedTextBrush` for the dot; `CautionBackgroundBrush` and `CautionBorderBrush` for the banner (warm tints of the accent in the Claude style); `SubtleBrush` and `DividerBrush` for the maintenance banner.
- **Not Claude Code's surface.** The status page isn't part of Claude Code, so it isn't in `compat/surface.yaml`; its address and fields are recorded here. The Claude Code fields the tabs read for it (`error_status`, `error_category` and `api_error_status`) are.

**Code.** `Claudette.Core/Status`: `StatusSummary` (the parser), `StatusFeed` (the request), `ServiceStatusReport` (the watched services, the level, the incidents, and `ServiceStatusDismissal`) and `ApiTrouble` (which session events are worth a check). In the app, `Services/ServiceStatusService.cs` (the schedule, the backoff and dismissing), started by `MainWindowViewModel` at launch and told about API errors by `TabViewModel`, and `ViewModels/ServiceStatusViewModel.cs` (the dot and the banner).

**Tests.** `ServiceStatusTests` in Core: the real summary from an incident on 2026-09-29 (trimmed, in `Fixtures/status/`), all operational, maintenance, unknown values and malformed answers, levels and name matching, which incidents count, dismissals, which API errors count, and the request against `FakeHttpHandler`. `ServiceStatusTests` in the App tests, with `FakeTimeProvider`: the check at launch, every 5 minutes, the backoff up to 30, checks on API errors at most once a minute (and from a tab's `api_retry`), the banner shown, dismissed, back for a new incident or a worse level, gone on recovery, maintenance, and the setting. `ServiceStatusUiTests`: the dot's place and tooltip, the banner rendered, **Dismiss**, and the colors in both styles, light and dark. Nothing reaches the network.

## 19. Open Questions

None right now.