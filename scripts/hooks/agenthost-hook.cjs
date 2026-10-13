// 虚拟窗口的钩子小程序：各家 agent（Claude / Codex / Cursor / Antigravity …）的官方钩子调它，
// 它把「这个会话现在在干什么」写成一个小文件，虚拟窗口（宿主）去读。
//
//   node agenthost-hook.cjs <kind> [事件名]   事件 JSON 从 stdin 进来（各家的钩子都这么传）；
//                                            载荷里不带事件名的（Antigravity）安装时把事件名写在第二个参数
//
// 只做观察：不拦截、不改输入、不往模型看得见的地方写字。标准输出一律不写 ——
// 唯一例外是 Cursor 的 beforeSubmitPrompt：它要求回一句 {"continue":true}，不回会卡住提交。
// 需要回答「放行 / 拦截」的事件（Cursor 的 preToolUse / beforeShellExecution …）根本不注册，
// 免得替用户批准命令。
//
// 事件 → 状态的对照照抄 Que（github.com/ZIXT233/Que，MIT）docs/harness/hook-api.md 第 3 节。
//
// 写到 %LOCALAPPDATA%\AgentHost\sessions\<kind>-<会话id>.json：
//   { kind, sessionId, cwd, event, state, tool, at, slot, entry, reply [, review] }
// state：working / asking（在问你、等你批准）/ done（这一轮做完了）/ stopped（会话结束）
// review：只有 Codex 的 PermissionRequest 有 —— 这次请求归谁批（见 codexReviewer）
// slot：宿主自己起的 CLI 会话带 AGENTHOST_SLOT 环境变量，顺手也写 cli\<slot>.txt（旧接口）。
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const kind = (process.argv[2] || '').toLowerCase();
const cursorKind = kind === 'cursor';

