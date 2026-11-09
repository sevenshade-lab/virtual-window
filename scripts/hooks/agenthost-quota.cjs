#!/usr/bin/env node
// 各家订阅的额度：读一遍，写到 %LOCALAPPDATA%\AgentHost\quota.json，宿主顶栏那一条照它画。
// 宿主每 10 分钟跑一次（点开明细时也跑一次）；也能手动跑：node agenthost-quota.cjs
//
// 六家都读官方的实时数字（都只用本机已登录的凭据、只读用量，不发对话）：
//   Claude  —— ~/.claude/.credentials.json 的令牌问 /api/oauth/usage（5 小时 / 每周窗口）。
//              令牌过期就照 Claude Code 自己的做法刷新一次，新令牌原样写回（不写回的话 CLI 会因为旧的刷新令牌作废而掉登录）。
//   Codex   —— ~/.codex/auth.json 的令牌问 chatgpt.com/backend-api/wham/usage；问不到就退回最近一份会话记录里的 rate_limits。
//   Cursor  —— Cursor 的 state.vscdb 里的令牌问 cursor.com/api/usage-summary（本计费周期用了多少）。
//   OpenCode Go —— Go 的 key 问 opencode.ai/zen/go/v1/usage（5 小时 / 每周 / 每月）。
//   Antigravity —— 已打开的官方桌面端 RetrieveUserQuotaSummary（官方分组的周 / 5 小时额度）。
//   Grok Bot —— 跟 Cursor 同一个账号，问 Cursor 后台的 GetSandUsageStatus（每周）。
//   Colab   —— WSL 里官方 colab usage 的剩余计算单元，挂在 Antigravity 那张卡上（一小时读一次）。
//   DeepSeek —— 按量付费，没有限额，读余额：api.deepseek.com/user/balance（key 取 OpenCode 配置里那把，没有再用 DEEPSEEK_API_KEY）。
// 账户、价钱、续费日都在 %LOCALAPPDATA%\AgentHost\quota-accounts.json，手改；DEFAULTS 里新加的一家会自动补进去，
// 不想看的那家写 "hidden": true（删掉的话下次又补回来）；宿主面板里每张卡的「隐藏」/ 底下「显示 X」改的就是它。
// 藏着的照样读：订阅停过又续上就自动去掉 hidden（lapsedSeen 记「停过」）；没藏但读到免费档的写 inactive，宿主不显示。

'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');

const HOME = os.homedir();
const BASE = path.join(process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local'), 'AgentHost');
const OUT = path.join(BASE, 'quota.json');
const ACCOUNTS = path.join(BASE, 'quota-accounts.json');

// price 按月；currency 随便写（USD / CNY），面板上原样显示。auto 的那几家用量是读出来的，其余手填。
const DEFAULTS = [
  { id: 'claude', name: 'Claude', price: 100, currency: 'USD', url: 'https://claude.ai/settings/usage' },
  { id: 'codex', name: 'Codex', price: null, currency: 'USD', url: 'https://chatgpt.com/codex/settings/usage' },
  { id: 'cursor', name: 'Cursor', price: 20, currency: 'USD', url: 'https://cursor.com/dashboard?tab=usage' },
  { id: 'opencode-go', name: 'OpenCode Go', icon: 'opencode', price: 10, currency: 'USD', url: 'https://opencode.ai/auth' },
  { id: 'antigravity', name: 'Antigravity', price: null, currency: 'USD', url: 'https://antigravity.google' },
  { id: 'deepseek', name: 'DeepSeek', icon: 'dsh', price: null, currency: 'CNY', url: 'https://platform.deepseek.com/usage' },
  { id: 'grokbot', name: 'Grok Bot', price: null, currency: 'USD', url: 'https://cursor.com/dashboard' },
];

function readJson(p) { try { return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, '')); } catch { return null; } }

function writeAtomic(p, text) {
  const tmp = p + '.' + process.pid + '.tmp';
  fs.writeFileSync(tmp, text, 'utf8');
  fs.renameSync(tmp, p);
}

async function getJson(url, opts) {
  const r = await fetch(url, Object.assign({ signal: AbortSignal.timeout(20000) }, opts));
  const text = await r.text();
  let body = null;
  try { body = JSON.parse(text); } catch { }
  return { status: r.status, body, text };
}

