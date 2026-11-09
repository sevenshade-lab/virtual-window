// 虚拟窗口的钩子小程序：各家 agent（Claude / Codex / Cursor / Antigravity …）的官方钩子调它，
// 它把「这个会话现在在干什么」写成一个小文件，虚拟窗口（宿主）去读。
//
//   node agenthost-hook.cjs <kind> [事件名]   事件 JSON 从 stdin 进来（各家的钩子都这么传）；
//                                            载荷里不带事件名的（Antigravity）安装时把事件名写在第二个参数
//
// 不拦截、不改输入。标准输出只在两种时候写：
//   - Cursor 的 beforeSubmitPrompt：它要求回一句 {"continue":true}，不回会卡住提交；
//   - 这一家有别的 agent 留的话还没看（留言，见 agenthost-notes.cjs）：会话开始 / 用户发话时把它们交给模型（noteFor）；
//     它这一轮做完时有从没送到过的新留言，就把它叫住接着处理完再停（endNote）。
//     信箱空着就跟以前一样什么都不写。
// 会话派了活（建委托单、给别家留言、排进 Codex 主对话）的那一下工具调用，顺手替它记下要等的回音（agenthost-wake.cjs），
// 挂着等、等到了叫醒它的是另一条 Stop 钩子；这里不写标准输出。
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

// ---- 留言：别的 agent 留给这一家的话，趁会话开始 / 用户发话交给模型 ----
//
// 存取都在 agenthost-notes.cjs（装钩子时跟本文件释放到同一个目录；没有它就当没有留言这回事）。
// 各家把一段话交给模型的办法不一样，只在确实交得进去的事件上送：
//   Claude / Codex   SessionStart、UserPromptSubmit：回 hookSpecificOutput.additionalContext
//                    （Claude 桌面端 2026-10-12 实测收得到；Codex 照它的钩子文档，作为 developer 消息进对话）
//   Cursor           sessionStart：回 additional_context。文档说会进对话开头的上下文，论坛上有人报过某些版本不进 ——
//                    所以照送，但不记成「送到过」（记了它却没看到，这条留言就再也不会提了）；
//                    beforeSubmitPrompt 只能放行 / 拦截，交不了
//   Antigravity      PreInvocation：回 injectSteps，往对话里插一条系统消息（2026-10-12 拿它的命令行 agy 1.3.3 实测：
//                    {systemMessage:{systemMessage:"…"}} 和 {ephemeralMessage:"…"} 模型都看得到，systemMessage 直接给字符串的不行；
//                    用前一种，留在对话里，后面几步还看得到编号）。每次调模型之前都发这个事件，只在一轮的头一次送
//   OpenCode         走插件（agenthost-opencode.js），不经这里
// 不该收的会话不送 —— 它们收了，真正在干活的那个会话就看不到了：
//   - 子 agent、Cursor 代跑的 Claude 钩子；
//   - 无头调用：别的程序起来问一句话就走的（Claude 的入口是 sdk-*、Codex 的 exec）；起它的程序也可以带环境变量
//     AGENTHOST_NOTES=off 明说不要；
//   - Codex 自己派生的小对话：审查模型、子 agent（对话记录第一行的 source 是个对象），还有每轮做完后生成
//     「下一句建议」的那种（不落盘，没有对话记录可读）。
// 一条留言算处理完只有一个标准：有人标了已读。没标之前，这一家的每个会话各被告知一次（notes.forSession 记着
// 每个会话见过哪些）：头一次轮到（会话开始，或者钩子是半路装上的、那一家不发 SessionStart）没标已读的都提一遍，
// 之后只送新来的。不是「谁先碰上归谁」—— 上面那几种认不出来的无头调用碰上了，也只是多看一眼，拿不走。
let notes = null;
try { notes = require('./agenthost-notes.cjs'); } catch {}
// 等回音（agenthost-wake.cjs）：这个会话派了活（建委托单、给别家留言、排进 Codex 主对话），钩子替它记下要等什么；
// 真正挂着等、等到了叫醒它的是另一条 Stop 钩子（asyncRewake，只有 Claude 有）。没有这个脚本就当没这回事
let wake = null;
try { wake = require('./agenthost-wake.cjs'); } catch {}
// 长对话提醒（agenthost-hygiene.cjs）：一轮开头 / 结尾量一下上下文多长，过线（默认 25 万）就在送得进话的那个事件上
// 提醒它「做完手头的事；有后续先登进待办、再交接」。没有这个脚本就当没这回事
let hygiene = null;
try { hygiene = require('./agenthost-hygiene.cjs'); } catch {}
// 在哪个事件上量（一轮开头、结尾；Cursor 的 afterAgentResponse 带着这一轮的用量）
const HYG_MEASURE = {
  claude: new Set(['UserPromptSubmit', 'Stop']),
  codex: new Set(['UserPromptSubmit', 'Stop']),
  // 命令行版（cursor-agent）只发 postToolUse / sessionEnd，不发 afterAgentResponse / stop（11-07 实测）：工具调用后也量
  cursor: new Set(['afterAgentResponse', 'stop', 'postToolUse']),
  antigravity: new Set(['PreInvocation', 'Stop']),
};
// 在哪个事件上送提醒：Claude / Codex 用户发话时（additionalContext）；Antigravity 一轮头一次调模型前（injectSteps）；
// Cursor 的 beforeSubmitPrompt 交不进话（Cursor 11-07 确认），改在 postToolUse 用 additional_context 送
const HYG_SEND = { claude: 'UserPromptSubmit', codex: 'UserPromptSubmit', antigravity: 'PreInvocation', cursor: 'postToolUse' };
// main 量到的这个会话的长度（没量返回 null）
let measured = null;
// 这条事件来之前，这个会话记着的那条记录（main 读到就放在这儿；没有记录是 null）
let before = null;

