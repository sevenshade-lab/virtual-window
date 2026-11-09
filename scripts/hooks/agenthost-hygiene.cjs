// 长对话提醒：量每家当前对话的上下文有多长，过线（默认 25 万）就由虚拟窗口统一提醒它
// 「先把手头这件事做完；有后续，登进待办、交接到新对话」。
// 用户 2026-11-07 定的线和做法；算法从共用工具 session-hygiene 的 long_session_hook.py 移植过来。
//
// 钩子小程序（agenthost-hook.cjs）和 OpenCode 插件 require 它；没有钩子的家（DSH）由宿主定时跑 `scan` 代算。
//
// 各家的长度从哪来：
//   claude       对话记录末尾最近一条主对话助手消息的 usage：input + cache_read + cache_creation
//   codex        rollout 里最近一条 token_count 的 last_token_usage.input_tokens（已含缓存）
//   cursor       记录里没有用量（Cursor 11-07 确认）。afterAgentResponse 实际带这一轮的 input_tokens / cache_read_tokens
//                （文档没写），有就用它、记下来；没有就按记录字节数 ÷ 3 估（approx）
//   antigravity  conversations\<对话>.db 的 gen_metadata 最后一行（protobuf），字段 1.9.10.1 = 当前上下文
//                （1.9.10.4 是窗口大小 256000；11-07 拿真对话对过，逐轮单调涨）
//   opencode     插件从 message.updated 拿 tokens（input + cache.read + cache.write），直接传数进来
//   dsh          ~/.dsh/sessions/<目录>/<会话>/session.v4.jsonl.zstd 里最近一条 assistant/message 的 usage.inputTokens
//   grokbot      模型在云端，本机没有记录：量不了
//
// 记在 %LOCALAPPDATA%\AgentHost\hygiene\<kind>-<会话>.json：{ kind, sessionId, tokens, approx, at, warned, title?, cwd? }
// warned = 上一次提醒时的长度：过线提一次，之后每再涨 STEP 提一次。
// 关掉：放一个 hygiene\off；只关某一家：hygiene\<kind>.off。阈值：环境变量 LONG_SESSION_TOKENS 或 hygiene\limit.txt。
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

// AGENTHOST_DATA：测试时指到临时目录
const BASE = process.env.AGENTHOST_DATA || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AgentHost');
const DIR = path.join(BASE, 'hygiene');
const BIN = __dirname;   // 待办脚本跟本文件放在同一个目录（bin）
const TAIL = 4 << 20;
const STEP = 100000;

function readText(f) { try { return fs.readFileSync(f, 'utf8'); } catch { return ''; } }

function limit() {
  const env = Number(process.env.LONG_SESSION_TOKENS);
  if (env > 0) return env;
  const f = Number(readText(path.join(DIR, 'limit.txt')).trim());
  return f > 0 ? f : 250000;
}

// 试验：hygiene\trial.txt 第一行 limit=<数>，下面每行一个范围（文件夹前缀、<家>:<会话>）。落在范围里的对话按这条低线算，
// 别的对话照常 —— 拿真的各家 agent 在测试文件夹里试「提醒送不送得到」，又不打扰用户正在用的对话
function trial() {
  const t = readText(path.join(DIR, 'trial.txt'));
  if (!t) return null;
  const lines = t.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
  const m = /^limit\s*=\s*(\d+)/.exec(lines[0] || '');
  if (!m) return null;
  return { limit: Number(m[1]), scopes: lines.slice(1).map(x => x.toLowerCase().replace(/\\/g, '/')) };
}

function limitFor(rec) {
  const t = trial();
  if (t && rec) {
    const cwd = String(rec.cwd || '').toLowerCase().replace(/\\/g, '/');
    const key = (rec.kind + ':' + rec.sessionId).toLowerCase();
    if (t.scopes.some(s => s === key || (cwd && s.length > 3 && cwd.startsWith(s)))) return t.limit;
  }
  return limit();
}

function off(kind) {
  return fs.existsSync(path.join(DIR, 'off')) || (kind && fs.existsSync(path.join(DIR, kind + '.off')));
}

function tail(file, bytes) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, bytes);
    const buf = Buffer.allocUnsafe(len);
    const got = fs.readSync(fd, buf, 0, len, size - len);
    return { text: buf.toString('utf8', 0, got), size };
  } catch { return null; } finally {
    if (fd !== undefined) { try { fs.closeSync(fd); } catch {} }
  }
}