const pct = v => (typeof v === 'number' && isFinite(v)) ? Math.max(0, Math.min(100, v)) : null;
const isoMs = s => { const t = Date.parse(s || ''); return isNaN(t) ? null : t; };

// ---------- Claude ----------
const CLAUDE_CREDS = path.join(HOME, '.claude', '.credentials.json');
const CLAUDE_CLIENT = '9d1c250a-e61b-44d9-88ed-5944d1962f5e';
const CLAUDE_WINDOWS = { five_hour: '5 小时', seven_day: '每周', seven_day_opus: '每周 Opus', seven_day_sonnet: '每周 Sonnet' };

async function claudeToken() {
  const d = readJson(CLAUDE_CREDS);
  const o = d && d.claudeAiOauth;
  if (!o || !o.accessToken) throw new Error('没登录 Claude Code 命令行');
  if (o.expiresAt && o.expiresAt > Date.now() + 60000) return { token: o.accessToken, o };
  // 过期了：刷新。借 Claude Code 同一把锁（proper-lockfile 在 .credentials.lock 旁边建 .lock 目录），它正在刷就这次不刷
  const lock = path.join(HOME, '.claude', '.credentials.lock.lock');
  try { fs.mkdirSync(lock); } catch {
    try { if (Date.now() - fs.statSync(lock).mtimeMs < 30000) throw new Error('Claude Code 正在刷新登录，下次再读'); fs.rmSync(lock, { recursive: true }); fs.mkdirSync(lock); }
    catch (e) { throw e.message.startsWith('Claude') ? e : new Error('Claude 登录锁被占用'); }
  }
  try {
    const again = readJson(CLAUDE_CREDS);   // 拿锁之后再看一眼：刚好别人刷过了就用它的
    const a = again && again.claudeAiOauth;
    if (a && a.expiresAt > Date.now() + 60000) return { token: a.accessToken, o: a };
    const r = await getJson('https://console.anthropic.com/v1/oauth/token', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ grant_type: 'refresh_token', refresh_token: a.refreshToken, client_id: CLAUDE_CLIENT }),
    });
    if (r.status !== 200 || !r.body || !r.body.access_token) throw new Error('Claude 登录过期，刷新没成（' + r.status + '），开一次 claude 命令行就好');
    a.accessToken = r.body.access_token;
    if (r.body.refresh_token) a.refreshToken = r.body.refresh_token;
    if (r.body.expires_in) a.expiresAt = Date.now() + r.body.expires_in * 1000;
    again.claudeAiOauth = a;
    writeAtomic(CLAUDE_CREDS, JSON.stringify(again));
    return { token: a.accessToken, o: a };
  } finally { try { fs.rmSync(lock, { recursive: true }); } catch { } }
}

async function claude() {
  const { token, o } = await claudeToken();
  const r = await getJson('https://api.anthropic.com/api/oauth/usage', {
    headers: { Authorization: 'Bearer ' + token, 'anthropic-beta': 'oauth-2025-04-20', 'User-Agent': 'claude-code/agenthost-quota' },
  });
  if (r.status !== 200 || !r.body) throw new Error('Claude 用量接口回 ' + r.status);
  const windows = [];
  for (const k of Object.keys(r.body)) {
    const w = r.body[k];
    if (!w || typeof w !== 'object' || typeof w.utilization !== 'number') continue;
    if (k === 'extra_usage') continue;
    // 没见过的键（接口里有带代号的，比如某个新模型单独的周限额）算次要的一行，不进顶栏
    windows.push({ key: k, label: CLAUDE_WINDOWS[k] || '其他限额', used: pct(w.utilization), resetsAt: isoMs(w.resets_at), minor: !CLAUDE_WINDOWS[k] || k !== 'five_hour' && k !== 'seven_day' });
  }
  const plan = { max: 'Max', pro: 'Pro', team: 'Team', enterprise: 'Enterprise' }[o.subscriptionType] || o.subscriptionType || '';
  const tier = /max_(\d+x)/.exec(o.rateLimitTier || '');
  return { plan: plan + (tier ? ' ' + tier[1] : ''), windows };
}

