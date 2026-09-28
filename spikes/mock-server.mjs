// Spike tool: a minimal fake Anthropic Messages API, so the real `claude` CLI can be driven without tokens.
// Usage: node mock-server.mjs [port] [logfile]
//
// Point `claude` at it with ANTHROPIC_BASE_URL=http://127.0.0.1:<port> and any ANTHROPIC_API_KEY.
// Replies are scripted from keywords in the latest prompt:
//   WRITE_FILE <path>   one Write tool call, then "Done with the tool."
//   EDIT_FILE <path>    a Read, then an Edit replacing "ORIGINAL LINE" with "EDITED LINE", then done
//   RUN_BASH <command>  one Bash tool call, then done
//   SLOW                a text reply streamed in small chunks over ~20 seconds (for interrupts)
//   (title requests)    requests with no tools that mention "title" get {"title": "Mock session title"}
//   anything else       "pong"
// Every request is logged (API keys redacted) so you can see what Claude Code sent.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const port = Number(process.argv[2] ?? 8787);
const root = process.env.SPIKE_ROOT ?? path.join(os.tmpdir(), 'claudette-spikes');
fs.mkdirSync(path.join(root, 'logs'), { recursive: true });
const logFile = process.argv[3] ?? path.join(root, 'logs', 'mock-requests.log');
fs.writeFileSync(logFile, '');
const log = (o) => fs.appendFileSync(logFile, JSON.stringify({ t: Date.now(), ...o }) + '\n');

let msgCounter = 0;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const newId = () => `toolu_mock_${++msgCounter}`;

function lastUserText(body) {
  const msgs = body.messages ?? [];
  for (let i = msgs.length - 1; i >= 0; i--) {
    const m = msgs[i];
    if (m.role !== 'user') continue;
    const c = typeof m.content === 'string' ? [{ type: 'text', text: m.content }] : m.content;
    const hasToolResult = c.some((b) => b.type === 'tool_result');
    const text = c.filter((b) => b.type === 'text').map((b) => b.text).join('\n');
    return { text, hasToolResult };
  }
  return { text: '', hasToolResult: false };
}

