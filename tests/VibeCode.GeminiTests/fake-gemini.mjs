// Fixtures follow Google's unmodified Gemini CLI 0.63.0 ACP messages and native JSONL usage records.
import readline from 'node:readline';
import fs from 'node:fs';
import path from 'node:path';
const send = value => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...value }) + '\n');
const id = 'f50bd21a-867d-4548-b551-000000000001';
const dir = path.join(process.env.GEMINI_CLI_HOME, '.gemini', 'tmp', 'fixture', 'chats');
fs.mkdirSync(dir, { recursive: true });
const file = path.join(dir, `session-2026-10-13-${id.slice(0, 8)}.jsonl`);
const append = row => fs.appendFileSync(file, JSON.stringify(row) + '\n');
const models = { currentModelId: 'auto-gemini', availableModels: [
  { modelId: 'auto-gemini', name: 'Auto' }, { modelId: 'gemini-test-pro', name: 'Gemini Pro' }, { modelId: 'gemini-test-flash', name: 'Gemini Flash' },
  // Tests future catalog passthrough, not account entitlement or current CLI availability.
  { modelId: 'gemini-4-argon', name: 'Gemini 4 Argon' }, { modelId: 'gemini-3.8-flash', name: 'Gemini 3.8 Flash' },
] };
let authenticated = false, turn = 0, pending = null, permitted = false;
const update = value => send({ method: 'session/update', params: { sessionId: id, update: value } });
function finish(request, cancelled = false) {
  for (const [suffix, model, tokens] of [
    ['a', 'gemini-test-pro', { input: 1000, cached: 400, output: 60, thoughts: 40, total: 1100 }],
    ['b', 'gemini-test-flash', { input: 500, cached: 200, output: 30, thoughts: 10, total: 540 }],
  ]) {
    const row = { id: `turn-${turn}-${suffix}`, type: 'gemini', model, tokens, content: 'response' };
    append({ ...row, tokens: null }); append(row); append(row); // upserts must not multiply consumption
  }
  if (!request.empty) {
    update({ sessionUpdate: 'agent_thought_chunk', content: { type: 'text', text: 'Checking the result.' } });
    update({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'Gemini fixture response.' } });
  }
  send({ id: request.id, result: { stopReason: cancelled ? 'cancelled' : 'end_turn', _meta: { quota: {
    token_count: { input_tokens: 1500, output_tokens: 90 }, model_usage: [
      { model: 'gemini-test-pro', token_count: { input_tokens: 1000, output_tokens: 60 } },
      { model: 'gemini-test-flash', token_count: { input_tokens: 500, output_tokens: 30 } },
    ],
  } } } });
}
readline.createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line);
  fs.appendFileSync(path.join(process.env.GEMINI_CLI_HOME, 'wire.jsonl'), line + '\n');
  const p = request.params;
  if (!request.method) {
    permitted = request.result?.outcome?.outcome === 'selected';
    if (pending) { finish(pending); pending = null; }
    return;
  }
  switch (request.method) {
    case 'initialize': send({ id: request.id, result: { protocolVersion: 1, agentCapabilities: { loadSession: true, mcpCapabilities: { http: true, sse: true }, promptCapabilities: { image: true } }, authMethods: [{ id: 'oauth-personal', name: 'Log in with Google' }] } }); break;
    case 'authenticate': authenticated = p.methodId === 'oauth-personal'; send({ id: request.id, result: {} }); break;
    case 'session/new': case 'session/load':
      if (!authenticated || process.env.GEMINI_API_KEY || process.env.GOOGLE_API_KEY) {
        send({ id: request.id, error: { code: -32000, message: 'Google authentication required' } }); break;
      }
      if (request.method === 'session/new') fs.writeFileSync(file, JSON.stringify({ sessionId: id }) + '\n');
      else {
        turn = 10;
        update({ sessionUpdate: 'user_message_chunk', content: { type: 'text', text: 'Earlier question' } });
        update({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'Earlier answer' } });
      }
      send({ id: request.id, result: { sessionId: id, models, modes: { currentModeId: 'default', availableModes: [{ id: 'default' }, { id: 'plan' }, { id: 'yolo' }] } } });
      update({ sessionUpdate: 'available_commands_update', availableCommands: [{ name: 'compress', description: 'Compact the context' }] });
      break;
    case 'session/set_model': case 'session/set_mode': send({ id: request.id, result: {} }); break;
    case 'session/prompt': {
      turn++;
      const text = p.prompt.filter(c => c.type === 'text').map(c => c.text).join('\n');
      if (text.includes('wait-for-cancel')) { pending = request; break; }
      if (text.includes('empty-with-usage')) { finish({ ...request, empty: true }); break; }
      if (text.includes('fail-with-usage')) {
        append({ id: `failed-${turn}`, type: 'gemini', model: 'gemini-test-pro', tokens: { input: 20, cached: 5, output: 3, thoughts: 2 } });
        send({ id: request.id, error: { code: 429, message: 'Quota exhausted. Retry after 60 seconds.' } }); break;
      }
      if (text.includes('permission')) {
        pending = request;
        append({ id: `turn-${turn}-a`, type: 'gemini', model: 'gemini-test-pro', tokens: { input: 1000, cached: 400, output: 60, thoughts: 40 } });
        // Gemini supplies display titles, locations and explanations, without rawInput.
        const call = text.includes('mcp-permission')
          ? { toolCallId: 'tool-one', title: 'chat_set_title(title: "Example")', kind: 'other', status: 'pending', locations: [],
              content: [{ type: 'content', content: { type: 'text', text: 'Call the MCP server.' } }] }
          : { toolCallId: 'tool-one', title: 'echo Gemini', kind: 'execute', status: 'pending', locations: [], content: [] };
        update({ sessionUpdate: 'tool_call', ...call });
        send({ id: 'permission-one', method: 'session/request_permission', params: { sessionId: id, toolCall: call,
          options: [{ optionId: 'proceed_once', name: 'Allow once', kind: 'allow_once' }, { optionId: 'cancel', name: 'Reject', kind: 'reject_once' }] } });
      } else finish(request);
      break;
    }
    case 'session/cancel': if (pending) { finish(pending, true); pending = null; } break;
    default: send({ id: request.id, error: { code: -32601, message: 'Method not supported' } });
  }
});