// ---------- Codex ----------
function codexWindow(w, fallbackKey) {
  if (!w) return null;
  const secs = w.limit_window_seconds || (w.window_minutes ? w.window_minutes * 60 : 0);
  const label = secs >= 6 * 86400 ? '每周' : secs >= 3600 ? Math.round(secs / 3600) + ' 小时' : fallbackKey;
  const at = w.reset_at || w.resets_at;
  return { key: fallbackKey, label, used: pct(w.used_percent), resetsAt: at ? at * 1000 : null };
}

function codexFromSessions() {
  // 最近一份会话记录里最后一条 rate_limits（Codex 自己每轮都会记）
  const root = path.join(HOME, '.codex', 'sessions');
  let newest = null;
  const walk = (d, depth) => {
    let ents; try { ents = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
    ents.sort((a, b) => b.name.localeCompare(a.name));
    for (const e of ents) {
      if (newest) return;
      const p = path.join(d, e.name);
      if (e.isDirectory() && depth < 3) walk(p, depth + 1);
      else if (e.isFile() && e.name.endsWith('.jsonl')) newest = p;
    }
  };
  walk(root, 0);
  if (!newest) return null;
  const lines = fs.readFileSync(newest, 'utf8').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    if (lines[i].indexOf('"rate_limits"') < 0) continue;
    try {
      const j = JSON.parse(lines[i]);
      const rl = (j.payload && j.payload.rate_limits) || j.rate_limits;
      if (rl) return { rl, at: fs.statSync(newest).mtimeMs };
    } catch { }
  }
  return null;
}

async function codex() {
  const auth = readJson(path.join(HOME, '.codex', 'auth.json'));
  const t = auth && auth.tokens;
  let err = '没登录 Codex';
  if (t && t.access_token) {
    const r = await getJson('https://chatgpt.com/backend-api/wham/usage', {
      headers: { Authorization: 'Bearer ' + t.access_token, 'chatgpt-account-id': t.account_id || '', 'User-Agent': 'codex_cli_rs' },
    });
    if (r.status === 200 && r.body && r.body.rate_limit) {
      const rl = r.body.rate_limit;
      const windows = [codexWindow(rl.primary_window, 'primary'), codexWindow(rl.secondary_window, 'secondary')].filter(Boolean);
      return { plan: planName(r.body.plan_type), windows, limitReached: !!rl.limit_reached };
    }
    err = 'Codex 用量接口回 ' + r.status;
  }
  const s = codexFromSessions();
  if (!s) throw new Error(err);
  const windows = [codexWindow(s.rl.primary, 'primary'), codexWindow(s.rl.secondary, 'secondary')].filter(Boolean);
  return { plan: planName(s.rl.plan_type), windows, stale: s.at, note: '接口没读到，用的是最近一次对话时的数' };
}

function planName(p) {
  return ({ plus: 'Plus', pro: 'Pro', prolite: 'Pro Lite', team: 'Team', business: 'Business', go: 'Go', free: 'Free' })[p] || p || '';
}

// ---------- Cursor ----------
function cursorToken() {
  const db = path.join(process.env.APPDATA || path.join(HOME, 'AppData', 'Roaming'), 'Cursor', 'User', 'globalStorage', 'state.vscdb');
  if (!fs.existsSync(db)) throw new Error('没装 Cursor');
  let sqlite;
  try { sqlite = require('node:sqlite'); } catch { throw new Error('Node 版本太老，读不了 Cursor 的登录（要 22.5 以上）'); }
  const conn = new sqlite.DatabaseSync(db, { readOnly: true });
  let tok, membership;
  try {
    const q = conn.prepare("select key, value from ItemTable where key in ('cursorAuth/accessToken','cursorAuth/stripeMembershipType')");
    for (const row of q.all()) {
      const v = typeof row.value === 'string' ? row.value : Buffer.from(row.value).toString('utf8');
      if (row.key === 'cursorAuth/accessToken') tok = v; else membership = v;
    }
  } finally { conn.close(); }
  if (!tok) throw new Error('Cursor 没登录');
  return { tok, membership };
}

