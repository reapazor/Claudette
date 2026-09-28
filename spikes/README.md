# Spikes

Throwaway Node scripts from the milestone 1 spikes (2026-09-28, Claude Code 2.1.284). They answered protocol questions before any app code was written: how to drive `claude` headlessly, where usage data comes from, how sign-in works, and so on.

**The findings live in [DESIGN.md](../DESIGN.md)**, in the sections they affected (§6, §7, §8, §9, §11, §13, §15). These scripts are kept for two reasons:

- **Reference.** They're working examples of the stream-json protocol, including the control messages that aren't documented anywhere else.
- **Re-checking.** When a new Claude Code version changes something (§16), re-run the relevant scenario to see what's different.

They aren't part of the app or the build, and aren't maintained like production code. The .NET test harness replaces them for day-to-day testing:

- `tools/Claudette.MockApi` is a port of `mock-server.mjs`, with the same prompt keywords.
- `tools/Claudette.FakeClaude` stands in for the CLI itself.
- `tests/Claudette.IntegrationTests` runs the real CLI against the mock.

## Requirements

- Node 22 or later.
- `claude` on `PATH`.

## Files

| File | Purpose |
|---|---|
| `mock-server.mjs` | A fake Anthropic Messages API. Point `claude` at it with `ANTHROPIC_BASE_URL` and any API key, and it replies with scripted responses, so sessions cost no tokens. Logs every request it receives. |
| `driver.mjs` | Runs `claude` in headless streaming mode, plays a scenario's steps (messages, control requests, waits), answers permission prompts, and logs every line in both directions. |
| `summarize.cjs` | Prints a driver log as one line per message. Hides partial stream events unless you pass `--all`. Redacts emails and sign-in URL secrets. |
| `auth-login-probe.mjs` | Runs `claude auth login` with no terminal attached, against a signed-out throwaway config, and prints what it outputs. Stops it after 10 seconds. |
| `dump-statusline.cjs` | A status line command that records its input. Used to check whether status lines run in headless mode (they don't). |
| `scenarios/*.json` | The scenarios. Each has a `description` field. |

## Running

```sh
cd spikes
node mock-server.mjs                                 # terminal 1: the fake API on port 8787
node driver.mjs scenarios/02-control-protocol.json   # terminal 2
node summarize.cjs <log path printed by the driver>
```

Everything the scripts create goes under `<OS temp>/claudette-spikes/`: the working repo, config folders, a fake session library, and logs. Set `SPIKE_ROOT` to use a different folder, or `MOCK_URL` if the mock server isn't on port 8787.

## Scenarios

| Scenario | Checks | Cost | Finding |
|---|---|---|---|
| `01-mock-basic` | The `initialize` handshake and one turn against the mock server | Free | The real CLI runs against a mock API with a dummy key (§15) |
| `02-control-protocol` | Permission prompt, `set_model`, `apply_flag_settings` (effort), `get_context_usage`, a message queued during a reply, `interrupt` | Free | §13 wire format and control requests |
| `03-accept-edits-midturn-title` | A write with no prompt, a message sent while a tool runs, `generate_session_title`, `rename_session` | Free | Mid-turn delivery (§5 check-ins), session titles (§13) |
| `04-always-allow` | Allowing with `updatedPermissions` | Free | Claude Code writes the rule to `.claude/settings.local.json` (§7) |
| `05-resume-from-file` | `--resume <path>` on a copied transcript. Run after another mock scenario. | Free | New turns go to `<session-id>.jsonl` next to the given file (§9) |
| `06-edit-original-file` | Read, then Edit, of an existing file | Free | `tool_use_result.originalFile` gives the diff's "before" side (§8) |
| `10-real-usage` | `get_usage`, `/usage`, `rate_limit_event`, status line in headless mode | **Real tokens**: one small Haiku call on your account | §6 usage sources |
| `11-auth-control` | `claude_authenticate` against a signed-out config | Free; never completes sign-in | §11 sign-in flow |

## Safety

- The driver removes inherited `CLAUDE*` environment variables before starting `claude`. When run from inside a Claude Code session, those variables make `claude` act as a child session (see §13).
- Mock scenarios use a throwaway `CLAUDE_CONFIG_DIR`, so they never touch your real `~/.claude`.
- `10-real-usage` uses your real account, with `--no-session-persistence`, so no transcript is saved. Its log contains your usage figures and account details.
- `11-auth-control` and `auth-login-probe.mjs` use a separate signed-out config folder, and never finish signing in. `auth-login-probe.mjs` may open a browser tab to the sign-in page; close it.
- Logs stay under the temp folder. Don't commit them: some include account details.

## Mock server prompts

The mock server picks its reply from keywords in the latest prompt:

| Prompt contains | Reply |
|---|---|
| `WRITE_FILE <path>` | A Write tool call, then "Done with the tool." |
| `EDIT_FILE <path>` | A Read, then an Edit replacing `ORIGINAL LINE` with `EDITED LINE`, then done |
| `RUN_BASH <command>` | A Bash tool call, then done |
| `SLOW` | Text streamed in small chunks over about 20 seconds (for interrupts) |
| anything else | `pong` |

Requests with no tools that mention "title" get `{"title": "Mock session title"}`.

## Scenario format

```jsonc
{
  "description": "What this checks",
  "env": { "NAME": "value" },          // added to the clean environment
  "unsetEnv": ["NAME"],                // removed from it
  "args": ["--model", "claude-haiku-4-5"],  // added after -p and the stream-json flags
  "cwd": "${WORK}",                    // optional, defaults to ${WORK}
  "before": [                          // run before claude starts
    { "removeFile": "${WORK}/a.txt" },
    { "writeFile": { "path": "${WORK}/e.txt", "content": "..." } },
    { "copyLatestTranscriptTo": "${LIB}/copied-session.jsonl" }
  ],
  "watchFile": "${WORK}/a.txt",        // log whether it exists when a tool call or prompt arrives
  "autoPermission": "allow",           // or "deny": how to answer can_use_tool
  "permissionDelayMs": 0,
  "updatedPermissions": [],            // sent with "allow" replies
  "steps": [
    { "control": { "subtype": "initialize", "hooks": null }, "waitFor": { "type": "control_response" } },
    { "user": "hello", "waitFor": { "type": "result" }, "timeoutMs": 60000 },
    { "control": { "subtype": "set_model", "model": "sonnet" },
      "waitFor": { "type": "control_response", "response.request_id": "$last" }, "lastOf": "set_model" },
    { "sleep": 1000 }
  ]
}
```

- `waitFor` matches message fields by dotted path.
- `"$last"` with `lastOf` waits for the reply to the most recent control request of that subtype.
- Placeholders: `${SPIKES}`, `${ROOT}`, `${WORK}` (a git repo), `${CFG}` (the mock scenarios' config folder), `${AUTHCFG}` (a signed-out config folder), `${LIB}`, `${LOGS}` and `${MOCK_URL}`.