// Claude / Codex 的 jsonl 记录
function fromJsonl(file) {
  const t = tail(file, TAIL);
  if (!t) return 0;
  const lines = t.text.split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const raw = lines[i];
    if (raw.indexOf('"usage"') < 0 && raw.indexOf('token_count') < 0) continue;
    let d;
    try { d = JSON.parse(raw); } catch { continue; }
    if (d.type === 'assistant' && !d.isSidechain) {
      const u = (d.message && d.message.usage) || {};
      const n = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
      if (n) return n;
    }
    const p = d.payload || {};
    if (d.type === 'event_msg' && p.type === 'token_count') {
      const n = ((p.info || {}).last_token_usage || {}).input_tokens || 0;
      if (n) return n;
    }
  }
  return 0;
}

// ---- Antigravity：protobuf 里取 1.9.10.1 ----
function varint(b, i) {
  let r = 0, s = 0;
  for (;;) {
    if (i >= b.length) throw new Error('eof');
    const c = b[i++];
    r += (c & 0x7f) * Math.pow(2, s);
    s += 7;
    if (c < 0x80) return [r, i];
    if (s > 63) throw new Error('varint');
  }
}
// 在 b 里找字段路径 want（如 [1, 9, 10, 1]）的 varint 值；找不到返回 0
function pbPath(b, want) {
  let i = 0;
  while (i < b.length) {
    let key; [key, i] = varint(b, i);
    const f = Math.floor(key / 8), w = key & 7;
    if (w === 0) {
      let v; [v, i] = varint(b, i);
      if (f === want[0] && want.length === 1) return v;
    } else if (w === 2) {
      let l; [l, i] = varint(b, i);
      if (f === want[0] && want.length > 1) {
        try { const v = pbPath(b.subarray(i, i + l), want.slice(1)); if (v) return v; } catch {}
      }
      i += l;
    } else if (w === 1) i += 8;
    else if (w === 5) i += 4;
    else throw new Error('wire ' + w);
  }
  return 0;
}

function quietSqlite() {
  // node:sqlite 在这个版本还会打一行「实验功能」警告到 stderr：钩子里别打
  const orig = process.emitWarning;
  process.emitWarning = function () {};
  try { return require('node:sqlite'); } finally { process.emitWarning = orig; }
}

// 桌面端记在 ~/.gemini/antigravity，命令行 agy 记在 ~/.gemini/antigravity-cli（11-07 实测）
function antigravityDb(id) {
  for (const d of ['antigravity', 'antigravity-cli']) {
    const f = path.join(os.homedir(), '.gemini', d, 'conversations', id + '.db');
    if (fs.existsSync(f)) return f;
  }
  return path.join(os.homedir(), '.gemini', 'antigravity', 'conversations', id + '.db');
}

function fromAntigravity(id) {
  const f = antigravityDb(id);
  if (!/^[a-zA-Z0-9-]{8,80}$/.test(id) || !fs.existsSync(f)) return 0;
  let db;
  try {
    const { DatabaseSync } = quietSqlite();
    db = new DatabaseSync(f, { readOnly: true });
    // 最后几行里从后往前找（最后一次生成有时还没写完用量）
    const rows = db.prepare('select data from gen_metadata order by idx desc limit 4').all();
    for (const r of rows) {
      if (!r.data) continue;
      let n = 0;
      try { n = pbPath(Buffer.from(r.data), [1, 9, 10, 1]); } catch {}
      if (n) return n;
    }
  } catch {} finally { try { if (db) db.close(); } catch {} }
  return 0;
}

// ---- DSH ----
function dshRoot() { return path.join(os.homedir(), '.dsh', 'sessions'); }

function readDsh(file) {
  try {
    const zlib = require('node:zlib');
    if (typeof zlib.zstdDecompressSync !== 'function') return null;
    return zlib.zstdDecompressSync(fs.readFileSync(file)).toString('utf8');
  } catch { return null; }
}

function dshInfo(file) {
  const t = readDsh(file);
  if (!t) return null;
  let tokens = 0, title = '', reply = '', cwd = '', ended = false;
  for (const l of t.split('\n')) {
    if (!l) continue;
    let d;
    try { d = JSON.parse(l); } catch { continue; }
    const data = d.data || {};
    if (d.type === 'session/title' && data.title) title = data.title;
    else if (d.type === 'assistant/message') {
      if (data.usage && data.usage.inputTokens) tokens = data.usage.inputTokens;
      const c = (data.message && data.message.content) || [];
      const txt = (Array.isArray(c) ? c : []).filter(x => x && x.type === 'text').map(x => x.text).join('\n').trim();
      if (txt) reply = txt;
    } else if (d.type === 'turn/end') ended = true;
    else if (!cwd && data.cwd) cwd = data.cwd;
  }
  return { tokens, title, reply: reply.slice(0, 2000), cwd, ended };
}