async function cursor() {
  const { tok, membership } = cursorToken();
  const sub = JSON.parse(Buffer.from(tok.split('.')[1], 'base64url').toString()).sub || '';
  const uid = sub.split('|').pop();
  const r = await getJson('https://cursor.com/api/usage-summary', {
    headers: { Cookie: 'WorkosCursorSessionToken=' + uid + '%3A%3A' + tok, 'User-Agent': 'Mozilla/5.0' },
  });
  if (r.status !== 200 || !r.body) throw new Error('Cursor 用量接口回 ' + r.status);
  const b = r.body, plan = (b.individualUsage && b.individualUsage.plan) || {};
  const end = isoMs(b.billingCycleEnd);
  const windows = [{ key: 'cycle', label: '本月', used: pct(plan.totalPercentUsed), resetsAt: end, minor: true }];
  if (typeof plan.autoPercentUsed === 'number') windows.push({ key: 'auto', label: '本月 Auto', used: pct(plan.autoPercentUsed), resetsAt: end });
  if (typeof plan.apiPercentUsed === 'number') windows.push({ key: 'api', label: '本月指定模型', used: pct(plan.apiPercentUsed), resetsAt: end });
  const m = b.membershipType || membership || '';
  return { plan: m ? m[0].toUpperCase() + m.slice(1) : '', windows, unlimited: !!b.isUnlimited };
}

// ---------- OpenCode Go ----------
// 官方用量接口（opencode.ai/zen/go/v1/usage，拿 Go 的 key 问；2026-10-27 查到的，控制台网页也是读它）：
// rolling = 5 小时、weekly、monthly，各给 percent 和 resetsAt。用同一把 key 的所有程序都算在里面。
async function opencodeGo() {
  const auth = readJson(path.join(HOME, '.local', 'share', 'opencode', 'auth.json'));
  const key = auth && auth['opencode-go'] && auth['opencode-go'].key;
  if (!key) throw new Error('OpenCode 没登录 Go');
  const r = await getJson('https://opencode.ai/zen/go/v1/usage', { headers: { Authorization: 'Bearer ' + key } });
  const u = r.body && r.body.usage;
  if (r.status !== 200 || !u) throw new Error('OpenCode Go 用量接口回 ' + r.status);
  const windows = [];
  for (const [k, label] of [['rolling', '5 小时'], ['weekly', '每周'], ['monthly', '每月']]) {
    const w = u[k];
    if (!w || typeof w.percent !== 'number') continue;
    windows.push({ key: k, label, used: pct(w.percent), resetsAt: isoMs(w.resetsAt), minor: k === 'monthly' });
  }
  return { plan: 'Go', windows };
}

// ---------- Antigravity ----------
// 2026-10-28：模型列表附带的 quotaInfo 不含周额度，agy 的令牌直接问汇总接口也只回第三方模型组。
// 跟官方界面读同一个本地 RPC，保留它返回的全部组和窗口；桌面端没开就报错，main 留旧数并标采集时间。
// 只认安装目录里的桌面服务，并核 HTTP 端口确实由它监听；本地 CSRF 只在内存里使用。
const ANTIGRAVITY_ENDPOINT_PS = [
  '$ErrorActionPreference = "Stop"',
  '$exe = Join-Path $env:LOCALAPPDATA "Programs\\antigravity\\resources\\bin\\language_server.exe"',
  '$server = Get-CimInstance Win32_Process -Filter "Name=\'language_server.exe\'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -match "--standalone" -and $_.CommandLine -notmatch "--headless" } | Select-Object -First 1',
  'if (!$server) { exit 3 }',
  '$csrfMatch = [regex]::Match($server.CommandLine, \'--csrf_token(?:=|\\s+)(?:"([^"]+)"|(\\S+))\')',
  'if (!$csrfMatch.Success) { exit 3 }',
  '$csrf = if ($csrfMatch.Groups[1].Success) { $csrfMatch.Groups[1].Value } else { $csrfMatch.Groups[2].Value }',
  '$logPath = Join-Path $env:APPDATA "antigravity\\logs\\language_server.log"',
  '$head = (Get-Content -LiteralPath $logPath -TotalCount 20) -join "`n"',
  '$portMatch = [regex]::Match($head, "listening on \\w+ port at (\\d+) for HTTP(?:\\s|$)")',
  'if (!$portMatch.Success) { exit 3 }',
  '$port = [int]$portMatch.Groups[1].Value',
  'if ($port -lt 1 -or $port -gt 65535) { exit 3 }',
  '$listener = Get-NetTCPConnection -State Listen -OwningProcess $server.ProcessId -LocalPort $port -ErrorAction SilentlyContinue | Where-Object LocalAddress -eq "127.0.0.1"',
  'if (!$listener) { exit 3 }',
  '[Console]::Out.Write((@{ port = $port; csrf = $csrf } | ConvertTo-Json -Compress))',
].join('\n');