//
// 还有一种送法：这一轮做完时（end）。它忙的工夫来了新留言、又一直没人跟它说话的话，上面两种时机都等不到 ——
// 所以它这一轮要停的时候看一眼：有从没送到过的新留言，就把它叫住接着处理完再停。各家叫住的办法：
//   Claude / Codex   Stop：回 {"decision":"block","reason":…}，reason 交给模型接着干（载荷里 stop_hook_active 为真
//                    说明它已经是被叫住之后又停的，不再叫）
//   Cursor           stop：回 {"followup_message":…}，它当成下一句用户的话自己发出去（loop_count > 0 说明已经是自己接的话；
//                    status 不是 completed 的 —— 被你按停、出错 —— 不接）。它是 Cursor 唯一一个每一轮都交得进话的口子
//   Antigravity      Stop：回 {"decision":"continue","reason":…}，reason 作为系统消息插进对话（fullyIdle=false 是半路暂停，不叫）
//   OpenCode         在插件里（会话闲下来时替它发一句），不经这里
// 一个会话一轮只叫住一次（notes.turnSpent / markTurn，用户再发话时清掉），而且只为「从没送到过的」叫 ——
// 一条留言最多叫住一个会话一次。放一个 notes\turnend.off 就只关这一种送法。
const NOTE_PHASE = {
  claude: { SessionStart: 'start', UserPromptSubmit: 'prompt', Stop: 'end' },
  codex: { SessionStart: 'start', UserPromptSubmit: 'prompt', Stop: 'end' },
  cursor: { sessionStart: 'start', stop: 'end' },
  antigravity: { PreInvocation: 'prompt', Stop: 'end' },
};
const NOTE_UNSURE = new Set(['cursor']);
// 做完时那一种送法送不送得到还没在真机上对上的：照送，但不记成「送到过」（跟 NOTE_UNSURE 一个道理）。
// Cursor 的 followup_message 是照它的钩子文档写的，桌面端还没亲眼见过
const END_UNSURE = new Set(['cursor']);
// 这次事件要把它叫住接着干：会话文件里别记成「做完了」（main 用）
let keepWorking = false;

function sessionOf(p) {
  const raw = kind === 'antigravity'
    ? pick(p, 'conversationId', 'conversation_id')
    : pick(p, 'session_id', 'sessionId', 'conversation_id', 'conversationId');
  return /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$/.test(raw) ? raw : '';
}