// 各家的「问你」形态工具：它一被调用就是在问你，跟权限门禁无关
const ASK_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode', 'request_user_input', 'ask_user_question', 'ask_user', 'ask_question']);
const TURN_START = new Set(['UserPromptSubmit', 'beforeSubmitPrompt', 'BeforeAgent', 'PreInvocation']);
const WORKING = new Set(['PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'BeforeTool', 'AfterTool', 'PostInvocation', 'postToolUse', 'postToolUseFailure']);
const TURN_END = new Set(['Stop', 'stop', 'StopFailure', 'StopCancelled', 'AfterAgent', 'afterAgentResponse']);
const SESSION_END = new Set(['SessionEnd', 'sessionEnd']);

function pick(o, ...keys) {
  for (const k of keys) {
    const v = k.split('.').reduce((a, p) => (a && typeof a === 'object' ? a[p] : undefined), o);
    if (typeof v === 'string' && v) return v;
  }
  return '';
}

// Codex 的「请求批准」（PermissionRequest）不一定是问你。它的每个对话有一项 approvals_reviewer，定批准归谁：
//   user          弹给你批 —— 这才是「在问你」；
//   auto_review   它自己的审查模型批（旧名 guardian_subagent）：十几秒到几十秒后自己放行或驳回，驳回了也只是让主模型
//                 换条路，不会转来问你。
// 事件是在「要去批」的那一刻发的，不管归谁批都发；以前一律记成「在问你」—— 开着自动审查跑长任务，灯隔一会儿黄一下，
// 队列视图还把它换到中间（用户 2026-10-12 报；当天两小时里 70 次，中位数 15 秒，换到中间 8 次）。
// 载荷里没有这一项（只有 permission_mode），只能从对话记录（transcript_path）里找最近的一条设置：每轮开头的
// turn_context、改设置时的 thread_settings_applied 都带着它，一行一条 JSON。从文件尾一块块往前找，找到就停。
// 记录可以很大（看过图的对话几百兆），两条设置之间隔二三十兆的也有：最多往前看 64MB（本机整份 485MB 扫一遍 0.65 秒；
// 平常设置就在最后几十 KB 里，读一块就够）。只找没被转义的那种写法 —— 模型的话、工具输出里引用到它时在 JSON 字符串里，
// 引号前带反斜杠，不算。
// 找不到、读不了返回空串：当成要你批（宁可多亮一次黄灯，也别该问你的时候不亮）。
const REVIEWER = /"approvals_reviewer"\s*:\s*"([A-Za-z_]{1,40})"/g;
const SCAN_FIRST = 256 << 10, SCAN_STEP = 8 << 20, SCAN_MAX = 64 << 20, SCAN_OVERLAP = 128;

function codexReviewer(p) {
  const tp = pick(p, 'transcript_path', 'transcriptPath');
  if (!tp) return '';
  let fd;
  try {
    fd = fs.openSync(tp, 'r');
    const size = fs.fstatSync(fd).size;
    const floor = Math.max(0, size - SCAN_MAX);
    let end = size, chunk = SCAN_FIRST;
    while (end > floor) {
      const start = Math.max(floor, end - chunk);
      // 往后多带一小段（上一轮读过的那一块的开头）：正好跨在两块接缝上的那一条也认得出
      const len = Math.min(size, end + SCAN_OVERLAP) - start;
      const buf = Buffer.allocUnsafe(len);
      const got = fs.readSync(fd, buf, 0, len, start);
      // 只找一段 ASCII：按 latin1 解，不用管一个汉字被切成两半
      const text = buf.latin1Slice(0, got);
      let m, last = '';
      REVIEWER.lastIndex = 0;
      while ((m = REVIEWER.exec(text))) last = m[1];
      if (last) return last;
      end = start;
      chunk = Math.min(chunk * 4, SCAN_STEP);
    }
  } catch {} finally {
    if (fd !== undefined) { try { fs.closeSync(fd); } catch {} }
  }
  return '';
}

// Claude 的 Stop 带着一份 background_tasks：这一轮的话说完了，可它派出去的活还有哪些在跑。
// 里面有子 agent、workflow、队友、云端会话在跑：跑完会把这个会话叫醒接着干 —— 这是「停下来等后台」，不是做完了。
// 以前一律记成做完：长任务派给后台 agent 之后灯就黄了（做完了没看），队列视图还把它换到中间，其实它还在干
// （用户 2026-10-12：「很多次在执行长任务的时候灯就变黄了」）。Claude 自己也是这么分的：后台 agent 还在跑时，
// 它不发「闲着了」的通知（idle_prompt）。
// 后台的命令行（shell）、监视（monitor）、外接工具的长任务（MCP task）不算：开发服务器、tail -f 这种可以一直挂着，
// 算进去这个会话就永远不会「做完」。
const BG_AGENT = new Set(['subagent', 'workflow', 'teammate', 'cloud session']);
const BG_OVER = /^(completed|complete|done|failed|error|killed|cancell?ed|stopped)$/i;

function waitingOnBackground(p) {
  const list = p && Array.isArray(p.background_tasks) ? p.background_tasks : [];
  return list.some(t => t && typeof t === 'object'
    && BG_AGENT.has(String(t.type || '').toLowerCase()) && !BG_OVER.test(String(t.status || '')));
}

// review：这次请求批准归谁批（只有 Codex 查得到，别家是空串）
function stateOf(event, p, review) {
  const tool = pick(p, 'tool_name', 'toolName', 'toolCall.name', 'tool.name', 'name');
  if ((event === 'PreToolUse' || event === 'BeforeTool' || event === 'preToolUse') && ASK_TOOLS.has(tool)) return 'asking';
  // 归它自己的审查模型批：不是问你，还算在干活
  if (event === 'PermissionRequest') return review && review !== 'user' ? 'working' : 'asking';
  if (event === 'Notification') {
    const t = pick(p, 'notification_type', 'notificationType', 'type');
    if (t === 'permission_prompt' || t === 'ToolPermission') return 'asking';
    if (t === 'idle_prompt') return 'done';
    // 老版本 Claude 的 Notification 不带类型：看文字
    const msg = pick(p, 'message');
    if (/permission|批准|允许/i.test(msg)) return 'asking';
    return null;
  }
  // Antigravity 的 Stop 带 fullyIdle=false 是半路暂停，不是做完
  if (event === 'Stop' && kind === 'antigravity' && p && p.fullyIdle === false) return 'working';
  // 这一轮说完了，后台还有它派出去的 agent 在跑：停下来等，不是做完
  if (event === 'Stop' && waitingOnBackground(p)) return 'working';
  if (TURN_START.has(event) || WORKING.has(event)) return 'working';
  if (TURN_END.has(event)) return 'done';
  if (SESSION_END.has(event)) return 'stopped';
  return null;   // SessionStart 之类：不改状态
}

// 这一轮最后说的话（给读这些文件的外部程序用，截 2000 字）：
// Codex 的 Stop 直接带 last_assistant_message；Cursor 的 afterAgentResponse 带 text；
// Claude 的 Stop 不带，从对话记录末尾找最后一条助手文字。
function replyOf(event, p) {
  const direct = pick(p, 'last_assistant_message', 'lastAssistantMessage', 'text', 'response');
  if (direct) return direct.slice(0, 2000);
  const tp = pick(p, 'transcript_path', 'transcriptPath');
  if (!tp || kind !== 'claude') return '';
  try {
    const fd = fs.openSync(tp, 'r');
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, 512 * 1024);
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, size - len);
    fs.closeSync(fd);
    const lines = buf.toString('utf8').split('\n');
    for (let i = lines.length - 1; i >= 0; i--) {
      const l = lines[i];
      if (!l.includes('"type":"assistant"') || !l.includes('"type":"text"')) continue;
      try {
        const m = JSON.parse(l).message;
        const txt = (m && Array.isArray(m.content) ? m.content : []).filter(c => c.type === 'text').map(c => c.text).join('\n').trim();
        if (txt) return txt.slice(0, 2000);
      } catch {}
    }
  } catch {}
  return '';
}