function antigravityEndpoint() {
  const { spawnSync } = require('child_process');
  const enc = Buffer.from(ANTIGRAVITY_ENDPOINT_PS, 'utf16le').toString('base64');
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', enc], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  let endpoint;
  try { if (r.status === 0) endpoint = JSON.parse(r.stdout); } catch { }
  if (!endpoint || !Number.isInteger(endpoint.port) || endpoint.port < 1 || endpoint.port > 65535 || typeof endpoint.csrf !== 'string' || !endpoint.csrf)
    throw new Error('Antigravity 桌面端没打开或用量服务未就绪，打开后再刷新');
  return endpoint;
}

function antigravityWindows(body) {
  const groups = body && body.response && body.response.groups;
  if (!Array.isArray(groups)) throw new Error('Antigravity 没回官方额度分组');
  const windows = [], seen = new Set();
  for (const g of groups) {
    if (!g || typeof g.displayName !== 'string' || !g.displayName || !Array.isArray(g.buckets)) continue;
    for (const b of g.buckets) {
      if (!b || typeof b.bucketId !== 'string' || !b.bucketId || seen.has(b.bucketId)) continue;
      seen.add(b.bucketId);
      const left = pct(typeof b.remainingFraction === 'number' ? b.remainingFraction * 100 : null);
      windows.push({ key: b.bucketId, group: g.displayName, period: b.window || '',
        label: { weekly: '每周', '5h': '5 小时', monthly: '每月' }[b.window] || b.displayName || b.window || '其他限额',
        used: left === null ? null : 100 - left, resetsAt: isoMs(b.resetTime), minor: false });
    }
  }
  if (!windows.some(w => w.used !== null)) throw new Error('Antigravity 没回可读取的额度');
  return windows;
}

async function antigravity() {
  const endpoint = antigravityEndpoint();
  const base = 'http://127.0.0.1:' + endpoint.port + '/exa.language_server_pb.LanguageServerService/';
  const opts = { method: 'POST', headers: { 'Content-Type': 'application/json', 'x-codeium-csrf-token': endpoint.csrf } };
  const r = await getJson(base + 'RetrieveUserQuotaSummary', { ...opts, body: JSON.stringify({ forceRefresh: true }) });
  if (r.status !== 200) throw new Error('Antigravity 官方用量接口回 ' + r.status);
  const windows = antigravityWindows(r.body);
  let plan = '';
  try {
    const t = await getJson(base + 'GetUserStatus', { ...opts, body: '{}' });
    const u = t.status === 200 && t.body && t.body.userStatus;
    plan = u && (u.userTier && u.userTier.name || u.planStatus && u.planStatus.planInfo && u.planStatus.planInfo.planName) || '';
  } catch { }
  return { plan, windows };
}

// ---------- Grok Bot ----------
// Grok Bot 跑在 Cursor 的后台上，用的是同一个 Cursor 账号：DashboardService/GetSandUsageStatus 回它这一周用了多少、什么时候重置
// （Grok Bot 自己的用量页读的也是它；2026-10-27 从它的程序包里查到）。
async function grokbot() {
  const tok = cursorToken().tok;
  const r = await getJson('https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus', {
    method: 'POST', body: '{}',
    headers: { Authorization: 'Bearer ' + tok, 'Content-Type': 'application/json', 'Connect-Protocol-Version': '1' },
  });
  const b = r.body;
  if (r.status !== 200 || !b || typeof b.usagePercent !== 'number') throw new Error('Grok Bot 用量接口回 ' + r.status);
  return {
    plan: '', limitReached: b.hasAvailableUsage === false,
    windows: [{ key: 'week', label: '每周', used: pct(b.usagePercent), resetsAt: isoMs(b.nextResetTimestampUtc) }],
  };
}