function cursorTranscript(sessionId) {
  if (!/^[a-zA-Z0-9-]{8,80}$/.test(sessionId || '')) return '';
  const root = path.join(os.homedir(), '.cursor', 'projects');
  let dirs = [];
  try { dirs = fs.readdirSync(root); } catch { return ''; }
  for (const d of dirs) {
    const f = path.join(root, d, 'agent-transcripts', sessionId, sessionId + '.jsonl');
    if (fs.existsSync(f)) return f;
  }
  return '';
}

// ---- 量 ----
// p：钩子载荷（可以是 {}）；返回 { tokens, approx }
function measure(kind, sessionId, p) {
  p = p || {};
  const tp = p.transcript_path || p.transcriptPath || '';
  try {
    if (kind === 'claude' || kind === 'codex') return { tokens: tp ? fromJsonl(tp) : 0, approx: false };
    if (kind === 'antigravity') return { tokens: fromAntigravity(sessionId), approx: false };
    if (kind === 'cursor') {
      const inp = Number(p.input_tokens) || 0;
      if (inp) return { tokens: inp + (Number(p.cache_read_tokens) || 0) + (Number(p.cache_write_tokens) || 0), approx: false };
      const prev = load(kind, sessionId);
      if (prev && prev.tokens && !prev.approx) return { tokens: prev.tokens, approx: false };
      // 命令行版（cursor-agent）的载荷里 transcript_path 是空的：按会话号去 ~/.cursor/projects/*/agent-transcripts/<会话>/ 找
      const f = tp || cursorTranscript(sessionId);
      if (f) { try { return { tokens: Math.round(fs.statSync(f).size / 3), approx: true }; } catch {} }
    }
  } catch {}
  return { tokens: 0, approx: false };
}

function stateFile(kind, sessionId) { return path.join(DIR, kind + '-' + sessionId + '.json'); }

function load(kind, sessionId) {
  try { return JSON.parse(fs.readFileSync(stateFile(kind, sessionId), 'utf8')); } catch { return null; }
}

function save(rec) {
  try {
    fs.mkdirSync(DIR, { recursive: true });
    const f = stateFile(rec.kind, rec.sessionId);
    const tmp = f + '.' + process.pid + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(rec));
    try { fs.renameSync(tmp, f); } catch { try { fs.unlinkSync(tmp); } catch {} }
  } catch {}
}

// 记下量到的长度（不提醒）；返回记录
function record(kind, sessionId, m, extra) {
  if (!sessionId || !m || !m.tokens) return load(kind, sessionId);
  const prev = load(kind, sessionId) || {};
  const rec = Object.assign({}, prev, extra || {}, { kind, sessionId, tokens: m.tokens, approx: !!m.approx, at: Date.now() });
  if (!rec.warned) rec.warned = 0;
  // 长度掉回线下（压缩过上下文）：下次过线再提
  if (rec.tokens < limitFor(rec)) rec.warned = 0;
  save(rec);
  return rec;
}

// 该不该提醒：过线、而且上次提醒之后又涨了 STEP（或从没提过）
function due(rec) {
  if (!rec || !rec.tokens || off(rec.kind)) return false;
  const L = limitFor(rec);
  if (rec.tokens < L) return false;
  return !rec.warned || rec.tokens >= rec.warned + STEP;
}

function markWarned(rec) {
  const cur = load(rec.kind, rec.sessionId) || rec;
  cur.warned = rec.tokens;
  cur.warnedAt = Date.now();
  save(cur);
}

function todoCmd(kind, sessionId) {
  // node 在 PATH 上：bash / cmd / PowerShell 都能直接跑这一行
  return 'node "' + path.join(BIN, 'agenthost-todo.cjs').replace(/\\/g, '/') + '" add --kind ' + kind + ' --session ' + sessionId + ' "<剩下什么，一两句>"';
}

const TAILS = {
  claude: '交接用 hand_off_to_session；交接或收尾之后用 archive_session 归档本对话（用户 11-07 定：即时归档）。',
};

