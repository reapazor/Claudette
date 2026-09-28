// Claude Code compatibility check (DESIGN.md §16). Node 22+, no dependencies.
//
//   node compat/check.mjs detect [--version X]
//       Is there a Claude Code version newer than `lastTested` in compat/surface.yaml? Prints JSON and, on
//       GitHub Actions, sets the `new` and `version` step outputs. Without --version, uses npm's `latest` tag.
//
//   node compat/check.mjs report --version X [--from Y] [--help-file F] [--tests-file F] [--tests-outcome success|failure] [--out report.md]
//       Writes a Markdown report: changelog entries since lastTested, the Agent SDK type diff, diffs of the docs pages
//       and CLI help against the snapshots in compat/, and every change that mentions an id from surface.yaml.
//       Also writes compat-result.json ({ matches, testsPassed, quiet }).
//
//   node compat/check.mjs update-snapshots [--help-file F]
//       Refreshes compat/docs/*.md (and compat/cli-help.txt from F) after a report has been handled.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const compatDir = path.dirname(fileURLToPath(import.meta.url));
const surfacePath = path.join(compatDir, 'surface.yaml');
const docsDir = path.join(compatDir, 'docs');
const helpSnapshot = path.join(compatDir, 'cli-help.txt');
const MAX_SECTION = 12000; // keep the issue body under GitHub's 65,536 character limit

const [mode, ...rest] = process.argv.slice(2);
const opts = {};
for (let i = 0; i < rest.length; i += 2) opts[rest[i].replace(/^--/, '')] = rest[i + 1];

const surface = readSurface();

switch (mode) {
  case 'detect': await detect(); break;
  case 'report': await report(); break;
  case 'update-snapshots': await updateSnapshots(); break;
  default:
    console.error('Usage: node compat/check.mjs detect|report|update-snapshots [options]');
    process.exit(2);
}

// --- Modes -----------------------------------------------------------------------------------------------

async function detect() {
  const tags = await json('https://registry.npmjs.org/-/package/@anthropic-ai/claude-code/dist-tags');
  const version = opts.version ?? tags.latest;
  const isNew = compareVersions(version, surface.lastTested) > 0;
  const result = { new: isNew, version, lastTested: surface.lastTested, tags };
  console.log(JSON.stringify(result, null, 2));
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `new=${isNew}\nversion=${version}\n`);
  }
}

