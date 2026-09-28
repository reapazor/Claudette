// Spike tool: drive the real `claude` CLI over stream-json stdin/stdout and log every line.
// Usage: node driver.mjs scenarios/<name>.json
// See README.md for the scenario format and placeholders.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const spikesDir = path.dirname(fileURLToPath(import.meta.url));
const scenarioPath = process.argv[2];
if (!scenarioPath) {
  console.error('Usage: node driver.mjs scenarios/<name>.json');
  process.exit(2);
}
const scenarioName = path.basename(scenarioPath, '.json');

// Placeholders available in scenario strings. Paths use forward slashes, which Windows accepts.
const slash = (p) => p.replace(/\\/g, '/');
const root = slash(process.env.SPIKE_ROOT ?? path.join(os.tmpdir(), 'claudette-spikes'));
const vars = {
  SPIKES: slash(spikesDir),
  ROOT: root,
  WORK: `${root}/repo`, // working folder for sessions (a git repo)
  CFG: `${root}/claude-config`, // isolated CLAUDE_CONFIG_DIR for mock scenarios
  AUTHCFG: `${root}/auth-config`, // isolated, signed-out CLAUDE_CONFIG_DIR for sign-in scenarios
  LIB: `${root}/library`, // stand-in for a session library folder
  LOGS: `${root}/logs`,
  MOCK_URL: process.env.MOCK_URL ?? 'http://127.0.0.1:8787',
};
const fill = (v) =>
  typeof v === 'string' ? v.replace(/\$\{(\w+)\}/g, (m, k) => vars[k] ?? m)
  : Array.isArray(v) ? v.map(fill)
  : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, fill(x)]))
  : v;
const cfg = fill(JSON.parse(fs.readFileSync(scenarioPath, 'utf8')));

for (const d of [vars.WORK, vars.CFG, vars.AUTHCFG, vars.LIB, vars.LOGS]) fs.mkdirSync(d, { recursive: true });
if (!fs.existsSync(`${vars.WORK}/.git`)) spawnSync('git', ['init', '-q'], { cwd: vars.WORK, stdio: 'ignore' });

// Preparation steps that run before `claude` starts.
for (const p of cfg.before ?? []) {
  if (p.removeFile) fs.rmSync(p.removeFile, { force: true });
  if (p.writeFile) fs.writeFileSync(p.writeFile.path, p.writeFile.content);
  if (p.copyLatestTranscriptTo) {
    const projects = `${vars.CFG}/projects`;
    const files = fs.existsSync(projects)
      ? fs.readdirSync(projects).flatMap((d) => {
          const dir = `${projects}/${d}`;
          return fs.statSync(dir).isDirectory()
            ? fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl')).map((f) => `${dir}/${f}`)
            : [];
        })
      : [];
    if (!files.length) {
      console.error('No transcript found under', projects, '- run a mock scenario first.');
      process.exit(1);
    }
    files.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
    fs.copyFileSync(files[0], p.copyLatestTranscriptTo);
  }
}

const out = cfg.out ?? `${vars.LOGS}/${scenarioName}.log`;
fs.writeFileSync(out, '');
const t0 = Date.now();
const log = (dir, o) => fs.appendFileSync(out, JSON.stringify({ ms: Date.now() - t0, dir, ...o }) + '\n');
// Timers are unref'd so a finished scenario doesn't keep Node alive until they fire.
const delay = (ms) => new Promise((r) => setTimeout(r, ms).unref());

// Start from a clean environment. When this runs inside Claude Code, inherited CLAUDE_*
// variables (child session, entrypoint, messaging socket) change how the CLI behaves.
const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AI_AGENT)/i.test(k)));
Object.assign(env, cfg.env ?? {});
for (const k of cfg.unsetEnv ?? []) delete env[k];

const args = ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose', ...(cfg.args ?? [])];
log('meta', { scenario: scenarioName, args, cwd: cfg.cwd ?? vars.WORK });
const child = spawn(cfg.claude ?? 'claude', args, { cwd: cfg.cwd ?? vars.WORK, env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });

const waiters = [];
let buf = '';
let reqN = 0;
const lastIds = {};

function send(obj) {
  if (obj.type === 'control_request' && !obj.request_id) obj.request_id = `req_${++reqN}`;
  if (obj.type === 'control_request') lastIds[obj.request.subtype] = obj.request_id;
  log('in', { msg: obj });
  child.stdin.write(JSON.stringify(obj) + '\n');
}

function matches(msg, m) {
  for (const [k, v] of Object.entries(m)) {
    const actual = k.split('.').reduce((o, p) => (o == null ? undefined : o[p]), msg);
    if (actual !== v) return false;
  }
  return true;
}

child.stdout.on('data', (d) => {
  buf += d.toString('utf8');
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { log('out-raw', { line }); continue; }
    const extra = {};
    // Records whether a file already exists when a tool call or permission prompt arrives.
    if (cfg.watchFile && msg.type === 'assistant' && msg.message?.content?.some?.((b) => b.type === 'tool_use')) {
      extra.watchFileExistsAtToolUse = fs.existsSync(cfg.watchFile);
    }
    if (cfg.watchFile && msg.type === 'control_request' && msg.request?.subtype === 'can_use_tool') {
      extra.watchFileExistsAtPermission = fs.existsSync(cfg.watchFile);
    }
    log('out', { msg, ...extra });
    if (msg.type === 'control_request' && msg.request?.subtype === 'can_use_tool' && cfg.autoPermission) {
      const allow = cfg.autoPermission === 'allow';
      const response = allow
        ? { behavior: 'allow', updatedInput: msg.request.input, ...(cfg.updatedPermissions ? { updatedPermissions: cfg.updatedPermissions } : {}) }
        : { behavior: 'deny', message: 'Denied by spike driver' };
      delay(cfg.permissionDelayMs ?? 0).then(() => send({ type: 'control_response', response: { subtype: 'success', request_id: msg.request_id, response } }));
    }
    for (const w of [...waiters]) {
      if (matches(msg, w.m)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(msg); }
    }
  }
});
child.stderr.on('data', (d) => log('stderr', { text: d.toString('utf8') }));

const exited = new Promise((resolve) => child.on('exit', (code, signal) => { log('exit', { code, signal }); resolve(); }));

function waitFor(m, timeoutMs = 60000) {
  return Promise.race([
    new Promise((resolve) => waiters.push({ m, resolve })),
    delay(timeoutMs).then(() => { throw new Error('timeout waiting for ' + JSON.stringify(m)); }),
  ]).catch((e) => { log('error', { error: e.message }); });
}

const user = (text) => ({ type: 'user', message: { role: 'user', content: text }, parent_tool_use_id: null, session_id: '' });

for (const step of cfg.steps ?? []) {
  if (step.sleep) await delay(step.sleep);
  if (step.user) send(user(step.user));
  if (step.control) send({ type: 'control_request', request: step.control });
  if (step.waitFor) {
    const m = { ...step.waitFor };
    if (m['response.request_id'] === '$last') m['response.request_id'] = lastIds[step.lastOf];
    await waitFor(m, step.timeoutMs);
  }
  if (step.signal) { log('meta', { signal: step.signal }); child.kill(step.signal); }
  if (step.closeStdin) child.stdin.end();
}
if (!cfg.keepOpen) child.stdin.end();
await Promise.race([exited, delay(cfg.exitTimeoutMs ?? 15000)]);
if (child.exitCode === null) { log('meta', { note: 'killing after timeout' }); child.kill(); }
console.log(`Log: ${out}`);