function reminder(rec) {
  const n = Math.round(rec.tokens / 1000);
  const L = Math.round(limitFor(rec) / 1000);
  return '【长对话提醒·虚拟窗口】本对话上下文' + (rec.approx ? '约' : '已约') + ' ' + n + 'k tokens，超过用户定的 ' + L + 'k 线'
    + '（长对话占内存、也更容易出错）。先把用户这条消息交代的事照常做完，不要因此打断或拒绝；做完后：'
    + '如果还有没做完的后续，先把剩下的事登进虚拟窗口的待办清单（用户靠它记着哪些事没做完，对话归档了也不丢）：'
    + todoCmd(rec.kind, rec.sessionId) + '；再把剩下的计划（文件、进度、完成标准）交给一个新对话接着做'
    + '（用自己的新建对话 / 交接办法，或写进交接单后告诉用户开新对话）。'
    + '如果没有后续，在收尾时用一句话告诉用户「这个对话可以归档了」。' + (TAILS[rec.kind] || '')
    + '只在这一轮做一次，不要反复提。';
}

// 钩子用：量、记下、该提醒就返回提醒正文（同时记成提醒过），否则返回 ''
function check(kind, sessionId, p, extra) {
  const m = measure(kind, sessionId, p);
  const rec = record(kind, sessionId, m, extra);
  if (!due(rec)) return '';
  markWarned(rec);
  return reminder(rec);
}

// 插件用（自己算好了数）
function checkTokens(kind, sessionId, tokens, extra) {
  const rec = record(kind, sessionId, { tokens: Number(tokens) || 0, approx: false }, extra);
  if (!due(rec)) return '';
  markWarned(rec);
  return reminder(rec);
}

// 没有钩子的家（DSH）：宿主定时跑。两天内动过的会话量一遍；过线的写进 hygiene\nudge.json 给宿主在状态行提一句
function scan() {
  const out = [];
  const root = dshRoot();
  const since = Date.now() - 2 * 86400e3;
  let dirs = [];
  try { dirs = fs.readdirSync(root); } catch {}
  for (const d of dirs) {
    let subs = [];
    try { subs = fs.readdirSync(path.join(root, d)); } catch { continue; }
    for (const s of subs) {
      const f = path.join(root, d, s, 'session.v4.jsonl.zstd');
      let st;
      try { st = fs.statSync(f); } catch { continue; }
      if (st.mtimeMs < since) continue;
      const sid = s.replace(/^session-/, '');
      const prev = load('dsh', sid);
      if (prev && prev.mtime === st.mtimeMs) { if (prev.tokens >= limit()) out.push(prev); continue; }
      const info = dshInfo(f);
      if (!info) continue;
      const rec = record('dsh', sid, { tokens: info.tokens, approx: false },
        { title: info.title, cwd: info.cwd, reply: info.reply, mtime: st.mtimeMs, file: f });
      if (rec && rec.tokens >= limit()) out.push(rec);
    }
  }
  // 给宿主：该提醒用户的（还没提过、或又涨了一截的）
  const nudge = out.filter(due).map(r => ({ kind: r.kind, sessionId: r.sessionId, tokens: r.tokens, title: r.title || '' }));
  try {
    fs.mkdirSync(DIR, { recursive: true });
    fs.writeFileSync(path.join(DIR, 'nudge.json'), JSON.stringify({ at: Date.now(), limit: limit(), items: nudge }));
  } catch {}
  return nudge;
}

// 宿主在状态行提过了：记成提醒过
function nudged(kind, sessionId) {
  const rec = load(kind, sessionId);
  if (rec) markWarned(rec);
}

// 偶尔清一下一周没动的
function sweep() {
  try {
    const week = Date.now() - 7 * 86400e3;
    for (const f of fs.readdirSync(DIR)) {
      if (!/\.json$/.test(f) || f === 'nudge.json') continue;
      const fp = path.join(DIR, f);
      try { if (fs.statSync(fp).mtimeMs < week) fs.unlinkSync(fp); } catch {}
    }
  } catch {}
}

module.exports = { limit, off, measure, record, due, markWarned, check, checkTokens, reminder, scan, nudged, load, sweep, dshInfo, fromAntigravity, fromJsonl, DIR };

if (require.main === module) {
  const a = process.argv.slice(2);
  const out = s => process.stdout.write(s + '\n');
  if (a[0] === 'measure') {
    // measure <kind> <记录文件 | 会话号>
    const kind = a[1], x = a[2] || '';
    const p = fs.existsSync(x) ? { transcript_path: x } : {};
    const sid = fs.existsSync(x) ? path.basename(x).replace(/\.[^.]+$/, '') : x;
    out(JSON.stringify(Object.assign({ kind, sessionId: sid }, measure(kind, sid, p))));
  } else if (a[0] === 'scan') {
    out(JSON.stringify(scan()));
  } else if (a[0] === 'nudged') {
    nudged(a[1], a[2]);
  } else {
    out('用法: node agenthost-hygiene.cjs measure <kind> <记录文件|会话号> | scan | nudged <kind> <会话号>');
  }
}