async function report() {
  const version = opts.version ?? fail('--version is required');
  const from = opts.from ?? surface.lastTested; // --from exists for dry runs against an older version
  const sections = [];

  // 1. Changelog entries after lastTested, up to and including this version.
  const changelog = await text('https://raw.githubusercontent.com/anthropics/claude-code/main/CHANGELOG.md');
  const entries = changelogBetween(changelog, from, version);
  sections.push({ title: `Changelog (${from} → ${version})`, body: entries || '_No changelog entries found._', lang: 'markdown', open: true });

  // 2. Agent SDK type definitions for the two CLI versions.
  const sdk = await json('https://registry.npmjs.org/@anthropic-ai/claude-agent-sdk');
  const sdkFrom = sdkVersionFor(sdk, from);
  const sdkTo = sdkVersionFor(sdk, version);
  let sdkDiff = '';
  if (sdkFrom && sdkTo) {
    const [a, b] = await Promise.all([sdkFrom, sdkTo].map((v) => text(`https://cdn.jsdelivr.net/npm/@anthropic-ai/claude-agent-sdk@${v}/sdk.d.ts`)));
    sdkDiff = diff(a, b, `sdk.d.ts@${sdkFrom}`, `sdk.d.ts@${sdkTo}`);
    sections.push({ title: `Agent SDK types (${sdkFrom} → ${sdkTo})`, body: sdkDiff || '_No changes._', lang: 'diff' });
  } else {
    sections.push({ title: 'Agent SDK types', body: `_Couldn't find Agent SDK versions for Claude Code ${from} and ${version} (found ${sdkFrom ?? 'none'} and ${sdkTo ?? 'none'})._` });
  }

  // 3. Docs pages that surface.yaml points at, against the stored snapshots.
  let docsDiff = '';
  for (const url of surface.docs) {
    const current = await fetchDocPage(url);
    const snapshotPath = path.join(docsDir, docSlug(url));
    const previous = fs.existsSync(snapshotPath) ? fs.readFileSync(snapshotPath, 'utf8') : '';
    if (current !== null && current !== previous) docsDiff += diff(previous, current, `snapshot/${docSlug(url)}`, url + '.md');
  }
  sections.push({ title: 'Docs pages', body: docsDiff || '_No changes to the tracked pages._', lang: 'diff' });

  // 4. CLI help.
  let helpDiff = '';
  if (opts['help-file'] && fs.existsSync(opts['help-file'])) {
    const previous = fs.existsSync(helpSnapshot) ? fs.readFileSync(helpSnapshot, 'utf8') : '';
    helpDiff = diff(previous, fs.readFileSync(opts['help-file'], 'utf8'), 'cli-help.txt', `claude --help (${version})`);
    sections.push({ title: 'CLI help', body: helpDiff || '_No changes._', lang: 'diff' });
  }

  // 5. Matches against surface.yaml ids: in the changelog, and in changed (+/-) diff lines.
  const changedLines = [entries, ...[sdkDiff, docsDiff, helpDiff].map((d) => d.split('\n').filter((l) => /^[+-][^+-]/.test(l)).join('\n'))].join('\n').split('\n');
  const matches = [];
  for (const id of surface.ids) {
    // Whole tokens only: `-p` matches "claude -p" but not "self-paced".
    const pattern = new RegExp(`(?<![\\w-])${id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\w-])`);
    const hits = changedLines.filter((line) => pattern.test(line));
    if (hits.length) matches.push({ id, hits: [...new Set(hits)].slice(0, 5) });
  }

  // 6. Tests.
  const testsPassed = opts['tests-outcome'] ? opts['tests-outcome'] === 'success' : null;
  const testSummary = opts['tests-file'] && fs.existsSync(opts['tests-file'])
    ? fs.readFileSync(opts['tests-file'], 'utf8').split('\n').filter((l) => /Passed!|Failed!|\[FAIL\]|error /.test(l)).slice(0, 60).join('\n')
    : '';

  const quiet = matches.length === 0 && testsPassed !== false;
  const lines = [
    `Claude Code **${version}** is out. Claudette was last tested with **${from}**.`,
    '',
    `- Tests: ${testsPassed === null ? 'not run' : testsPassed ? '✅ passed' : '❌ failed'}`,
    `- Changes mentioning something in \`compat/surface.yaml\`: **${matches.length}**`,
    '',
    '## Matched changes',
    '',
    matches.length
      ? matches.map((m) => `- \`${m.id}\`\n${m.hits.map((h) => `  - \`${h.trim().slice(0, 200).replace(/`/g, "'")}\``).join('\n')}`).join('\n')
      : '_None._',
    '',
  ];
  if (testSummary) lines.push('## Test results', '', '```', truncate(testSummary), '```', '');
  for (const s of sections) {
    lines.push(`<details${s.open ? ' open' : ''}><summary><b>${s.title}</b></summary>`, '');
    lines.push(s.lang ? `\`\`\`${s.lang}\n${truncate(s.body)}\n\`\`\`` : s.body, '', '</details>', '');
  }
  lines.push(
    '## Handling this report (DESIGN.md §16)',
    '',
    '1. Read the matched changes and any failing tests. Re-run the relevant scenario in `spikes/` to see protocol changes.',
    '2. Update the code, `compat/surface.yaml` and the protocol fixtures.',
    `3. Bump \`lastTested\` in \`compat/surface.yaml\` and \`ClaudeLocator.LastTestedVersion\` to ${version}.`,
    '4. Refresh the snapshots: `node compat/check.mjs update-snapshots --help-file <claude --help output>`.',
  );

  const out = opts.out ?? 'report.md';
  fs.writeFileSync(out, lines.join('\n') + '\n');
  fs.writeFileSync('compat-result.json', JSON.stringify({ version, matches: matches.length, testsPassed, quiet }, null, 2));
  console.log(`Wrote ${out}: ${matches.length} matched change(s), tests ${testsPassed === null ? 'not run' : testsPassed ? 'passed' : 'failed'}.`);
}