function readRec(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

// 钩子进程里没有别的事要做，同步等就行
function sleep(ms) {
  try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); }
  catch { const end = Date.now() + ms; while (Date.now() < end); }
}

// 「写 .tmp → 改名盖上去」的后一半。改名那一下只要别的进程正开着目标文件就失败（EPERM；对方带不带
// 「允许删」打开都一样）—— 宿主每 2 秒把变了的读一遍、同一会话并发的另一个钩子进程先读上一条记录、外部程序也可能在读。
// 以前异常被吞掉：这次更新丢了，.tmp 留在目录里；丢的要是 Stop，那一格的灯就停在 working（2026-10-12 查到）。
// 所以失败了再试几次。别人读一次这种小文件约 1 毫秒、偶尔十来毫秒（本机实测，开着实时防护）：
// 头两次不等、马上再试（失败的一次改名加上重读也要一两毫秒），之后才真的等。系统计时一拍 15.6 毫秒，
// 写得再小也是等一拍。加起来最多等 0.25 秒就放弃 —— Antigravity 的钩子超时只有 2 秒，Cursor 要等我们
// 回话才提交。实在盖不上就把 .tmp 删掉，别留垃圾。
const BUSY = new Set(['EPERM', 'EBUSY', 'EACCES']);
const WAITS = [0, 0, 15, 15, 30, 60, 100];

function replace(tmp, file, rec, changes) {
  for (let i = 0; ; i++) {
    try { fs.renameSync(tmp, file); return; } catch (e) {
      if (!BUSY.has(e && e.code) || i >= WAITS.length) break;
    }
    if (WAITS[i]) sleep(WAITS[i]);
    // 等的这一会儿，别的钩子进程可能已经写进更新的一条：不拿旧的去盖新的
    //（旧的 working 盖掉后来的 done，灯一样停在 working）。不改状态的事件本来就只在还没有记录时才写：有了就算了。
    const cur = readRec(file);
    if (cur && (!changes || cur.at > rec.at)) break;
  }
  try { fs.unlinkSync(tmp); } catch {}
}

