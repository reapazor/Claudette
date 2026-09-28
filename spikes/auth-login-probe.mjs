// Spike tool: run `claude auth login` with no terminal attached and record what it prints.
// Usage: node auth-login-probe.mjs [seconds=10]
// Uses an isolated, signed-out config folder, so your real sign-in is never touched.
// It may open a browser tab to the sign-in page: close it without signing in.
// The command is stopped after the given number of seconds.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const seconds = Number(process.argv[2] ?? 10);
const root = process.env.SPIKE_ROOT ?? path.join(os.tmpdir(), 'claudette-spikes');
const cfgDir = path.join(root, 'auth-config');
fs.mkdirSync(cfgDir, { recursive: true });

const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AI_AGENT)/i.test(k)));
env.CLAUDE_CONFIG_DIR = cfgDir;

const redact = (s) => s.replace(/(code_challenge|state)=[^&"\s]*/g, '$1=<redacted>');
const t0 = Date.now();
const child = spawn('claude', ['auth', 'login'], { env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
child.stdout.on('data', (d) => console.log(`[${Date.now() - t0}ms stdout]`, redact(d.toString())));
child.stderr.on('data', (d) => console.log(`[${Date.now() - t0}ms stderr]`, redact(d.toString())));
child.on('exit', (code, signal) => { console.log('exit', code, signal); process.exit(0); });
setTimeout(() => { console.log(`stopping after ${seconds}s`); child.kill(); }, seconds * 1000);