async function updateSnapshots() {
  fs.mkdirSync(docsDir, { recursive: true });
  for (const url of surface.docs) {
    const page = await fetchDocPage(url);
    if (page !== null) {
      fs.writeFileSync(path.join(docsDir, docSlug(url)), page);
      console.log('updated', docSlug(url));
    }
  }
  if (opts['help-file']) {
    fs.copyFileSync(opts['help-file'], helpSnapshot);
    console.log('updated cli-help.txt');
  }
}

// --- Helpers ---------------------------------------------------------------------------------------------

/** Reads the parts of surface.yaml the check needs: versions, ids and docs URLs. */
function readSurface() {
  const yaml = fs.readFileSync(surfacePath, 'utf8');
  const field = (name) => yaml.match(new RegExp(`^\\s*${name}:\\s*([0-9.]+)`, 'm'))?.[1];
  const unquote = (s) => s.trim().replace(/^"(.*)"$/, '$1');
  return {
    lastTested: field('lastTested') ?? fail('surface.yaml has no lastTested'),
    ids: [...yaml.matchAll(/^\s*- id:\s*(.+)$/gm)].map((m) => unquote(m[1])),
    docs: [...new Set([...yaml.matchAll(/^\s*docs:\s*(\S+)/gm)].map((m) => m[1]))],
  };
}

function compareVersions(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d) return Math.sign(d);
  }
  return 0;
}

/** The changelog's "## X.Y.Z" sections with from < X.Y.Z <= to. */
function changelogBetween(changelog, from, to) {
  const parts = changelog.split(/^## /m).slice(1);
  return parts
    .filter((p) => {
      const v = p.split('\n')[0].trim();
      return /^\d+\.\d+\.\d+$/.test(v) && compareVersions(v, from) > 0 && compareVersions(v, to) <= 0;
    })
    .map((p) => '## ' + p.trim())
    .join('\n\n');
}

/** The newest Agent SDK version that bundles the given Claude Code version. */
function sdkVersionFor(registryDoc, cliVersion) {
  return Object.entries(registryDoc.versions ?? {})
    .filter(([, meta]) => meta.claudeCodeVersion === cliVersion)
    .map(([v]) => v)
    .filter((v) => !v.includes('-'))
    .sort(compareVersions)
    .pop();
}

function docSlug(url) {
  return url.replace(/^https?:\/\/[^/]+\/docs\/(en\/)?/, '').replace(/[^a-zA-Z0-9-]+/g, '__') + '.md';
}

async function fetchDocPage(url) {
  try {
    return await text(url + '.md');
  } catch (e) {
    console.warn(`Couldn't fetch ${url}.md: ${e.message}`);
    return null;
  }
}

/** A unified diff, using git so no dependencies are needed. */
function diff(a, b, labelA, labelB) {
  if (a === b) return '';
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'compat-'));
  const fa = path.join(dir, 'a');
  const fb = path.join(dir, 'b');
  fs.writeFileSync(fa, a);
  fs.writeFileSync(fb, b);
  const r = spawnSync('git', ['diff', '--no-index', '--no-color', '--unified=2', `--src-prefix=${labelA}:`, `--dst-prefix=${labelB}:`, fa, fb], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  fs.rmSync(dir, { recursive: true, force: true });
  return (r.stdout ?? '').split('\n').filter((l) => !l.startsWith('diff --git') && !l.startsWith('index ')).join('\n').trim();
}

function truncate(s) {
  return s.length > MAX_SECTION ? s.slice(0, MAX_SECTION) + `\n… (${s.length - MAX_SECTION} more characters)` : s;
}

async function text(url) {
  const res = await fetch(url, { headers: { 'user-agent': 'claudette-compat-check' } });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} for ${url}`);
  return res.text();
}

async function json(url) {
  return JSON.parse(await text(url));
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