function main(raw) {
  let p = {};
  // Cursor 在 Windows 上传进来的 JSON 开头带 BOM，JSON.parse 直接失败 —— 以前它的记录一条都没写出来
  raw = (raw || '').replace(/^﻿/, '');
  try { p = raw && raw.trim() ? JSON.parse(raw) : {}; } catch { p = {}; }
  // Antigravity 的载荷里没有事件名，安装时把事件名写在命令行第二个参数里
  const event = process.argv[3] || pick(p, 'hook_event_name', 'hookEventName', 'event');
  if (!kind || !event) return;
  // Cursor 也会读 ~/.claude/settings.json 跑 Claude 的钩子：那是 Cursor 的会话，别记成 Claude
  if (kind === 'claude' && p.cursor_version) return;
  // Antigravity 一个对话只认 conversationId（它的 brain 目录名）；偶尔来一条空载荷的 PostToolUse，
  // 记成 nosession 会被当成「最新的桌面会话」，别记
  const sessionRaw = kind === 'antigravity'
    ? pick(p, 'conversationId', 'conversation_id')
    : pick(p, 'session_id', 'sessionId', 'conversation_id', 'conversationId');
  if (kind === 'antigravity' && !sessionRaw) return;
  const sessionId = /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$/.test(sessionRaw) ? sessionRaw : 'nosession';
  const base = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AgentHost');
  const dir = path.join(base, 'sessions');
  const file = path.join(dir, kind + '-' + sessionId + '.json');
  const prev = readRec(file);
  // 子 agent 的事件不算一张卡片：不新建记录，不改主对话的状态、事件、工具。
  // 但它说明这个会话还有活在跑 —— 主对话记着「在干活」的话把时刻刷新一下。不然子 agent / workflow 一跑二十几分钟
  // （这段时间主对话自己一条事件都没有），宿主会当它没动静、闲着了。主对话已经做完、正在问你的不动。
  if (p.agent_id || p.agentId || p.subagent_id) {
    if (prev && prev.state === 'working') {
      const beat = Object.assign({}, prev, { at: Date.now() });
      const t = file + '.' + process.pid + '.tmp';
      try {
        fs.writeFileSync(t, JSON.stringify(beat));
        // 写之前再看一眼：就这一两毫秒里主对话自己来了一条（做完了、在问你），别拿刷新的把它盖回「在干活」
        const cur = readRec(file);
        if (cur && cur.state === 'working' && cur.at <= beat.at) replace(t, file, beat, true);
        else fs.unlinkSync(t);
      } catch { try { fs.unlinkSync(t); } catch {} }
    }
    return;
  }
  const review = kind === 'codex' && event === 'PermissionRequest' ? codexReviewer(p) : '';
  const state = stateOf(event, p, review);
  if (state === null && prev) return;   // 不改状态的事件，已有记录就别动
  const slot = process.env.AGENTHOST_SLOT || '';
  const cwd = pick(p, 'cwd') || (Array.isArray(p.workspace_roots) && p.workspace_roots[0]) || process.cwd();
  const st = state || (prev && prev.state) || 'done';
  // 在问你时来的事件不一定带工具名（Claude 弹出批准框之后还会来一条 Notification）：沿用上一条的。
  // 宿主的悬停提示靠它说「等你批准什么」，冲成空就分不出是批准、选择题还是批计划。
  const tool = pick(p, 'tool_name', 'toolName', 'toolCall.name', 'tool.name') || (st === 'asking' && prev ? prev.tool || '' : '');
  // 这一轮做完时它最后说的话；别的事件沿用上一次记下的。
  // 做完之后又来一条不带正文的结束事件（Cursor 先 afterAgentResponse 带正文、后 stop 不带）：别把刚记下的冲掉。
  const said = TURN_END.has(event) ? replyOf(event, p) : '';
  const reply = TURN_END.has(event)
    ? (said || (prev && prev.state === 'done' ? prev.reply || '' : ''))
    : ((prev && prev.reply) || '');
  const rec = {
    kind, sessionId, cwd, event,
    state: st,
    tool,
    at: Date.now(),
    slot,
    // 桌面端还是命令行：Claude 桌面端会带 CLAUDE_CODE_ENTRYPOINT=claude-desktop
    // 只给 Claude 记：别家的钩子要是从 Claude 里起的进程继承了这个变量，会被错认成 Claude 桌面端
    entry: kind === 'claude' ? (process.env.CLAUDE_CODE_ENTRYPOINT || '') : '',
    reply,
  };
  // 查到了归谁批就记下（查的时候看得见为什么亮 / 没亮黄灯）；别的事件不带
  if (review) rec.review = review;
  const tmp = file + '.' + process.pid + '.tmp';
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(tmp, JSON.stringify(rec));
    replace(tmp, file, rec, state !== null);
  } catch { try { fs.unlinkSync(tmp); } catch {} }
  if (slot && state) {
    try {
      const cliDir = path.join(base, 'cli');
      fs.mkdirSync(cliDir, { recursive: true });
      fs.writeFileSync(path.join(cliDir, slot.replace(/[^A-Za-z0-9_-]/g, '_') + '.txt'), state === 'asking' ? 'asking' : state);
    } catch {}
  }
  // 偶尔清一下一周没动的旧会话
  if (Math.random() < 0.02) {
    try {
      const week = Date.now() - 7 * 86400e3;
      for (const f of fs.readdirSync(dir)) {
        const fp = path.join(dir, f);
        try { if (fs.statSync(fp).mtimeMs < week) fs.unlinkSync(fp); } catch {}
      }
    } catch {}
  }
}

// stdin 读到头就处理；有的宿主不关 stdin，两秒兜底，别把对方的钩子拖到超时
let buf = '';
let done = false;
// 先写文件再回话：Cursor 一收到标准输出的回应就把钩子进程掐掉，先回话文件就来不及写
//（2026-09-25 实测：Cursor 调了几十次钩子、全部 exit 0，却一条记录都没有）。
function finish() {
  if (done) return; done = true;
  let event = '';
  try { main(buf); } catch {}
  if (cursorKind) {
    try { event = process.argv[3] || JSON.parse(buf.replace(/^﻿/, '') || '{}').hook_event_name || ''; } catch {}
    // Windows 上写管道是异步的：等写完再退，不然 {"continue":true} 可能丢，Cursor 会卡住提交
    try { process.stdout.write(JSON.stringify(event === 'beforeSubmitPrompt' ? { continue: true } : {}), () => process.exit(0)); } catch { process.exit(0); }
    setTimeout(() => process.exit(0), 500);
    return;
  }
  process.exit(0);
}
process.stdin.setEncoding('utf8');
process.stdin.on('data', d => { buf += d; });
process.stdin.on('end', finish);
process.stdin.on('error', finish);
setTimeout(finish, 2000);