// Codex 对话记录的第一行：这条对话的身份（session_meta）。里面带着整段系统提示，可以有几十 KB，最多读 2MB 找换行。
// 读不到文件返回 null；读到了但认不出格式返回 {}（以后它改了格式也照送，不至于悄悄一条都不送）
function codexThread(p) {
  const tp = pick(p, 'transcript_path', 'transcriptPath');
  if (!tp) return null;
  let fd;
  try {
    fd = fs.openSync(tp, 'r');
    const parts = [];
    let pos = 0;
    while (pos < (2 << 20)) {
      const b = Buffer.allocUnsafe(64 << 10);
      const n = fs.readSync(fd, b, 0, b.length, pos);
      if (n <= 0) break;
      const got = b.subarray(0, n);
      const nl = got.indexOf(10);
      if (nl >= 0) { parts.push(got.subarray(0, nl)); break; }
      parts.push(got);
      pos += n;
    }
    try {
      const j = JSON.parse(Buffer.concat(parts).toString('utf8'));
      return j && j.payload && typeof j.payload === 'object' ? j.payload : {};
    } catch { return {}; }
  } catch { return null; } finally {
    if (fd !== undefined) { try { fs.closeSync(fd); } catch {} }
  }
}

// 这个会话不该收留言的原因；该收返回空串。
//   forced  起它的程序明说了要（AGENTHOST_NOTES=on）。只免掉「无头调用」这一条 —— 环境变量会一路传给它起的子 agent、
//           它派生的小对话，那些照样不收（独立审查实测：以前带着 on 的子 agent、Codex 的小对话、Cursor 代跑的钩子都收得到）
//   phase   这次是哪种时机（start / prompt / end）
function noteSkip(p, forced, phase) {
  if (/^(0|off|no|false)$/i.test(process.env.AGENTHOST_NOTES || '')) return 'AGENTHOST_NOTES=off';
  if (p.agent_id || p.agentId || p.subagent_id) return 'subagent';
  if (kind === 'claude') {
    if (p.cursor_version) return 'claude hook run by cursor';
    const entry = process.env.CLAUDE_CODE_ENTRYPOINT || '';
    if (!forced && (/^sdk/i.test(entry) || entry === 'mcp')) return 'headless (' + entry + ')';
  }
  if (kind === 'codex') {
    const t = codexThread(p);
    if (!t) return 'no transcript (helper thread)';
    if (t.source && typeof t.source === 'object') return 'spawned thread';
    if (!forced && t.source === 'exec') return 'headless (exec)';
  }
  // Antigravity 每次调模型之前都发 PreInvocation：只在一轮的头一次送，别在它干到一半时插话。
  // 头一次 = 这是第 0 次调用，或者这条事件来之前它不在干活（上一轮做完了、还没有记录）——
  // 后一条是防着调用次数是整个对话累计的、不是每轮从 0 数。只管「用户发话」那个时机（before 是 main 读到的上一条记录）
  if (phase === 'prompt' && kind === 'antigravity' && Number(p.invocationNum) > 0 && before && before.state === 'working') return 'mid-turn';
  return '';
}

// 这一下工具调用是不是在用留言：给别家留言、看信箱、标已读、问别家的那几个工具，或者直接在命令行里调留言脚本
//（没有那几个工具的家、工具进程还是旧的）。认错了也不要紧 —— 只是让这个会话在做完时也可以被叫住（见 notes.holdable）
const NOTE_TOOL = /agent[-_]?bus|agent_(send|inbox|ack|peers)|ask_agent/i;
function usedNotes(p, raw) {
  if (NOTE_TOOL.test(pick(p, 'tool_name', 'toolName', 'toolCall.name', 'tool.name'))) return true;
  return raw.length <= 262144 && raw.indexOf('agenthost-notes.cjs') >= 0;
}

function noteReply(event, text) {
  if (cursorKind) return { additional_context: text };
  // Antigravity：往对话里插一条系统消息
  if (kind === 'antigravity') return { injectSteps: [{ systemMessage: { systemMessage: text } }] };
  return { hookSpecificOutput: { hookEventName: event, additionalContext: text } };
}

