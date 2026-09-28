// Spike tool: print a one-line-per-message summary of a driver log.
// Usage: node summarize.cjs <log> [--all]   (partial stream_event lines are hidden unless --all)
// Sign-in URLs are redacted (state, code_challenge); emails are redacted.
const fs = require('fs');

const file = process.argv[2];
const showAll = process.argv.includes('--all');
const redact = (s) => s
  .replace(/(code_challenge|state)=[^&"]*/g, '$1=<redacted>')
  .replace(/"email":"[^"]*"/g, '"email":"<redacted>"');

for (const line of fs.readFileSync(file, 'utf8').split('\n').filter(Boolean)) {
  const o = JSON.parse(line);
  const m = o.msg || {};
  if (m.type === 'stream_event' && !showAll) continue;
  let s = [o.ms, o.dir, m.type || '', m.subtype || m.response?.subtype || m.request?.subtype || m.event?.type || ''].join(' ');
  if (o.dir === 'stderr') s += ' ' + o.text.slice(0, 300);
  if (o.dir === 'exit') s += ' code=' + o.code + ' signal=' + o.signal;
  if (o.dir === 'error' || o.dir === 'meta') s += ' ' + JSON.stringify(o).slice(0, 300);
  if (m.type === 'result') s += ' ' + JSON.stringify({ is_error: m.is_error, result: m.result, terminal_reason: m.terminal_reason }).slice(0, 300);
  if (m.type === 'assistant') s += ' ' + JSON.stringify(m.message?.content?.map((b) => (b.type === 'text' ? b.text.slice(0, 80) : b.type + ':' + (b.name || '')))).slice(0, 300) + (m.error ? ' error=' + m.error : '');
  if (m.type === 'user' && o.dir === 'out') s += ' ' + JSON.stringify(m.message?.content).slice(0, 200) + (m.tool_use_result ? ' tool_use_result=' + JSON.stringify(m.tool_use_result).slice(0, 300) : '');
  if (m.type === 'control_request' && o.dir === 'out') s += ' ' + JSON.stringify(m.request).slice(0, 500);
  if (m.type === 'control_response' && o.dir === 'out') s += ' ' + JSON.stringify(m.response).slice(0, 400);
  if (m.type === 'control_request' && o.dir === 'in') s += ' ' + JSON.stringify(m.request).slice(0, 200);
  for (const k of Object.keys(o)) if (k.startsWith('watchFile')) s += ' ' + k + '=' + o[k];
  if (m.type === 'system' && m.subtype !== 'init') s += ' ' + JSON.stringify(m).slice(0, 300);
  if (m.type && !['assistant', 'user', 'result', 'system', 'control_request', 'control_response', 'stream_event'].includes(m.type)) s += ' ' + JSON.stringify(m).slice(0, 400);
  console.log(redact(s));
}
