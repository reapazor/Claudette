// Writes a Markdown summary of the .trx files under a folder: counts per test assembly, then each failed test with the
// start of its message. CI appends it to the job's summary page. Node 20+, no dependencies.
//
//   node .github/scripts/test-summary.mjs <folder> [title]
import fs from 'node:fs';
import path from 'node:path';

const [folder = 'TestResults', title = 'Tests'] = process.argv.slice(2);
const files = fs.existsSync(folder) ? walk(folder).filter((f) => f.endsWith('.trx')) : [];
const lines = [`### ${title}`, ''];

if (files.length === 0) {
  lines.push('_No test results: the run stopped before any tests finished._');
} else {
  const rows = [];
  const failures = [];
  for (const file of files) {
    const xml = fs.readFileSync(file, 'utf8');
    const counters = attributes(xml.match(/<Counters\b[^>]*>/)?.[0] ?? '');
    const codeBase = xml.match(/<TestMethod\b[^>]*codeBase="([^"]+)"/)?.[1];
    const name = codeBase ? path.basename(codeBase.replace(/\\/g, '/'), '.dll') : path.basename(file, '.trx');
    const passed = Number(counters.passed ?? 0);
    const failed = Number(counters.failed ?? 0) + Number(counters.error ?? 0) + Number(counters.timeout ?? 0) + Number(counters.aborted ?? 0);
    const skipped = Number(counters.total ?? 0) - Number(counters.executed ?? 0);
    rows.push({ name, passed, failed, skipped });
    // Passed results are usually empty elements (<UnitTestResult ... />); failed ones hold the message.
    for (const match of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
      const result = attributes(match[1]);
      if (result.outcome !== 'Failed') continue;
      const message = decode((match[2] ?? '').match(/<Message>([\s\S]*?)<\/Message>/)?.[1] ?? '').trim();
      failures.push({ name, test: decode(result.testName ?? '?'), message });
    }
  }
  rows.sort((a, b) => a.name.localeCompare(b.name));
  lines.push('| Assembly | Passed | Failed | Skipped |', '| --- | ---: | ---: | ---: |');
  for (const r of rows) lines.push(`| ${r.name} | ${r.passed} | ${r.failed ? `**${r.failed}**` : 0} | ${r.skipped} |`);
  if (failures.length) {
    lines.push('', `#### Failed (${failures.length})`, '');
    for (const f of failures.slice(0, 50)) {
      const text = f.message.split('\n').slice(0, 8).join('\n').slice(0, 1200).replace(/```/g, "'''");
      lines.push(`<details><summary><code>${escape(f.test)}</code> (${f.name})</summary>`, '', '```', text, '```', '', '</details>');
    }
    if (failures.length > 50) lines.push('', `… and ${failures.length - 50} more.`);
  }
}
console.log(lines.join('\n'));

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));
}

function attributes(tag) {
  return Object.fromEntries([...tag.matchAll(/(\w+)="([^"]*)"/g)].map((m) => [m[1], m[2]]));
}

function decode(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&#xD;/g, '').replace(/&#xA;/g, '\n').replace(/&amp;/g, '&');
}

function escape(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