// ---------- DeepSeek ----------
function deepseekKeys() {
  const keys = [];
  const cfg = readJson(path.join(HOME, '.config', 'opencode', 'opencode.json'));
  const k = cfg && cfg.provider && cfg.provider.deepseek && cfg.provider.deepseek.options && cfg.provider.deepseek.options.apiKey;
  if (typeof k === 'string' && k && !k.startsWith('{env:')) keys.push(k);
  if (process.env.DEEPSEEK_API_KEY) keys.push(process.env.DEEPSEEK_API_KEY);
  return keys;
}

async function deepseek() {
  const keys = deepseekKeys();
  if (keys.length === 0) throw new Error('本机没有 DeepSeek 的 key');
  let status = 0;
  for (const key of keys) {   // 环境变量里那把可能已经作废（2026-10-27 就是），一把不行换下一把
    const r = await getJson('https://api.deepseek.com/user/balance', { headers: { Authorization: 'Bearer ' + key } });
    status = r.status;
    const b = r.body && r.body.balance_infos && r.body.balance_infos[0];
    if (r.status !== 200 || !b) continue;
    const num = v => Math.round(parseFloat(v || '0') * 100) / 100;
    return {
      plan: '按量付费', windows: [], limitReached: r.body.is_available === false,
      balance: { currency: b.currency || 'CNY', total: num(b.total_balance), granted: num(b.granted_balance), toppedUp: num(b.topped_up_balance) },
    };
  }
  throw new Error('DeepSeek 余额接口回 ' + status);
}

// ---------- Colab（跟 Antigravity 同属 Google AI Pro） ----------
// 用户 11-09：「Googlepro订阅里面的colab也可以注明出来」「甚至比订阅本身的模型还重要」。
// 官方 Colab CLI 装在 WSL Ubuntu 里：colab usage 打出「Current balance: 248.12 compute units」「Usage rate: 0.00/hr」。
// 一小时最多读一次（缓存在上一份 quota.json 里）；WSL 没在跑就不读（别为一个数把 4GB 的 WSL 拉起来），留旧数。
const COLAB_EVERY = 3600000;
function parseColab(text) {
  const m = /Current balance:\s*([\d.]+)\s*compute units/i.exec(text || '');
  if (!m) return null;
  const r = /Usage rate:\s*([\d.]+)\s*\/\s*hr/i.exec(text);
  const n = /Active assignments:\s*(\d+)/i.exec(text);
  return { units: parseFloat(m[1]), rate: r ? parseFloat(r[1]) : null, active: n ? parseInt(n[1], 10) : null, at: Date.now() };
}

function run(exe, args, timeout) {
  const { execFile } = require('child_process');
  return new Promise(res => execFile(exe, args, { windowsHide: true, timeout, maxBuffer: 1 << 20, encoding: 'buffer' },
    (err, out) => res({ err, out: out || Buffer.alloc(0) })));
}

async function colab(prev) {
  if (prev && prev.at && Date.now() - prev.at < COLAB_EVERY && !process.argv.includes('--colab')) return prev;
  // wsl -l 打的是 UTF-16
  const l = await run('wsl.exe', ['-l', '--running', '-q'], 15000);
  if (!/Ubuntu-24\.04/.test(l.out.toString('utf16le').replace(/\0/g, ''))) return prev ? Object.assign({}, prev, { stale: true }) : null;
  const r = await run('wsl.exe', ['-d', 'Ubuntu-24.04', '-u', 'dev', '--', 'bash', '-lc', 'colab usage'], 45000);
  const got = parseColab(r.out.toString('utf8'));
  if (got) return got;
  return prev ? Object.assign({}, prev, { stale: true }) : null;
}

const READERS = { claude, codex, cursor, 'opencode-go': opencodeGo, antigravity, grokbot, deepseek };