// 回应写成纯 ASCII 的 JSON（汉字写成 \uXXXX）：有的家是经 PowerShell 起的钩子（Codex），标准输出按系统代码页转一道，
// 直接写 UTF-8 的汉字到了它那儿可能是乱码或问号；转义之后哪种代码页都原样。
function asciiJson(o) {
  return JSON.stringify(o).replace(/[\u007f-\uffff]/g, ch => '\\u' + ch.charCodeAt(0).toString(16).padStart(4, '0'));
}

// 这一轮要停了，可这次停不是「做完了、可以接着干别的」的那种；是就返回原因
function stopSkip(p) {
  if (kind === 'cursor') {
    // 明说了是做完的才接话：被你按停的、出错的不接；连 status 都没带的也不接（不知道它是怎么停的）
    const status = pick(p, 'status');
    if (status !== 'completed') return 'status ' + (status || 'missing');
    if (Number(p.loop_count) > 0) return 'already a follow-up (loop_count)';
    return '';
  }
  if (kind === 'antigravity') return p.fullyIdle === false ? 'not fully idle' : '';
  return p.stop_hook_active === true ? 'stop hook already active' : '';
}

function endReply(text) {
  if (cursorKind) return { followup_message: text };
  if (kind === 'antigravity') return { decision: 'continue', reason: text };
  return { decision: 'block', reason: text };
}

// 这一轮做完了：有从没送到过的新留言，就把它叫住接着处理。要叫住返回那条回应，不叫返回 null
function endNote(event, p) {
  if (typeof notes.turnSpent !== 'function') return null;   // 留言脚本是旧的（没跟着一起换）：当没有这种送法
  const c = notes.counts(kind);
  if (!c.new) return null;                      // 没有从没送到过的：最常见的情况，到此为止
  const session = sessionOf(p);
  if (!session) return null;
  const log = what => notes.logDelivery(kind, event, session, what + ' (new ' + c.new + ', open ' + c.open + ')');
  if (notes.deliveryOff(kind)) { log('skip: deliver.off'); return null; }
  if (notes.turnEndOff(kind)) { log('skip: turnend.off'); return null; }
  const forced = /^(1|on|yes|true)$/i.test(process.env.AGENTHOST_NOTES || '');
  const why = noteSkip(p, forced, 'end') || stopSkip(p);
  if (why) { log('skip: ' + why); return null; }
  if (notes.turnSpent(kind, session)) { log('skip: already held once this turn'); return null; }
  // 同一家开着几个会话：留给这一阵在跟别家打交道的那个（它不来的话，留言照常在下次有人发话时送）
  if (typeof notes.holdable === 'function' && !notes.holdable(kind, session)) {
    log('skip: left for the session of this agent that has been using notes');
    return null;
  }
  const r = notes.forSession(kind, session, { end: true, unsure: END_UNSURE.has(kind) });
  if (!r.text) return null;
  notes.markTurn(kind, session);
  if (wake && typeof wake.remember === 'function') wake.remember(kind, session, r.shown);
  log('held at turn end: sent ' + r.shown.length + ' of ' + r.total + ' unread'
    + (r.moved && r.moved.length ? ', ' + r.moved.length + ' now open' : '') + (forced ? ' (forced)' : ''));
  return endReply(r.text);
}

// 这个事件要不要带上留言。要就返回那条回应（写到标准输出），不要返回 null
function noteFor(event, p) {
  if (!notes) return null;
  const phase = NOTE_PHASE[kind] && NOTE_PHASE[kind][event];
  if (!phase) return null;
  if (phase === 'end') return endNote(event, p);
  const c = notes.counts(kind);
  if (!c.new && !c.open) return null;           // 信箱空着：最常见的情况，到此为止
  const session = sessionOf(p);
  const log = what => notes.logDelivery(kind, event, session, what + ' (new ' + c.new + ', open ' + c.open + ')');
  // 没送的原因只在还有「没送到过的」时记：不然每个不该收的小对话每发一句话都记一行
  if (notes.deliveryOff(kind)) { if (c.new) log('skip: deliver.off'); return null; }
  // 起它的程序明说了要（AGENTHOST_NOTES=on）：无头的也送 —— 专门起来替这一家处理留言的就是这种
  const forced = /^(1|on|yes|true)$/i.test(process.env.AGENTHOST_NOTES || '');
  const why = noteSkip(p, forced, phase);
  if (why) { if (c.new) log('skip: ' + why); return null; }
  // 这个会话还没见过哪些、头一次轮到要不要都提一遍、记成送到过：都在 forSession 里
  const r = notes.forSession(kind, session, { start: phase === 'start', unsure: NOTE_UNSURE.has(kind) });
  if (!r.text) return null;
  // 念给它了：它在等回音的话记一笔，回音要是就在这里头，就不用再为它叫醒一次
  if (wake && typeof wake.remember === 'function') wake.remember(kind, session, r.shown);
  log('sent ' + r.shown.length + ' of ' + r.total + ' unread' + (r.stale ? ', ' + r.stale + ' stale' : '')
    + (r.moved && r.moved.length ? ', ' + r.moved.length + ' now open' : '') + (forced ? ' (forced)' : ''));
  return noteReply(event, r.text);
}