// Decide the scripted reply: { kind, blocks, chunkDelayMs? }.
function script(body) {
  const sys = JSON.stringify(body.system ?? '');
  const { text, hasToolResult } = lastUserText(body);
  const tools = (body.tools ?? []).map((t) => t.name);

  // Side requests such as title generation have no tools.
  if (!tools.length && /title/i.test(sys + text)) {
    return { kind: 'title', blocks: [{ type: 'text', text: '{"title": "Mock session title"}' }] };
  }

  // EDIT_FILE needs two tool rounds (Claude Code requires a Read before an Edit),
  // so count the tool rounds since the prompt that asked for it.
  const msgs = body.messages ?? [];
  let lastPrompt = -1;
  for (let i = msgs.length - 1; i >= 0; i--) {
    const c = msgs[i].content;
    if (msgs[i].role === 'user' && (typeof c === 'string' || c.some((b) => b.type === 'text' && /EDIT_FILE|WRITE_FILE|RUN_BASH|SLOW|hello/.test(b.text)))) { lastPrompt = i; break; }
  }
  const promptText = lastPrompt >= 0 ? JSON.stringify(msgs[lastPrompt].content) : '';
  const toolRounds = msgs.slice(lastPrompt + 1).filter((m) => m.role === 'user').length;
  const e = promptText.match(/EDIT_FILE (\S+?)(\\|")/);
  if (e && tools.includes('Edit')) {
    const file_path = e[1].replace(/\\\\/g, '\\');
    if (toolRounds === 0) return { kind: 'tool-read', blocks: [{ type: 'tool_use', id: newId(), name: 'Read', input: { file_path } }] };
    if (toolRounds === 1) return { kind: 'tool-edit', blocks: [{ type: 'tool_use', id: newId(), name: 'Edit', input: { file_path, old_string: 'ORIGINAL LINE', new_string: 'EDITED LINE' } }] };
  }

  if (hasToolResult) return { kind: 'after-tool', blocks: [{ type: 'text', text: 'Done with the tool.' }] };
  const w = text.match(/WRITE_FILE (\S+)/);
  if (w && tools.includes('Write')) {
    return { kind: 'tool', blocks: [{ type: 'tool_use', id: newId(), name: 'Write', input: { file_path: w[1], content: 'hello from mock\n' } }] };
  }
  const b = text.match(/RUN_BASH (.+)/);
  if (b && tools.includes('Bash')) {
    return { kind: 'tool', blocks: [{ type: 'tool_use', id: newId(), name: 'Bash', input: { command: b[1], description: 'mock command' } }] };
  }
  if (/SLOW/.test(text)) return { kind: 'slow', blocks: [{ type: 'text', text: 'slow '.repeat(40) }], chunkDelayMs: 500 };
  return { kind: 'text', blocks: [{ type: 'text', text: 'pong' }] };
}

function sse(res, event, data) {
  res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
}

const server = http.createServer(async (req, res) => {
  let raw = '';
  for await (const chunk of req) raw += chunk;
  let body = {};
  try { body = raw ? JSON.parse(raw) : {}; } catch { /* not JSON */ }
  const url = req.url ?? '';
  const summary = {
    method: req.method, url,
    model: body.model, stream: body.stream, max_tokens: body.max_tokens,
    bodyKeys: Object.keys(body), thinking: body.thinking, output_config: body.output_config,
    tools: (body.tools ?? []).map((t) => t.name).slice(0, 40),
    nMessages: body.messages?.length,
    lastUser: lastUserText(body).text.slice(0, 200),
    systemStart: JSON.stringify(body.system ?? '').slice(0, 300),
    headers: Object.fromEntries(Object.entries(req.headers)
      .filter(([k]) => /anthropic|x-api-key|authorization|user-agent|x-app/i.test(k))
      .map(([k, v]) => [k, /key|authorization/i.test(k) ? '<redacted>' : v])),
  };

  // Anything other than the Messages API: answer token counting, 404 the rest.
  if (!url.startsWith('/v1/messages') || url.startsWith('/v1/messages/count_tokens')) {
    log({ unhandled: true, ...summary });
    if (url.startsWith('/v1/messages/count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ input_tokens: 100 }));
    }
    res.writeHead(404, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ type: 'error', error: { type: 'not_found_error', message: 'mock: not implemented' } }));
  }

  const plan = script(body);
  log({ ...summary, reply: plan.kind });
  const id = `msg_mock_${++msgCounter}`;
  const usage = { input_tokens: 1000, output_tokens: 20, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 };
  const stopReason = plan.blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn';

  if (!body.stream) {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ id, type: 'message', role: 'assistant', model: body.model, content: plan.blocks, stop_reason: stopReason, stop_sequence: null, usage }));
  }

  res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
  sse(res, 'message_start', { type: 'message_start', message: { id, type: 'message', role: 'assistant', model: body.model, content: [], stop_reason: null, stop_sequence: null, usage: { ...usage, output_tokens: 1 } } });
  for (let i = 0; i < plan.blocks.length; i++) {
    const blk = plan.blocks[i];
    if (blk.type === 'text') {
      sse(res, 'content_block_start', { type: 'content_block_start', index: i, content_block: { type: 'text', text: '' } });
      const parts = plan.chunkDelayMs ? blk.text.match(/.{1,5}/g) : [blk.text];
      for (const p of parts) {
        if (res.destroyed) return log({ clientDisconnected: true, id });
        sse(res, 'content_block_delta', { type: 'content_block_delta', index: i, delta: { type: 'text_delta', text: p } });
        if (plan.chunkDelayMs) await sleep(plan.chunkDelayMs);
      }
    } else {
      sse(res, 'content_block_start', { type: 'content_block_start', index: i, content_block: { type: 'tool_use', id: blk.id, name: blk.name, input: {} } });
      sse(res, 'content_block_delta', { type: 'content_block_delta', index: i, delta: { type: 'input_json_delta', partial_json: JSON.stringify(blk.input) } });
    }
    sse(res, 'content_block_stop', { type: 'content_block_stop', index: i });
  }
  sse(res, 'message_delta', { type: 'message_delta', delta: { stop_reason: stopReason, stop_sequence: null }, usage: { output_tokens: 20 } });
  sse(res, 'message_stop', { type: 'message_stop' });
  res.end();
});

server.listen(port, '127.0.0.1', () => {
  log({ listening: port });
  console.log(`Mock Messages API on http://127.0.0.1:${port} - logging to ${logFile}`);
});