// 订阅还在不在（2026-11-09 用户：「按照额度来判断」「发现订阅没有了就自动去掉显示」「有新的订阅被发现了就自动加上」）。
//   只看读成功时官方回的套餐名：免费档 = 没订阅；读不到、套餐名空着、按量付费、接口不报套餐的不判（返回 null），免得网一抖就藏掉。
//   Grok Bot 跟着 Cursor 账号走：Cursor 那边是免费档就算它也没了。
const FREE = /^(free|hobby|免费)\b/i;
function subscribed(id, r, results) {
  if (!r || r.error || r.stale) return null;
  switch (id) {
    case 'claude': case 'codex': case 'cursor': case 'antigravity':
      // 只认官方明说的免费档；套餐名空着（接口没给 / 退回旧会话记录）不判
      return r.plan ? !FREE.test(r.plan) : null;
    case 'grokbot': {
      const c = results.cursor;
      return c && !c.error ? subscribed('cursor', c, results) : null;
    }
    default: return null;
  }
}

async function main() {
  fs.mkdirSync(BASE, { recursive: true });
  let accounts = readJson(ACCOUNTS);
  let dirty = !Array.isArray(accounts);
  if (dirty) accounts = [];
  for (const d of DEFAULTS) if (!accounts.some(a => a && a.id === d.id)) { accounts.push(d); dirty = true; }
  if (dirty) try { writeAtomic(ACCOUNTS, JSON.stringify(accounts, null, 2) + '\n'); } catch { }
  const prev = readJson(OUT) || {};
  const prevById = {};
  for (const a of prev.accounts || []) prevById[a.id] = a;

  // 藏起来的也照样读：要知道它哪天重新订了，好自动放回来
  const live = accounts.filter(a => a && a.id);
  const raw = {};
  const ag = prevById.antigravity;
  const colabP = live.some(a => a.id === 'antigravity') ? colab(ag && ag.colab).catch(() => ag && ag.colab || null) : Promise.resolve(null);
  await Promise.all(live.map(async a => {
    const read = READERS[a.id];
    if (!read) return;
    try { raw[a.id] = await read(); } catch (e) { raw[a.id] = { error: String(e && e.message || e) }; }
  }));

  const colabNow = await colabP;
  let changed = false;
  const out = live.map(a => {
    const base = { id: a.id, icon: a.icon || a.id, name: a.name || a.id, price: a.price ?? null, currency: a.currency || 'USD', url: a.url || '', renewDay: a.renewDay || null, note: a.note || '' };
    const r = raw[a.id];
    let o;
    if (!READERS[a.id]) o = Object.assign(base, { manual: true, plan: a.plan || '', windows: (a.windows || []).map(w => Object.assign({}, w)) });
    else if (!r.error) o = Object.assign(base, r, { ok: true, at: Date.now(), plan: a.plan || r.plan });
    else {
      // 读失败时留着上一次的数，标上是什么时候的
      const old = prevById[a.id];
      o = Object.assign(base, old && old.windows ? { windows: old.windows, plan: old.plan, at: old.at } : { windows: [] },
        { ok: false, error: r.error });
    }
    if (a.id === 'antigravity') { const c = colabNow; if (c) o.colab = c; else delete o.colab; }
    const sub = subscribed(a.id, r, raw);
    // 手动藏的：订阅停过一次又续上了 → 自动放回（停过才算，免得刚藏起来的马上又被放回）
    if (a.hidden && sub === false && !a.lapsedSeen) { a.lapsedSeen = true; changed = true; }
    if (a.hidden && sub === true && a.lapsedSeen) { delete a.hidden; delete a.lapsedSeen; changed = true; }
    if (!a.hidden && a.lapsedSeen) { delete a.lapsedSeen; changed = true; }
    if (a.hidden) o.hidden = true;
    // 没手动藏、但读到没订阅：自动收起（面板底下「已隐藏」里写着「没订阅」）；用户点了「显示」的（keepShown）照样列
    else if (sub === false && !a.keepShown) o.inactive = true;
    return o;
  });
  if (changed) try { writeAtomic(ACCOUNTS, JSON.stringify(accounts, null, 2) + '\n'); } catch { }
  writeAtomic(OUT, JSON.stringify({ updated: Date.now(), accounts: out }, null, 2));
  if (process.argv.includes('--print')) console.log(JSON.stringify(out, null, 2));
}

if (require.main === module) main().catch(e => { console.error(e && e.stack || e); process.exit(1); });
module.exports = { antigravity, antigravityWindows, parseColab };
