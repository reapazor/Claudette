// Spike tool: a status line command that records the JSON Claude Code passes to it.
// Used by scenarios/10-real-usage.json to check whether status line commands run in headless mode
// (they don't, as of Claude Code 2.1.284). Appends to <SPIKE_ROOT>/logs/statusline-input.log.
const fs = require('fs');
const os = require('os');
const path = require('path');

const root = process.env.SPIKE_ROOT || path.join(os.tmpdir(), 'claudette-spikes');
let input = '';
process.stdin.on('data', (c) => (input += c)).on('end', () => {
  fs.mkdirSync(path.join(root, 'logs'), { recursive: true });
  fs.appendFileSync(path.join(root, 'logs', 'statusline-input.log'), JSON.stringify({ t: Date.now(), input }) + '\n');
  process.stdout.write('ok');
});