// 这次要不要带上长对话提醒：要就返回提醒正文，不要返回 ''。量是 main 做的（measured），这里只看该不该送
function hygieneFor(event, p) {
  if (!hygiene || HYG_SEND[kind] !== event) return '';
  const session = sessionOf(p);
  if (!session) return '';
  // 跟留言一样：子 agent、Cursor 代跑的 Claude 钩子、无头调用、Antigravity 一轮中间都不送
  // 起它的程序明说了要（AGENTHOST_NOTES=on）：无头的也送（拿命令行实测送不送得到时用）
  if (noteSkip(p, /^(1|on|yes|true)$/i.test(process.env.AGENTHOST_NOTES || ''), 'prompt')) return '';
  const rec = hygiene.load(kind, session);
  if (!hygiene.due(rec)) return '';
  hygiene.markWarned(rec);
  return hygiene.reminder(rec);
}

// 留言的回应里再并进一段话（长对话提醒）；没有留言就单独成一条回应
function withText(event, note, text) {
  if (!text) return note;
  if (!note) return noteReply(event, text);
  if (note.hookSpecificOutput) note.hookSpecificOutput.additionalContext = (note.hookSpecificOutput.additionalContext || '') + '\n\n' + text;
  else if (Array.isArray(note.injectSteps)) note.injectSteps.push({ systemMessage: { systemMessage: text } });
  else if ('additional_context' in note) note.additional_context = (note.additional_context || '') + '\n\n' + text;
  return note;
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
  before = prev;
  // 这个会话在用留言（它自己或者它的子 agent 调了那几个工具）：记一下，做完时优先叫住它；会话结束了就撤掉
  if (notes && typeof notes.markTalk === 'function' && sessionId !== 'nosession') {
    if (SESSION_END.has(event)) notes.clearTalk(kind, sessionId);
    else if (WORKING.has(event) && usedNotes(p, raw)) notes.markTalk(kind, sessionId);
  }
  // 派活 / 看留言的那一下工具调用：登记这个会话要等的回音（见 agenthost-wake.cjs）。子 agent 派的活也算主对话的
  if (wake && event === 'PostToolUse' && sessionId !== 'nosession') {
    try { wake.onTool(kind, sessionId, p); } catch {}
  }
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
  // 用户又发话了：这个会话这一轮还没被叫住过（见 endNote）。Antigravity 每次调模型之前都发 PreInvocation，
  // 它没有别的「已经被叫住过」的标记，全靠这个记号：只要上一条记录还是「在干活」就不清 —— 被叫住时记的正是「在干活」，
  // 所以被叫住之后接着调模型的那几次怎么编号（从 0 重新数也好、接着数也好）都清不掉它，要等这一轮真做完、用户再发话
  if (notes && typeof notes.clearTurn === 'function' && TURN_START.has(event) && sessionId !== 'nosession'
    && !(kind === 'antigravity' && prev && prev.state === 'working')) {
    notes.clearTurn(kind, sessionId);
  }
  // 查「某一家量不到长度」用：放一个 hygiene\dump.on，就把这家载荷里有哪些键、数字字段是多少记到 hygiene\dump.log（不记正文）
  if (hygiene && fs.existsSync(path.join(hygiene.DIR, 'dump.on'))) {
    try {
      const nums = {};
      for (const k of Object.keys(p)) if (typeof p[k] === 'number' || typeof p[k] === 'boolean') nums[k] = p[k];
      fs.appendFileSync(path.join(hygiene.DIR, 'dump.log'), JSON.stringify({ at: Date.now(), kind, event, session: sessionId, keys: Object.keys(p), nums,
        transcript: pick(p, 'transcript_path', 'transcriptPath'), cwd: pick(p, 'cwd'), roots: p.workspace_roots }) + '\n');
    } catch {}
  }
  // 长对话：一轮开头 / 结尾量一下（Antigravity 一轮中间每次调模型都发 PreInvocation，只量头一次）
  if (hygiene && sessionId !== 'nosession' && HYG_MEASURE[kind] && HYG_MEASURE[kind].has(event)
    && !(kind === 'antigravity' && event === 'PreInvocation' && Number(p.invocationNum) > 0 && prev && prev.state === 'working')) {
    try {
      const m = hygiene.measure(kind, sessionId, p);
      const where = pick(p, 'cwd') || (Array.isArray(p.workspace_roots) && p.workspace_roots[0]) || process.cwd();
      if (m.tokens) { hygiene.record(kind, sessionId, m, { cwd: where }); measured = m; }
    } catch {}
  }
  const review = kind === 'codex' && event === 'PermissionRequest' ? codexReviewer(p) : '';
  let state = stateOf(event, p, review);
  // 这次停被叫住了、要接着处理留言：不算做完，还在干活
  if (keepWorking && TURN_END.has(event)) state = 'working';
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
  // 上下文多长（宿主悬停提示用）、对话记录在哪（待办脚本读对话标题用）：没量的事件沿用上一条
  const ctx = measured ? measured.tokens : (prev && prev.ctx) || 0;
  if (ctx) { rec.ctx = ctx; if (measured ? measured.approx : prev && prev.ctxApprox) rec.ctxApprox = true; }
  const transcript = pick(p, 'transcript_path', 'transcriptPath') || (prev && prev.transcript) || '';
  if (transcript) rec.transcript = transcript;
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
  let event = '', p = {}, note = null;
  // 这一轮要停的事件：先定要不要把它叫住处理留言 —— 要的话会话文件里就不能记成「做完了」。只是定下来、不往外写，
  // 写会话文件还是在回话之前。别的事件照旧：会话文件写完了才看留言
  let endPhase = false;
  try {
    const head = buf.charAt(0) === String.fromCharCode(0xFEFF) ? buf.slice(1) : buf;
    const early = (head.trim() ? JSON.parse(head) : {}) || {};
    const ev = process.argv[3] || pick(early, 'hook_event_name', 'hookEventName', 'event');
    endPhase = !!(NOTE_PHASE[kind] && NOTE_PHASE[kind][ev] === 'end');
    // 送不送得到没把握的那几家（END_UNSURE）不改灯：它要是不认这条回应，灯会白白停在「在干活」上二十分钟
    if (endPhase) { note = noteFor(ev, early); keepWorking = !!note && !END_UNSURE.has(kind); }
  } catch { note = null; keepWorking = false; }
  try { main(buf); } catch {}
  try { p = JSON.parse(buf.replace(/^﻿/, '') || '{}') || {}; } catch { p = {}; }
  try { event = process.argv[3] || pick(p, 'hook_event_name', 'hookEventName', 'event'); } catch {}
  // 会话文件写完了才看留言：这一步再出什么岔子也不耽误灯
  if (!endPhase) { try { note = noteFor(event, p); } catch { note = null; } }
  // 长对话提醒：并进留言那条回应里（没有留言就单独回一条）
  try { note = withText(event, note, hygieneFor(event, p)); } catch {}
  if (cursorKind || note) {
    // Windows 上写管道是异步的：等写完再退，不然 {"continue":true} 可能丢，Cursor 会卡住提交
    const out = cursorKind && event === 'beforeSubmitPrompt' ? { continue: true } : (note || {});
    try { process.stdout.write(asciiJson(out), () => process.exit(0)); } catch { process.exit(0); }
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
