// 待办清单：各家 agent 的对话里「还没做完的事」汇到一处，对话可以放心归档，提醒也不丢。
// 用户 2026-11-07：他不常归档对话，是因为对话标题能提醒他还有哪些事没做完 —— 由虚拟窗口统一汇总。
//
// 存在 %LOCALAPPDATA%\AgentHost\todo.json：
//   { v: 1, items: [ { id, kind, session, title, cwd, text, source, state, created, updated, doneAt?, project? } ], seen: { "<kind>-<会话>": 指纹 } }
//   aka     这条还代表哪些对话（["<家>-<会话>", …]）：同一件事跨了几个对话（父对话、子对话、交接出去的）时并成一条，
//           用户 2026-11-09：「待办出现的太多了，而且很多待办实质上就是一个事情」
//   headless DSH 后台自动跑的会话（每晚客服复盘、A股盯盘的 scheduler…）登出来的：DSH 桌面端看不到这个对话
//           （它的 ~/.dsh/storages/workspace.json 里没有）。用户 2026-11-09：「这个对话我都无法在DSH里面看到」「我怎么处理」。
//           这种条目带 cont（「接着做」那句话：说清是后台任务、指去同一项目里能打开的对话）和 goKind（点胶囊切去哪家），宿主照用
//   project 长期项目的名字（用户 2026-11-07：「有一些属于是长期项目的，待办里面有没有标注」）：有这个字段的条目在条带 / 面板里
//           跟一次性待办分开列、带项目名的小标。同一个对话后来登的 / 扫出来的条目跟着它。
//           用户 2026-11-09：「像 deepseek 的那个股票分析对话我聊了好多天了，这种不应该默认算长期项目吗」—— 所以也自动标：
//           scan 让模型给每个对话说一个话题名（topic，已有项目名优先），话题记在 topics 里（哪几天、哪几个对话）；
//           属于已有项目、或同一话题隔天又出现（≥2 天且 ≥3 个对话，或 ≥3 天）、或单个对话本身聊了 ≥3 天，就标成这个项目。
//           A股助理那种每次开新会话的，靠的是「话题隔天又出现」。用户手动取消（project -）的话题记在 noProject，不再自动标
//   source  agent（对话自己登的）/ scan（虚拟窗口看收尾话推断的）/ opencode（OpenCode 自己的 todo 表里没勾的）
//   state   open / maybe（后来这个对话收尾说都好了，可能做完了，等用户勾）/ done
//
// 两路来源：
//   1. agent 自己登：node agenthost-todo.cjs add --kind <家> --session <会话> "剩下什么"（长对话提醒里带着这条命令）
//   2. scan（宿主每 30 分钟跑一次）：钩子每轮做完都把最后一段话记在 sessions\<家>-<会话>.json 的 reply 里。
//      做完后闲了 30 分钟以上、最后一段话变了的对话，交给便宜模型（OpenCode Go 的 deepseek-v4.1-flash，忙就退到
//      DeepSeek 官方 deepseek-flash）问一句「还有没有留下的事」。无头调用、子 agent 的不看。
//      顺带：DSH 没有钩子，从它的会话文件里读收尾话；OpenCode 自己的 todo 表里没勾完的直接搬过来；DSH 的长度由
//      agenthost-hygiene.cjs 的 scan 代算。
//
// 命令：
//   add [--kind K] [--session S] [--title T] [--cwd C] [--project 名] <剩下什么>
//   done <编号…> | open <编号> | del <编号…>
//   project <名|-> <编号…>     把这几条标成长期项目「名」（- 是取消）
//   merge <留下的> <并进去的…>  几条其实是一件事：并成一条（被并的标 done、对话记进留下那条的 aka）
//   project --auto [--dry]     给所有条目（含做完的，当历史）补话题，按上面的规则给开着的补标长期项目
//   dedupe [--dry]             让便宜模型把开着的待办按「是不是同一件事」分组，自动并（scan 新登了条目后也会跑一遍）
//   title <编号> <新标题>       手动改标题
//   title --auto <编号…>        让便宜模型按「剩下的事」重起一个精确短标题（用户 2026-11-07：自动标题「不够精确简单」，长期做项目时影响效率）
//   list [--json] [--all]       按组列（等你的 / agent 在做 / 等别人或外部 / 长期搁置 / 可能做完了，见「四象限之上」），组里按四象限；★ = 上条带的
//   prio <编号…> 重要|不重要|急|不急|等你拍板|等你动手|agent|等别人|等外部|进行中|搁置|等时机|自动    手动定（自动 = 撤掉手动的）
//   prio --auto [--all] [--dry]  让便宜模型判没判过的（--all 全部重判）重不重要、卡在谁、什么状态，带那个对话最后一段话当上下文
//   archive scan [--dry] [--all] | list | do <key…|all> | keep <key…>    归档候选（见下面「归档候选」）：只列不归，用户点了才归
//
// 标题：条目的 title 原本直接拿对话标题（各家自己起的，DSH 是开头一句截断）。scan 问模型时让它连短标题一起出：
//   对话标题已经精确就原样回；不精确（截断的开头、套话、泛泛的「游戏开发建议」）就按剩下的事起一个 4～14 字的（对象 + 事）。
//   copy <编号>            打出「接着做」的那句话（宿主放进剪贴板）
//   scan [--dry] [--max N] [--min-idle 分钟]
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');

const BASE = process.env.AGENTHOST_DATA || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AgentHost');
const FILE = path.join(BASE, 'todo.json');
const LOCK = FILE + '.lock';
const SESS = path.join(BASE, 'sessions');
const HOME = os.homedir();
const NAMES = { claude: 'Claude', codex: 'Codex', cursor: 'Cursor', antigravity: 'Antigravity', opencode: 'OpenCode', dsh: 'DSH', grokbot: 'Grok Bot' };

let hygiene = null;
try { hygiene = require('./agenthost-hygiene.cjs'); } catch {}

function sleep(ms) { try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); } catch {} }

// 读改写一次：拿一个目录锁（别的进程同时在登 / 宿主在勾），最多等 3 秒；拿不到也照做（锁是防丢一条，不是防坏文件）
function withData(fn) {
  let locked = false;
  for (let i = 0; i < 60; i++) {
    try { fs.mkdirSync(LOCK); locked = true; break; } catch (e) {
      // 锁放了 30 秒以上：上一个进程死在半路，接过来
      try { if (Date.now() - fs.statSync(LOCK).mtimeMs > 30000) { fs.rmdirSync(LOCK); continue; } } catch {}
      sleep(50);
    }
  }
  try {
    const d = load();
    const r = fn(d);
    save(d);
    return r;
  } finally { if (locked) { try { fs.rmdirSync(LOCK); } catch {} } }
}

function load() {
  try {
    const d = JSON.parse(fs.readFileSync(FILE, 'utf8'));
    if (d && Array.isArray(d.items)) { d.seen = d.seen || {}; return d; }
  } catch {}
  return { v: 1, items: [], seen: {} };
}

function save(d) {
  // 做完满 30 天的删掉；seen 只留两周内的
  const month = Date.now() - 30 * 86400e3;
  d.items = d.items.filter(x => x.state !== 'done' || (x.doneAt || x.updated) > month);
  // 档位跟着字走：字变了重算截止，重要的最终结果每次都按「用户 > 兜底表 > 模型」再合一遍（兜底表改了，老条目也跟上）
  for (const x of d.items) if (x.state !== 'done') refreshPrio(x);
  fs.mkdirSync(BASE, { recursive: true });
  const tmp = FILE + '.' + process.pid + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(d, null, 1));
  for (let i = 0; i < 8; i++) {
    try { fs.renameSync(tmp, FILE); return; } catch { sleep(20 * (i + 1)); }
  }
  try { fs.unlinkSync(tmp); } catch {}
}

function newId(d) {
  for (;;) {
    const id = crypto.randomBytes(3).toString('hex');
    if (!d.items.some(x => x.id === id)) return id;
  }
}

function readJson(f) { try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch { return null; } }

function tailText(file, bytes) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, bytes);
    const buf = Buffer.allocUnsafe(len);
    fs.readSync(fd, buf, 0, len, size - len);
    return buf.toString('utf8');
  } catch { return ''; } finally { if (fd !== undefined) { try { fs.closeSync(fd); } catch {} } }
}

function headLine(file) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const parts = [];
    let pos = 0;
    while (pos < (2 << 20)) {
      const b = Buffer.allocUnsafe(64 << 10);
      const n = fs.readSync(fd, b, 0, b.length, pos);
      if (n <= 0) break;
      const got = b.subarray(0, n);
      const nl = got.indexOf(10);
      if (nl >= 0) { parts.push(got.subarray(0, nl)); break; }
      parts.push(got); pos += n;
    }
    return Buffer.concat(parts).toString('utf8');
  } catch { return ''; } finally { if (fd !== undefined) { try { fs.closeSync(fd); } catch {} } }
}

function sqlite() {
  const orig = process.emitWarning;
  process.emitWarning = function () {};
  try { return require('node:sqlite'); } catch { return null; } finally { process.emitWarning = orig; }
}

function sqlGet(file, sql, args) {
  const m = sqlite();
  if (!m || !fs.existsSync(file)) return null;
  let db;
  try { db = new m.DatabaseSync(file, { readOnly: true }); return db.prepare(sql).get(...(args || [])) || null; }
  catch { return null; } finally { try { if (db) db.close(); } catch {} }
}

function sqlAll(file, sql, args) {
  const m = sqlite();
  if (!m || !fs.existsSync(file)) return [];
  let db;
  try { db = new m.DatabaseSync(file, { readOnly: true }); return db.prepare(sql).all(...(args || [])); }
  catch { return []; } finally { try { if (db) db.close(); } catch {} }
}

const OPENCODE_DB = path.join(HOME, '.local', 'share', 'opencode', 'opencode.db');

// Claude 桌面端的对话标题在它自己的会话元数据里（%LOCALAPPDATA%\Claude-Data\<账号>\claude-code-sessions\…\local_*.json：
// cliSessionId → title），用户在侧栏看到的就是这个。一个进程里只扫一遍
let claudeTitles = null;
function claudeDesktopTitle(session) {
  if (!claudeTitles) {
    claudeTitles = new Map();
    const root = path.join(process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local'), 'Claude-Data');
    const walk = (dir, depth) => {
      let names = [];
      try { names = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
      for (const e of names) {
        const p = path.join(dir, e.name);
        if (e.isDirectory() && depth < 4) walk(p, depth + 1);
        else if (e.isFile() && /^local_.*\.json$/.test(e.name) && dir.indexOf('claude-code-sessions') >= 0) {
          const j = readJson(p);
          if (j && j.cliSessionId && j.title) claudeTitles.set(j.cliSessionId, j.title);
        }
      }
    };
    walk(root, 0);
  }
  return claudeTitles.get(session) || '';
}

// 对话标题：各家自己起的那个（用户就是靠它记事的）；拿不到退回文件夹名
function titleOf(kind, session, rec) {
  let t = '';
  try {
    if (kind === 'claude') t = claudeDesktopTitle(session);
    if (kind === 'claude' && !t && rec && rec.transcript) {
      const lines = tailText(rec.transcript, 4 << 20).split('\n');
      for (let i = lines.length - 1; i >= 0 && !t; i--) {
        if (lines[i].indexOf('"custom-title"') < 0 && lines[i].indexOf('"ai-title"') < 0) continue;
        try { const j = JSON.parse(lines[i]); t = j.customTitle || j.aiTitle || j.title || ''; } catch {}
      }
    } else if (kind === 'codex') {
      const lines = tailText(path.join(HOME, '.codex', 'session_index.jsonl'), 2 << 20).split('\n');
      for (let i = lines.length - 1; i >= 0 && !t; i--) {
        if (lines[i].indexOf(session) < 0) continue;
        try { const j = JSON.parse(lines[i]); if (j.id === session) t = j.thread_name || ''; } catch {}
      }
    } else if (kind === 'antigravity') {
      const r = sqlGet(path.join(HOME, '.gemini', 'antigravity', 'conversation_summaries.db'),
        'select title from conversation_summaries where conversation_id = ?', [session]);
      t = (r && r.title) || '';
    } else if (kind === 'opencode') {
      const r = sqlGet(OPENCODE_DB, 'select title from session where id = ?', [session]);
      t = (r && r.title) || '';
    } else if (kind === 'dsh' && hygiene) {
      const h = hygiene.load('dsh', session);
      t = (h && h.title) || '';
    } else if (kind === 'cursor' && rec && rec.transcript) {
      const m = /<user_query>\s*([\s\S]{1,200}?)\s*<\/user_query>/.exec(headLine(rec.transcript));
      t = m ? m[1] : '';
    }
  } catch {}
  t = String(t || '').replace(/\s+/g, ' ').trim();
  if (!t && rec && rec.cwd) t = path.basename(rec.cwd);
  return t.slice(0, 60);
}

// 这条待办是不是代表这个对话（本身的 + 并进来的）
function owns(x, kind, session) {
  return (x.kind === kind && x.session === session) || (Array.isArray(x.aka) && x.aka.includes(kind + '-' + session));
}
function addAka(x, key) {
  if (key === x.kind + '-' + x.session) return;
  x.aka = Array.isArray(x.aka) ? x.aka : [];
  if (!x.aka.includes(key)) x.aka.push(key);
}

// 把 fromIds 几条并进 keep：对话记进 aka，被并的标 done（mergedInto 指向留下的，open 能恢复）
function mergeInto(d, keep, fromIds) {
  let n = 0;
  for (const x of d.items) {
    if (x === keep || !fromIds.includes(x.id)) continue;
    addAka(keep, x.kind + '-' + x.session);
    for (const a of x.aka || []) addAka(keep, a);
    if (!keep.project && x.project) keep.project = x.project;
    x.state = 'done'; x.doneAt = x.updated = Date.now(); x.mergedInto = keep.id;
    n++;
  }
  if (n) keep.updated = Date.now();
  return n;
}
function merge(keepId, fromIds) {
  return withData(d => {
    const keep = d.items.find(x => x.id === keepId);
    if (!keep) throw new Error('没有这条：' + keepId);
    keep.state = 'open';
    return mergeInto(d, keep, fromIds);
  });
}

// ---- 四象限（用户 2026-11-09：「要按照我之前说过的又着急又重要，着急，重要，不着急不重要来划分」
//      「目前光凭借脚本来判断不行」「要用更合适的判断方法」）----
// 两条轴用完全不同的办法定（用户 09-09 定的做法）。
//   重要 = 漏了 / 拖了会有实际后果。只有用户知道或要从上下文推 → 模型判（scan 时带收尾话，prio --auto 时带对话最后一段话），
//          再过一张「只抬不降」的兜底表 MUST_MATTER（漏判代价大的几类，模型说不重要也抬上来）。用户手动定的（impUser）压过两者。
//   紧急 = 这件事有没有硬时间点 → 算出来，不问模型（有确定答案，问模型只会引入噪声）：字里的钟点 / 日子 / 截止
//          （「11-08 17:30 后」「明天上午 10 点」「12-06 到期」），按写下这段字的那天解（「明天」是写字那天的明天）。
//          时间点在 URGENT_DAYS 天以内、或已经过了但没过 STALE_DAYS 天，算急：12-06 到期的事 12-03 起才算急；
//          过了一周还挂着的，那个时刻已经错过了，不再占「急」的位置（用户勾掉或改字）。用户手动定的（urg）压过算的。
// 字段：impModel / impWhy（模型说的）、impUser、important（合好的最终结果）、impBy（user / table / model）、
//       due（毫秒，0 = 没有时间点）、dueFor（算 due 时那段字的指纹）、textAt（那段字是什么时候写的）、urg
const URGENT_DAYS = 3, STALE_DAYS = 7;
const MUST_MATTER = new RegExp([
  '充值|付款|扣费|退款|罚款|资损|亏本|亏钱|欠费|续费|作废|保证金',      // 钱
  '买家|客户|订单|发货|差评|投诉|驳回|违规|处罚|下架',                   // 对外：店铺、客户
  '掉线|没在运行|登录过期|服务挂|线上.{0,4}(坏|挂|报错)',                 // 在跑的东西坏了
  '泄露|密码|口令|密钥',                                                 // 安全
  '证件|合同|发票|报税|考试|面试|签证',                                   // 证件、考试（跟用户本人有关的）
].join('|'));
const QUAD_NAME = { 1: '急且重要', 2: '急', 3: '重要', 4: '不急不重要' };

// 用户本人的称呼：模型有时把「等某某」判成等别人，其实等的是用户自己（拍板 / 动手）。
// 本机的写在 private 段里，导出公开版时去掉，公开版只认「用户」「你」。
const OWNER_NAMES = [];
const OWNER_HINT = OWNER_NAMES.length ? OWNER_NAMES.map(n => '「' + n + '」').join('') + '就是用户本人，等他算 decide 或 act；' : '';
const OWNER_RE = new RegExp(['用户'].concat(OWNER_NAMES).join('|') + '|^你$');

const CN_NUM = { 一: 1, 二: 2, 两: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10, 十一: 11, 十二: 12 };
function bjParts(ms) { const t = new Date(ms + 8 * 3600e3); return { y: t.getUTCFullYear(), m: t.getUTCMonth() + 1, d: t.getUTCDate(), wd: t.getUTCDay() }; }
function bjMs(y, m, d, hh, mm) { return Date.UTC(y, m - 1, d, hh || 0, mm || 0) - 8 * 3600e3; }
// 日子后面紧跟的钟点：「17:30」「上午10点」「晚上八点半」
const CLOCK = /^\s*(?:日|号)?\s*(早上|上午|中午|下午|傍晚|晚上)?\s*(\d{1,2}|十[一二]?|[一二两三四五六七八九十])(?:[:：](\d{2})|点(半|\d{1,2}分?)?)/;
function clockAt(s, i) {
  const m = CLOCK.exec(s.slice(i, i + 14));
  if (!m) return null;
  let h = /^\d/.test(m[2]) ? Number(m[2]) : CN_NUM[m[2]];
  // 光写数字没有冒号也没有「点」不算钟点；中文数字要带早晚（「一点」多半是「一点点」）
  if (!m[3] && !m[0].includes('点')) return null;
  if (!/^\d/.test(m[2]) && !m[1]) return null;
  if ((m[1] === '下午' || m[1] === '傍晚' || m[1] === '晚上') && h < 12) h += 12;
  const mm = m[3] ? Number(m[3]) : m[4] === '半' ? 30 : m[4] ? parseInt(m[4], 10) : 0;
  if (h > 23 || mm > 59) return null;
  return { h, mm, len: m[0].length };
}

// 字里最早的那个（不早于写字当天的）时间点；没有回 0。只给日子的算那天 23:59
function dueOf(text, base) {
  const s = String(text || '');
  const b = bjParts(base);
  const day0 = bjMs(b.y, b.m, b.d);
  const got = [];
  const at = (y, m, d, i) => {
    if (m < 1 || m > 12 || d < 1 || d > 31) return;
    let ms = bjMs(y, m, d);
    if (ms < day0 - 180 * 86400e3) ms = bjMs(y + 1, m, d);   // 年底写「1-05」是明年
    if (ms < day0) return;                                     // 写字那天之前的日子是在说过去的事（「换回 11-07 旧图」），不是时间点
    const c = i != null ? clockAt(s, i) : null;
    got.push(c ? ms + (c.h * 60 + c.mm) * 60e3 : ms + (23 * 60 + 59) * 60e3);
  };
  const used = [];
  let m;
  const iso = /(20\d\d)[-/.年](\d{1,2})[-/.月](\d{1,2})日?/g;
  while ((m = iso.exec(s))) { at(Number(m[1]), Number(m[2]), Number(m[3]), m.index + m[0].length); used.push([m.index, m.index + m[0].length]); }
  // 只认「-」和「月」：「减1/3」是分数不是日子
  const md = /(?<![\d.\-/年x×*])(1[0-2]|0?[1-9])(?:-|月)(3[01]|[12]\d|0?[1-9])(?:日|号)?(?![\d.x×*\/])/g;
  while ((m = md.exec(s))) {
    if (used.some(([a, z]) => m.index >= a && m.index < z)) continue;
    at(b.y, Number(m[1]), Number(m[2]), m.index + m[0].length);
    used.push([m.index, m.index + m[0].length]);
  }
  const rel = /(大后天|后天|明天|明早|明晚|明日|今天|今晚|今早|今日|今夜|下周[一二三四五六日天]?|这周[一二三四五六日天]|周末|月底)/g;
  while ((m = rel.exec(s))) {
    const w = m[1];
    // 星期按一到日算（日 = 7）
    const cw = b.wd || 7;
    let add = 0;
    if (w === '后天') add = 2; else if (w === '大后天') add = 3;
    else if (/^明/.test(w)) add = 1;
    else if (/^今/.test(w)) add = 0;
    else if (w === '周末') add = Math.max(0, 6 - cw);
    else if (w === '月底') add = new Date(Date.UTC(b.y, b.m, 0)).getUTCDate() - b.d;
    else {
      const tw = { 一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 日: 7, 天: 7 }[w.slice(-1)];
      if (tw == null) add = 7 - cw + 7;                       // 「下周」没说周几：按下周日收
      else add = tw - cw + (w.startsWith('下周') ? 7 : 0);
      if (add < 0) continue;
    }
    const t = bjParts(day0 + add * 86400e3 + 12 * 3600e3);
    at(t.y, t.m, t.d, m.index + m[0].length);
    used.push([m.index, m.index + m[0].length]);
  }
  // 「2 小时后」「3 天内」
  const after = /(\d+)\s*(分钟|小时|天)(?:后|之后|以内|内)/g;
  while ((m = after.exec(s))) got.push(base + Number(m[1]) * (m[2] === '天' ? 86400e3 : m[2] === '小时' ? 3600e3 : 60e3));
  // 没跟着日子的钟点：算写字当天
  const lone = /(早上|上午|中午|下午|傍晚|晚上)?\s*(\d{1,2}|(?<=[午上])(?:十[一二]?|[一二两三四五六七八九十]))(?:[:：]\d{2}|点)/g;
  while ((m = lone.exec(s))) {
    if (used.some(([a, z]) => m.index >= a && m.index <= z + 2)) continue;
    if (/[\d.]$/.test(s.slice(0, m.index))) continue;
    const c = clockAt(s, m.index);
    if (c) got.push(day0 + (c.h * 60 + c.mm) * 60e3);
  }
  return got.length ? Math.min(...got) : 0;
}

function floorImportant(x) { return MUST_MATTER.test((x.title || '') + ' ' + (x.text || '')); }

function refreshPrio(x) {
  const fp = hash(x.text || '');
  if (x.dueFor !== fp) { x.due = dueOf(x.text, x.textAt || x.created || Date.now()); x.dueFor = fp; }
  if (typeof x.impUser === 'boolean') { x.important = x.impUser; x.impBy = 'user'; }
  else if (floorImportant(x) && !x.impModel) { x.important = true; x.impBy = 'table'; }
  else if (typeof x.impModel === 'boolean') { x.important = x.impModel; x.impBy = 'model'; }
  else { x.important = floorImportant(x); x.impBy = x.important ? 'table' : ''; }
  // 卡在谁、状态（见下面「四象限之上」）：用户手动 > 模型；状态再过一道只抬不降的关键词（搁置 / 暂停 / 等重启…）
  x.wait = WAITS.includes(x.waitUser) ? x.waitUser : WAITS.includes(x.waitModel) ? x.waitModel : '';
  if (PHASES.includes(x.phaseUser)) x.phase = x.phaseUser;
  else if (SHELVE_WORDS.test((x.title || '') + ' ' + (x.text || '')) && !isUrgent(x)) x.phase = 'shelved';
  else x.phase = PHASES.includes(x.phaseModel) ? x.phaseModel : 'active';
}

// ---- 四象限之上（用户 2026-11-09：「我提出的这四个象限也只是最基本的」「可以拓展为多个象限也行，根据需要来进行」；
//      看了草案 side-session-notes/…/分档体系设计-草案-1109.md 说「照这个做」）----
//   卡在谁 wait：decide 等你拍板 / act 等你动手 / agent agent 能接着做 / other 等别人（师傅、买家、别家 agent；用户本人的称呼不算别人，见 OWNER_NAMES）/ external 等外部（到货、审核、数据）。
//                模型判（带上下文），who 是它说的那个人或那件事（「师傅」「PETG 到货」）。没判过的当「等你」（宁可多给他看一眼）
//   状态 phase：active 进行中 / shelved 搁置（用户说过搁置、暂停）/ timing 等时机（要等某个日子或某件事才能动）。
//                模型判 + 关键词只抬不降（字里写了搁置就算搁置，除非这件事眼下急）。用户 11-09：「AICG和oppo目前确实是不重要，
//                然后也不急，但是我以后肯定会重新拾起来的」—— 搁置的永远不勾、不归，只进面板最底下「长期搁置」，每周提一次
//   放了多久：updated 到现在多少天（算的）。重要的 7 天没动提一句；不急不重要的 14 天没动问要不要勾掉（数字是草案定的）
//   面板分组 groupOf：1 等你的 → 2 agent 在做 → 3 等别人或外部（含等时机）→ 4 长期搁置 → 5 可能做完了；组里按四象限、同一个项目挨着（面板折叠）
//   条带 forStrip：只放「等你的」和急的（搁置的、可能做完了的不放），最多 5 个
const WAITS = ['decide', 'act', 'agent', 'other', 'external'];
const PHASES = ['active', 'shelved', 'timing'];
const WAIT_NAME = { decide: '等你拍板', act: '等你动手', agent: 'agent 能接着做', other: '等别人', external: '等外部' };
const PHASE_NAME = { active: '进行中', shelved: '搁置', timing: '等时机' };
const GROUP_NAME = { 1: '等你的', 2: 'agent 在做', 3: '等别人或外部', 4: '长期搁置', 5: '可能做完了' };
// 只认说这件事本身的说法（「项目搁置中」「已暂停」「没在推进」）；「搁置的长期项目（OPPO…）不能归」这种提到别的项目的不算
const SHELVE_WORDS = /搁置中|已搁置|暂时搁置|先搁置|暂停中|已暂停|先放着|先放一放|没在推进|暂不推进|等(?:用户|你)?(?:想)?重启/;
const STRIP_MAX = 5, IMP_STALE_DAYS = 7, IDLE_ASK_DAYS = 14;

function groupOf(x, now) {
  if (x.state === 'maybe') return 5;
  if (x.phase === 'shelved') return 4;
  if (x.wait === 'agent') return 2;
  if (x.wait === 'other' || x.wait === 'external' || x.phase === 'timing') return isUrgent(x, now) && x.phase !== 'timing' ? 1 : 3;
  return 1;
}
function idleDays(x, now) { return Math.max(0, Math.floor(((now || Date.now()) - (x.updated || x.created || 0)) / 86400e3)); }
// 放了多久的提醒：重要的 7 天没动「7 天没动了」；不急不重要的 14 天没动「14 天没动，要不要勾掉」；搁置的不提（每周另提一次）
function staleNote(x, now) {
  if (x.state !== 'open' || x.phase === 'shelved') return '';
  const n = idleDays(x, now);
  if (x.important && n >= IMP_STALE_DAYS) return n + ' 天没动了';
  if (quadOf(x, now) === 4 && n >= IDLE_ASK_DAYS) return n + ' 天没动，要不要勾掉';
  return '';
}
function forStrip(items, now) {
  return items.filter(x => x.state === 'open' && x.phase !== 'shelved' && (groupOf(x, now) === 1 || isUrgent(x, now)))
    .sort((a, b) => quadOf(a, now) - quadOf(b, now) || (a.due || 9e15) - (b.due || 9e15) || b.updated - a.updated)
    .slice(0, STRIP_MAX);
}
// 面板的顺序：组 → 项目挨着（项目按它里面最靠前那件排）→ 四象限 → 时间点 → 新的在前
function panelOrder(items, now) {
  const base = items.slice().sort((a, b) => groupOf(a, now) - groupOf(b, now) || quadOf(a, now) - quadOf(b, now)
    || (a.due || 9e15) - (b.due || 9e15) || b.updated - a.updated);
  const out = [], taken = new Set();
  for (const x of base) {
    if (taken.has(x)) continue;
    const g = groupOf(x, now);
    const same = x.project ? base.filter(y => !taken.has(y) && y.project === x.project && groupOf(y, now) === g) : [x];
    for (const y of same) { taken.add(y); out.push(y); }
  }
  return out;
}

function isUrgent(x, now) {
  if (typeof x.urg === 'boolean') return x.urg;
  if (!x.due) return false;
  const left = x.due - (now || Date.now());
  return left <= URGENT_DAYS * 86400e3 && left >= -STALE_DAYS * 86400e3;
}
function quadOf(x, now) {
  const u = isUrgent(x, now), i = !!x.important;
  return i && u ? 1 : u ? 2 : i ? 3 : 4;
}
// 「11-08 17:30」「12-06」「已过 2 天」：给人看的时间点
function dueLabel(x, now) {
  if (!x.due) return '';
  const p = bjParts(x.due), t = new Date(x.due + 8 * 3600e3);
  const hm = t.getUTCHours() === 23 && t.getUTCMinutes() === 59 ? '' : ' ' + String(t.getUTCHours()).padStart(2, '0') + ':' + String(t.getUTCMinutes()).padStart(2, '0');
  const s = p.m + '-' + String(p.d).padStart(2, '0') + hm;
  const over = (now || Date.now()) - x.due;
  return over > 0 ? s + '（已过' + (over < 86400e3 ? Math.max(1, Math.round(over / 3600e3)) + ' 小时' : Math.round(over / 86400e3) + ' 天') + '）' : s;
}

const IMP_PROMPT = '你在帮用户给待办分轻重。下面每条是「编号　标题 — 剩下的事」，有的带着那个对话最后一段话当上下文。'
  + '给每条判它重不重要：重要 = 漏了或一直拖着会有实际后果——钱（花钱、亏钱、优惠/额度/红包过期作废）、店铺和买家（转化、差评、违规、对外答应过的事）、'
  + '在跑的东西坏了没人管、用户明说过要紧的、长期目标卡在关键的下一步。'
  + '不重要 = 可选的优化、学个知识点、看一眼结果、顺手的小事、搁置中暂时没有下一步的项目。拿不准时偏向重要（漏判比多提醒一次代价大）。'
  + '别管急不急（有没有时间点由程序另算）。'
  + '再判两样：wait = 这件事现在卡在谁身上：decide 等用户拍板 / 批准 / 选方案；act 等用户自己动手（试玩、验收、去办事、看一眼）；'
  + 'agent 不用等谁，AI 助手自己能接着做；other 等别的人（师傅、买家、家里人、别家 agent 发布）——' + OWNER_HINT + 'external 等外部的东西（到货、平台审核、数据出来、某个日子）。'
  + 'who = 等的那个人或那件事，2～8 个字（「师傅」「PETG 到货」「双11 报名结果」），decide/act/agent 回 ""。'
  + 'phase = active 正在推进 / shelved 用户说过搁置、暂停、以后再拾起 / timing 要等某个时机才能动（「看两天转化再定」）。'
  + '只回一行 JSON：{"imp":{"编号":[true,"十个字以内的理由","wait","who","phase"],…}}。';

// 给开着的待办判重要（没判过的；--all 全部重判，用户手动定过的不动）；回 [{id, imp, why}]
async function autoPrio(opts) {
  const d0 = load();
  // 重要、卡在谁、状态一起判：三样里有没判过的就判（用户手动定过的那样照留，合的时候用户的压过模型的）
  const items = d0.items.filter(x => x.state !== 'done' && (opts.all || typeof x.impModel !== 'boolean' || !WAITS.includes(x.waitModel) || !PHASES.includes(x.phaseModel)));
  const got = {};
  for (let i = 0; i < items.length; i += 6) {
    const part = items.slice(i, i + 6);
    const brief = part.map(x => {
      const rec = x.session ? sessionRec(x.kind, x.session) : null;
      const ctx = rec && rec.reply ? '\n　　（那个对话最后说的：' + String(rec.reply).replace(/\s+/g, ' ').slice(0, 400) + '）' : '';
      return x.id + '　' + (x.project ? '〔' + x.project + '〕' : '') + (x.title || '') + ' — ' + String(x.text || '').slice(0, 300) + ctx;
    }).join('\n');
    let r = null;
    for (let k = 0; k < 2 && !r; k++) { try { r = await ask(brief, IMP_PROMPT); } catch {} }
    if (!r || !r.imp) continue;
    for (const [id, v] of Object.entries(r.imp)) {
      const a = Array.isArray(v) ? v : [v];
      if (typeof a[0] === 'boolean') got[id] = { imp: a[0], why: typeof a[1] === 'string' ? a[1].slice(0, 30) : '',
        wait: WAITS.includes(a[2]) ? a[2] : '', who: typeof a[3] === 'string' ? a[3].trim().slice(0, 12) : '', phase: PHASES.includes(a[4]) ? a[4] : '' };
    }
  }
  const out = [];
  const apply = d => {
    for (const x of d.items) {
      const g = got[x.id]; if (!g) continue;
      x.impModel = g.imp; x.impWhy = g.why;
      // 等的是用户本人（OWNER_NAMES）：模型偶尔还是写成等别人，这里拉回来
      if (g.wait === 'other' && OWNER_RE.test(g.who)) { g.wait = 'decide'; g.who = ''; }
      if (g.wait) { x.waitModel = g.wait; x.who = g.wait === 'other' || g.wait === 'external' ? g.who : ''; }
      if (g.phase) x.phaseModel = g.phase;
      refreshPrio(x);
      out.push({ id: x.id, imp: g.imp, why: g.why, wait: x.wait, who: x.who || '', phase: x.phase });
    }
  };
  if (opts.dry) apply(JSON.parse(JSON.stringify(d0))); else withData(apply);
  return out;
}

const WAIT_WORD = { 等你拍板: 'decide', 拍板: 'decide', 等你动手: 'act', 动手: 'act', agent: 'agent', 等别人: 'other', 等外部: 'external' };
const PHASE_WORD = { 进行中: 'active', 搁置: 'shelved', 等时机: 'timing' };
// 用户手动定：重要 / 不重要 / 急 / 不急 / 卡在谁 / 状态 / 自动（撤掉手动的，回到模型 + 兜底表 + 算的）
function setPrio(ids, what) {
  return withData(d => {
    let n = 0;
    for (const x of d.items) {
      if (!ids.includes(x.id)) continue;
      if (what === '重要') x.impUser = true;
      else if (what === '不重要') x.impUser = false;
      else if (what === '急') x.urg = true;
      else if (what === '不急') x.urg = false;
      else if (what === '自动') { delete x.impUser; delete x.urg; delete x.waitUser; delete x.phaseUser; }
      else if (WAIT_WORD[what]) x.waitUser = WAIT_WORD[what];
      else if (PHASE_WORD[what]) x.phaseUser = PHASE_WORD[what];
      else throw new Error('用法：prio <编号…> 重要|不重要|急|不急|等你拍板|等你动手|agent|等别人|等外部|进行中|搁置|等时机|自动');
      x.updated = Date.now();
      n++;
    }
    return n;
  });
}

// 话题名收拾一下：太长的不要
function cleanTopic(t) {
  t = String(typeof t === 'string' ? t : '').replace(/[\s　]+/g, '').replace(/^[「『"'“‘《〔]+|[」』"'”’》〕]+$/g, '').trim();
  return t.length >= 2 && t.length <= 10 ? t : '';
}
function dayOf(ms) { return new Date(ms + 8 * 3600e3).toISOString().slice(0, 10); } // 北京时间的日子

// 记一次话题出现：哪天、哪个对话
function noteTopic(d, name, at, key) {
  if (!name) return;
  d.topics = d.topics || {};
  const t = d.topics[name] = d.topics[name] || { days: [], sessions: [] };
  const day = dayOf(at);
  if (!t.days.includes(day)) t.days.push(day);
  if (key && !t.sessions.includes(key)) t.sessions.push(key);
  if (t.sessions.length > 60) t.sessions = t.sessions.slice(-60);
}
function projectNames(d) {
  const s = new Set();
  for (const x of d.items) if (x.project) s.add(x.project);
  return [...s];
}
// 这个话题够不够算长期项目：已经是项目、或隔天又出现、或单个对话本身聊了好几天（spanDays）
function isLongTopic(d, name, spanDays) {
  if (!name || (d.noProject || []).includes(name)) return false;
  if (projectNames(d).includes(name)) return true;
  const t = (d.topics || {})[name];
  const days = t ? t.days.length : 0, n = t ? t.sessions.length : 0;
  return days >= 3 || (days >= 2 && n >= 3) || (spanDays || 0) >= 3;
}
// 话题够格了：开着的、同话题、没标项目的都标上
function promoteTopic(d, name) {
  let n = 0;
  for (const x of d.items) if (x.state !== 'done' && !x.project && x.topic === name) { x.project = name; n++; }
  return n;
}
// 单个对话从开头到现在多少天（Claude 看对话记录文件、DSH 看会话目录的创建时间；别家拿不准就不算）
function sessionSpanDays(r) {
  try {
    let f = '';
    if (r.kind === 'claude' && r.transcript) f = r.transcript;
    else if (r.kind === 'dsh' && r.file) f = path.dirname(r.file);
    if (!f) return 0;
    return (Date.now() - fs.statSync(f).birthtimeMs) / 86400e3;
  } catch { return 0; }
}

// DSH 桌面端列出来的会话（workspace.json 里各工作区的 sessionIds）；读不到返回 null（那就不判后台）
function dshVisible() {
  const j = readJson(path.join(HOME, '.dsh', 'storages', 'workspace.json'));
  if (!j) return null;
  const set = new Set();
  const walk = o => {
    if (!o || typeof o !== 'object') return;
    if (Array.isArray(o.sessionIds)) for (const id of o.sessionIds) set.add(String(id).replace(/^session-/, ''));
    for (const k of Object.keys(o)) if (o[k] && typeof o[k] === 'object') walk(o[k]);
  };
  walk(j);
  return set.size ? set : null;
}

// 后台来的条目：找同一个长期项目 / 话题里能打开的对话当「家」，算好「接着做」那句话和要切去的那家
function fixHeadless(d, visible) {
  if (!visible) return;
  for (const x of d.items) {
    if (x.kind !== 'dsh' || x.state === 'done' || !x.session) continue;
    if (visible.has(x.session)) { delete x.headless; delete x.cont; delete x.goKind; continue; }
    x.headless = true;
    const key = x.project || x.topic;
    let home = null;
    if (key) for (const y of d.items) {
      if (y === x || y.headless || !y.session || (y.project || y.topic) !== key) continue;
      if (y.kind === 'dsh' && !visible.has(y.session)) continue;
      if (!home || y.updated > home.updated) home = y;
    }
    // 待办里没有：从这个话题出现过的对话里挑最近一个能打开的（比如 A股盯盘 → DSH 的「股市分析」本身）
    if (!home && key && d.topics && d.topics[key]) {
      const ss = d.topics[key].sessions || [];
      for (let i = ss.length - 1; i >= 0 && !home; i--) {
        const m = /^([a-z]+)-(.+)$/.exec(ss[i]);
        if (!m || m[2] === x.session) continue;
        if (m[1] === 'dsh') {
          if (!visible.has(m[2])) continue;
          const h = hygiene ? readJson(path.join(hygiene.DIR, 'dsh-' + m[2] + '.json')) : null;
          home = { kind: 'dsh', session: m[2], title: (h && h.title) || '', cwd: (h && h.cwd) || '' };
        } else {
          const rec = sessionRec(m[1], m[2]);
          home = { kind: m[1], session: m[2], title: titleOf(m[1], m[2], rec) || '', cwd: (rec && rec.cwd) || '' };
        }
      }
    }
    const left = String(x.text || '').replace(/[。.！!；;，,\s]+$/, '');
    x.cont = (x.project ? '长期项目「' + x.project + '」· ' : '')
      + '这条是 DSH 后台自动任务（' + (x.title || '') + '）跑出来的，DSH 里没有能打开的对话。'
      + (home ? '接着做：在 ' + (NAMES[home.kind] || home.kind) + ' 的对话「' + (home.title || '') + '」' + (home.cwd ? '（文件夹 ' + home.cwd + '）' : '') + '里处理' : '开个新对话处理')
      + '。剩下的事：' + left + '。';
    if (home) x.goKind = home.kind; else delete x.goKind;
  }
}

function sessionRec(kind, session) { return readJson(path.join(SESS, kind + '-' + session + '.json')); }

// add 没给会话号：取这一家在当前文件夹里最近有动静的会话
function guessSession(kind, cwd) {
  let best = null;
  try {
    for (const f of fs.readdirSync(SESS)) {
      if (!f.startsWith(kind + '-') || !f.endsWith('.json')) continue;
      const r = readJson(path.join(SESS, f));
      if (!r || !r.sessionId) continue;
      if (cwd && r.cwd && path.resolve(r.cwd).toLowerCase() !== path.resolve(cwd).toLowerCase()) continue;
      if (!best || r.at > best.at) best = r;
    }
  } catch {}
  return best ? best.sessionId : '';
}

function guessKind() {
  if (process.env.CLAUDE_CODE_ENTRYPOINT || process.env.CLAUDECODE) return 'claude';
  if (process.env.CODEX_HOME || process.env.CODEX_SANDBOX || process.env.CODEX_THREAD_ID) return 'codex';
  if (process.env.CURSOR_AGENT || process.env.CURSOR_TRACE_ID) return 'cursor';
  if (process.env.OPENCODE || process.env.OPENCODE_BIN_PATH) return 'opencode';
  return '';
}

function add(opts, text) {
  text = String(text || '').trim();
  if (!text) throw new Error('要写剩下什么');
  const kind = (opts.kind || guessKind() || 'unknown').toLowerCase();
  const cwd = opts.cwd || process.cwd();
  const session = opts.session || guessSession(kind, cwd);
  const rec = session ? sessionRec(kind, session) : null;
  const title = opts.title || titleOf(kind, session, rec) || path.basename(cwd);
  return withData(d => {
    // 同一个对话同样的话登过了（还开着）：只刷新时间
    const same = d.items.find(x => x.state !== 'done' && owns(x, kind, session) && x.text === text);
    if (same) { same.updated = Date.now(); same.state = 'open'; if (opts.project) same.project = opts.project; return same; }
    const it = { id: newId(d), kind, session, title, cwd: (rec && rec.cwd) || cwd, text: text.slice(0, 500),
      source: opts.source || 'agent', state: 'open', created: Date.now(), updated: Date.now(), textAt: Date.now() };
    const project = opts.project || inheritProject(d, kind, session);
    if (project) it.project = project;
    d.items.push(it);
    return it;
  });
}

// 同一个对话已经有条目标了长期项目：新登的跟着它（这不是按内容猜，是同一个对话）
function inheritProject(d, kind, session) {
  if (!session) return '';
  let best = null;
  for (const x of d.items) if (x.project && owns(x, kind, session) && (!best || x.updated > best.updated)) best = x;
  return best ? best.project : '';
}

function setProject(ids, name) {
  return withData(d => {
    let n = 0;
    for (const x of d.items) {
      if (!ids.includes(x.id)) continue;
      if (name) x.project = name;
      else {
        // 用户手动取消：这个话题以后不再自动标
        if (x.project) { d.noProject = d.noProject || []; if (!d.noProject.includes(x.project)) d.noProject.push(x.project); }
        delete x.project;
      }
      x.updated = Date.now();
      n++;
    }
    return n;
  });
}

function setState(ids, state) {
  return withData(d => {
    let n = 0;
    for (const x of d.items) {
      if (!ids.includes(x.id)) continue;
      x.state = state; x.updated = Date.now();
      if (state === 'done') x.doneAt = Date.now(); else delete x.doneAt;
      n++;
    }
    return n;
  });
}

function del(ids) {
  return withData(d => { const before = d.items.length; d.items = d.items.filter(x => !ids.includes(x.id)); return before - d.items.length; });
}

function continueText(x) {
  if (x.cont) return x.cont;
  const who = (NAMES[x.kind] || x.kind) + ' 的对话「' + (x.title || '') + '」';
  return (x.project ? '长期项目「' + x.project + '」· ' : '') + '接着做：' + who + (x.cwd ? '（文件夹 ' + x.cwd + '）' : '') + (x.session ? '，会话 ' + x.session : '')
    + '。剩下的事：' + String(x.text || '').replace(/[。.！!；;，,\s]+$/, '') + '。先看那个对话最后的交代（交接单、提交、文件）再动手。';
}

// ---- 兜底扫描 ----

function hash(s) { return crypto.createHash('sha1').update(String(s)).digest('hex').slice(0, 16); }

// 无头调用、子程序、测试目录里的会话不看
function skipRec(r) {
  const cwd = String(r.cwd || '').toLowerCase();
  if (/\\temp\\|\/temp\/|agent-bus|scratchpad/.test(cwd)) return 'temp / agent-bus';
  if (r.kind === 'claude' && /^(sdk|mcp)/i.test(r.entry || '')) return 'headless';
  // OpenCode 命令行的会话多是定时跑的例行任务（opencode run）；用户自己用的是桌面端
  if (r.kind === 'opencode' && r.entry !== 'opencode-desktop') return 'opencode cli';
  if (r.kind === 'codex' && r.transcript) {
    try {
      const j = JSON.parse(headLine(r.transcript));
      const src = j && j.payload && j.payload.source;
      if (src === 'exec' || (src && typeof src === 'object')) return 'headless / spawned';
    } catch {}
  }
  return '';
}

function goKey() {
  const a = readJson(path.join(HOME, '.local', 'share', 'opencode', 'auth.json'));
  return (a && a['opencode-go'] && a['opencode-go'].key) || '';
}

function deepseekKey() {
  const c = readJson(path.join(HOME, '.config', 'opencode', 'opencode.json'));
  return (c && c.provider && c.provider.deepseek && c.provider.deepseek.options && c.provider.deepseek.options.apiKey) || '';
}

const PROMPT = '你在帮用户整理待办清单。下面是某个 AI 助手对话最后一轮的收尾话（还有对话标题和文件夹）。'
  + '判断这个对话里还有没有没做完的事：要以后接着做的、在等别人（别的 agent、发布、到货）的、等用户拍板 / 批准 / 动手 / 验收的，都算。'
  + '只是汇报做完了、顺口问一句要不要做件无关的小事、例行巡检「无异常」，不算。'
  + '只回一行 JSON：{"left":"剩下的事","title":"短标题","same":"编号","project":"话题","important":true}。left 用一两句中文写清楚是什么事、卡在谁身上（别写「见上文」「如上」）；没有就 ""。'
  + 'title 是这件事在待办清单上显示的名字：对话标题已经精确、一眼能认出是哪件事就原样回；'
  + '不精确的（截断的开头一句、「你是…助理」这种提示词、泛泛的「开发建议」「问题」）就按剩下的事起一个 4～14 个字的，写「对象 + 事」（如「工业富联减仓」「A1 挂扣改打 PETG」），不要句号、不要套话。'
  + 'same：如果后面给了「已经开着的待办」，而这个对话剩下的事跟其中某条其实是同一件事（同一个东西的同一个活，换了个对话接着做、交接出去、或子对话在做），'
  + '回那条的编号；只是同一个项目下的另一件事、或拿不准，回 ""。'
  + 'project：这个对话属于哪个长期在做的话题，2～8 个字（如「A股盯盘」「网店运营」「小说连载」）；后面给了「已有的话题」时，属于其中一个就原样用那个名字，'
  + '都不是就自己起一个；一次性的杂事（清理进程、查个报错）回 ""。没剩下的事也要回 project。'
  + 'important：剩下的事重不重要（true/false）——漏了或一直拖着会有实际后果（钱、店铺和买家、对外答应的事、在跑的东西坏了、用户明说要紧的、长期目标卡在关键一步）算重要；'
  + '可选的优化、学个知识点、看一眼结果、顺手的小事、搁置中暂时没下一步的不算；拿不准偏向 true。别管急不急。';

// 只给已有条目重起名用的提示词（没有收尾话，只有现在的标题和剩下的事）
const TITLE_PROMPT = '你在帮用户给待办清单上的一条起名字。给你它现在的标题和「剩下的事」。'
  + '现在的标题已经精确、一眼能认出是哪件事就原样回；不精确的（截断的开头一句、「你是…助理」这种提示词、泛泛的「开发建议」「问题」）'
  + '就按剩下的事起一个 4～14 个字的，写「对象 + 事」（如「工业富联减仓」「A1 挂扣改打 PETG」），不要句号、不要套话。'
  + '只回一行 JSON：{"title":"短标题"}。';

// 模型回的标题收拾一下：去引号 / 句号 / 多余空白，太长的不要（宁可留原标题）
function cleanTitle(t) {
  t = String(t || '').replace(/[\s　]+/g, ' ').replace(/^[「『"'“‘《]+|[」』"'”’》]+$/g, '').replace(/[。.！!；;，,]+$/, '').trim();
  return t.length >= 2 && t.length <= 24 ? t : '';
}

async function chat(url, key, model, headers, user, system) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), 90000);
  try {
    const r = await fetch(url, {
      method: 'POST', signal: ctl.signal,
      headers: Object.assign({ Authorization: 'Bearer ' + key, 'Content-Type': 'application/json' }, headers || {}),
      body: JSON.stringify({ model, max_tokens: 8000, temperature: 0, messages: [{ role: 'system', content: system || PROMPT }, { role: 'user', content: user }] }),
    });
    const j = await r.json();
    const c = j && j.choices && j.choices[0];
    if (!c || c.finish_reason === 'length') throw new Error((j && j.error && (j.error.message || j.error.type)) || 'no answer');
    const m = /\{[\s\S]*\}/.exec((c.message && c.message.content) || '');
    if (!m) throw new Error('not json');
    const o = JSON.parse(m[0]);
    return { left: typeof o.left === 'string' ? o.left.trim() : '', title: cleanTitle(typeof o.title === 'string' ? o.title : ''),
      same: typeof o.same === 'string' ? o.same.trim() : '', groups: Array.isArray(o.groups) ? o.groups : null,
      project: cleanTopic(o.project), topics: o.topics && typeof o.topics === 'object' ? o.topics : null,
      important: typeof o.important === 'boolean' ? o.important : null,
      imp: o.imp && typeof o.imp === 'object' ? o.imp : null, cls: o.cls && typeof o.cls === 'object' ? o.cls : null };
  } finally { clearTimeout(t); }
}

// 问便宜模型（Go 的 deepseek-v4.1-flash，忙就退到 DeepSeek 官方 deepseek-flash）；回 { left, title }
async function ask(user, system) {
  const go = goKey();
  let err;
  for (let i = 0; go && i < 2; i++) {
    try {
      return await chat('https://opencode.ai/zen/go/v1/chat/completions', go, 'deepseek-v4.1-flash',
        { 'x-opencode-session': 'agenthost-todo-' + Date.now() }, user, system);
    } catch (e) { err = e; }
  }
  const ds = deepseekKey();
  if (ds) return chat('https://api.deepseek.com/v1/chat/completions', ds, 'deepseek-flash', null, user, system);
  throw err || new Error('no model key');
}

function openBrief(items) {
  return items.map(x => x.id + '　' + (x.title || '') + ' — ' + String(x.text || '').slice(0, 160)).join('\n');
}
function judge(item, open, topics) {
  return ask('对话标题：' + (item.title || '（无）') + '\n文件夹：' + (item.cwd || '') + '\n收尾话：\n' + item.reply.slice(0, 2000)
    + (open && open.length ? '\n\n已经开着的待办（编号　标题 — 剩下的事）：\n' + openBrief(open) : '')
    + (topics && topics.length ? '\n\n已有的话题：' + topics.join('、') : ''));
}

const TOPIC_PROMPT = '你在帮用户整理待办清单。下面是一批待办（编号　标题 — 剩下的事），有做完的也有没做完的。'
  + '给每条说它属于哪个长期在做的话题，2～8 个字（如「A股盯盘」「网店运营」「小说连载」「虚拟窗口」）；同一个话题的要用完全相同的名字；'
  + '给了「已有的话题」时，属于其中一个就原样用那个名字（同一只股票、同一个店的不同商品都归进那个大话题，别再细分）。一次性的杂事（清理进程、查个报错）回 ""。'
  + '只回一行 JSON：{"topics":{"编号":"话题",…}}。';

// 给已有条目补话题（做完的也补，当历史算「隔天又出现」），再按规则给开着的补标项目；回 { topics: {名: [编号…]}, tagged: [{id, project}] }
async function autoProject(opts) {
  const d0 = load();
  const all = d0.items;
  const known = projectNames(d0).concat(Object.keys(d0.topics || {}));
  const got = {};
  for (let i = 0; i < all.length; i += 15) {
    const part = all.slice(i, i + 15);
    const brief = part.map(x => x.id + '　' + (x.title || '') + ' — ' + String(x.text || '').slice(0, 120) + (x.cwd ? '（文件夹 ' + x.cwd + '）' : '')).join('\n');
    let r;
    try { r = await ask(brief + (known.length ? '\n\n已有的话题：' + [...new Set(known)].join('、') : ''), TOPIC_PROMPT); }
    catch { try { r = await ask(brief + (known.length ? '\n\n已有的话题：' + [...new Set(known)].join('、') : ''), TOPIC_PROMPT); } catch { continue; } }
    for (const [id, name] of Object.entries(r.topics || {})) { got[id] = cleanTopic(name); if (got[id]) known.push(got[id]); }
  }
  const out = { topics: {}, tagged: [] };
  const apply = d => {
    for (const x of d.items) {
      const name = got[x.id];
      if (!name) continue;
      x.topic = name;
      noteTopic(d, name, x.created || x.updated, x.kind + '-' + x.session);
      (out.topics[name] = out.topics[name] || []).push(x.id);
    }
    for (const name of Object.keys(out.topics)) {
      if (!isLongTopic(d, name)) continue;
      for (const x of d.items) if (x.state !== 'done' && !x.project && x.topic === name) { x.project = name; out.tagged.push({ id: x.id, project: name }); }
    }
  };
  if (opts.dry) { const d = JSON.parse(JSON.stringify(d0)); apply(d); } else withData(d => { apply(d); fixHeadless(d, dshVisible()); });
  return out;
}

const DEDUPE_PROMPT = '你在帮用户整理待办清单。下面是开着的待办（编号　标题 — 剩下的事）。有些其实是同一件事：同一个东西的同一个活，'
  + '在不同对话里各登了一条（换了个对话接着做、交接出去、子对话在做）。把这种分成组。只是同一个项目下的不同的事（比如同一个店的改价和报活动）不算，拿不准不算。'
  + '只回一行 JSON：{"groups":[["留下的编号","并进去的编号"]]}，每组留下的放第一个（挑写得最全、最新的）；没有就 {"groups":[]}。';

// 开着的待办让模型分组，同一件事的并成一条；回 [[留下的, 并进去的…], …]
async function dedupe(opts) {
  const open = load().items.filter(x => x.state !== 'done');
  if (open.length < 2) return [];
  const r = await ask(openBrief(open), DEDUPE_PROMPT);
  const ids = new Set(open.map(x => x.id));
  const used = new Set();
  const groups = [];
  for (const g of r.groups || []) {
    if (!Array.isArray(g)) continue;
    const ok = g.map(String).filter(id => ids.has(id) && !used.has(id));
    if (ok.length < 2) continue;
    ok.forEach(id => used.add(id));
    groups.push(ok);
  }
  if (!opts.dry && groups.length) withData(d => {
    for (const g of groups) { const keep = d.items.find(x => x.id === g[0]); if (keep) mergeInto(d, keep, g.slice(1)); }
  });
  return groups;
}

function setTitle(id, title) {
  title = cleanTitle(title);
  if (!title) throw new Error('标题要 2～24 个字');
  return withData(d => {
    const x = d.items.find(i => i.id === id);
    if (!x) throw new Error('没有这条：' + id);
    x.title = title; x.updated = Date.now();
    return x;
  });
}

// 让模型给已有的条目重起名（用户觉得哪条不精确就点哪条）；回 [{ id, from, to }]，没改的 to 为空
async function retitle(ids) {
  const items = load().items.filter(x => ids.includes(x.id));
  const out = [];
  for (const x of items) {
    const r = await ask('现在的标题：' + (x.title || '（无）') + '\n剩下的事：' + (x.text || ''), TITLE_PROMPT);
    const to = r.title && r.title !== x.title ? r.title : '';
    if (to) setTitle(x.id, to);
    out.push({ id: x.id, from: x.title, to });
  }
  return out;
}

// 只看最近两天收过尾的对话。原来是 7 天：换版重开几次、每轮判 15 个，把一周里早就了结的对话全翻出来登了 36 条，
// 用户 2026-11-07：「怎么会有那么多待办事项」「很多都是早就完成了的」。更早的对话要登，由那个对话自己 add
const WINDOW_DAYS = 2;

function candidates(minIdleMin) {
  const now = Date.now();
  const out = [];
  try {
    for (const f of fs.readdirSync(SESS)) {
      if (!f.endsWith('.json')) continue;
      const r = readJson(path.join(SESS, f));
      if (!r || !r.kind || !r.sessionId || r.sessionId === 'nosession') continue;
      if (r.state !== 'done' && r.state !== 'stopped') continue;
      if (now - r.at < minIdleMin * 60e3 || now - r.at > WINDOW_DAYS * 86400e3) continue;
      if (!r.reply || r.reply.trim().length < 15) continue;
      if (skipRec(r)) continue;
      out.push(r);
    }
  } catch {}
  // DSH：没有钩子，收尾话在它自己的会话文件里（hygiene.scan 读过的记在 hygiene\dsh-*.json）
  if (hygiene) {
    try {
      for (const f of fs.readdirSync(hygiene.DIR)) {
        if (!f.startsWith('dsh-') || !f.endsWith('.json')) continue;
        const h = readJson(path.join(hygiene.DIR, f));
        if (!h || !h.reply || !h.mtime) continue;
        if (now - h.mtime < minIdleMin * 60e3 || now - h.mtime > WINDOW_DAYS * 86400e3) continue;
        out.push({ kind: 'dsh', sessionId: h.sessionId, cwd: h.cwd || '', reply: h.reply, at: h.mtime, title: h.title || '', file: h.file || '' });
      }
    } catch {}
  }
  return out.sort((a, b) => b.at - a.at);
}

// OpenCode 自己的 todo 表里没勾完的：一周内动过、没归档、桌面端的会话（命令行那些是定时例行任务，清单常留半截）
function opencodeTodos() {
  const since = Date.now() - 7 * 86400e3;
  const desktop = new Set();
  try {
    for (const f of fs.readdirSync(SESS)) {
      if (!f.startsWith('opencode-')) continue;
      const r = readJson(path.join(SESS, f));
      if (r && r.entry === 'opencode-desktop') desktop.add(r.sessionId);
    }
  } catch {}
  return sqlAll(OPENCODE_DB,
    "select t.session_id as session, t.content as text, s.title as title, s.directory as cwd from todo t join session s on s.id = t.session_id "
    + "where t.status not in ('completed','cancelled') and s.time_updated > ? and s.time_archived is null and s.parent_id is null", [since]).filter(t => desktop.has(t.session));
}

async function scan(opts) {
  const max = opts.max || 15;
  const report = { judged: 0, added: 0, merged: 0, maybe: 0, errors: 0, imported: 0, long: [] };
  if (hygiene) { try { report.long = hygiene.scan(); } catch {} }
  const seen = load().seen;
  const todo = candidates(opts.minIdle != null ? opts.minIdle : 30).filter(r => seen[r.kind + '-' + r.sessionId] !== hash(r.reply)).slice(0, max);
  const results = [];
  const d0 = load();
  const open = d0.items.filter(x => x.state !== 'done');
  const topicsKnown = [...new Set(projectNames(d0).concat(Object.keys(d0.topics || {})))].slice(-80);
  // 三个一起问
  for (let i = 0; i < todo.length; i += 3) {
    await Promise.all(todo.slice(i, i + 3).map(async rr => { const r = rr;
      const item = { kind: r.kind, session: r.sessionId, cwd: r.cwd, reply: r.reply, at: r.at, title: r.title || titleOf(r.kind, r.sessionId, r) };
      try { const r = await judge(item, open.filter(x => !owns(x, item.kind, item.session)), topicsKnown); item.left = r.left; item.same = r.same; item.topic = r.project; item.imp = r.important; item.span = sessionSpanDays(rr); if (r.title) item.title = r.title; results.push(item); report.judged++; }
      catch (e) { report.errors++; if (opts.verbose) process.stderr.write(r.kind + '-' + r.sessionId + ': ' + e.message + '\n'); }
    }));
  }
  const oc = opencodeTodos();
  if (opts.dry) { report.results = results.map(x => ({ kind: x.kind, title: x.title, left: x.left, same: x.same || undefined, topic: x.topic || undefined })); report.opencode = oc.length; return report; }
  withData(d => {
    let sameIt;
    for (const it of results) {
      const key = it.kind + '-' + it.session;
      d.seen[key] = hash(it.reply);
      noteTopic(d, it.topic, it.at, key);
      const mine = d.items.filter(x => owns(x, it.kind, it.session) && x.state !== 'done');
      if (it.left) {
        // 这个对话自己登过（还开着）：它比推断的准，不再加
        if (mine.some(x => x.source === 'agent' && x.state === 'open')) continue;
        const old = mine.find(x => x.source === 'scan');
        if (old) { old.text = it.left.slice(0, 500); old.textAt = Date.now(); old.title = it.title || old.title; old.state = 'open'; old.updated = Date.now(); if (it.topic && !old.topic) old.topic = it.topic; if (typeof it.imp === 'boolean') old.impModel = it.imp; delete old.waitModel; delete old.phaseModel; }
        else if (!mine.length && (sameIt = d.items.find(x => x.id === it.same && x.state !== 'done'))) {
          // 跟别的对话登的那条是同一件事：不另登，记进那条的 aka；那条是推断的就换成这段更新的说法
          addAka(sameIt, key);
          if (sameIt.source === 'scan') { sameIt.text = it.left.slice(0, 500); sameIt.textAt = Date.now(); if (typeof it.imp === 'boolean') sameIt.impModel = it.imp; delete sameIt.waitModel; delete sameIt.phaseModel; }
          sameIt.state = 'open'; sameIt.updated = Date.now();
          report.merged++;
        } else {
          const proj = inheritProject(d, it.kind, it.session) || (isLongTopic(d, it.topic, it.span) ? it.topic : '');
          d.items.push(Object.assign({ id: newId(d), kind: it.kind, session: it.session, title: it.title, cwd: it.cwd, text: it.left.slice(0, 500),
            source: 'scan', state: 'open', created: Date.now(), updated: Date.now(), textAt: Date.now() }, proj ? { project: proj } : {}, it.topic ? { topic: it.topic } : {},
            typeof it.imp === 'boolean' ? { impModel: it.imp } : {}));
          report.added++;
        }
      } else {
        // 后来又收尾、说都好了：之前登的（比这段收尾话早的）标成「可能做完了」，等用户勾
        for (const x of mine) if (x.state === 'open' && x.updated < it.at && x.source !== 'opencode') { x.state = 'maybe'; x.updated = Date.now(); report.maybe++; }
      }
    }
    // 话题这一轮刚够格（隔天又出现）：之前登的同话题条目也补标
    for (const it of results) if (it.topic && isLongTopic(d, it.topic, it.span)) report.promoted = (report.promoted || 0) + promoteTopic(d, it.topic);
    for (const t of oc) {
      if (d.items.some(x => x.kind === 'opencode' && x.session === t.session && x.text === t.text)) continue;
      d.items.push({ id: newId(d), kind: 'opencode', session: t.session, title: t.title || '', cwd: t.cwd || '', text: String(t.text).slice(0, 500),
        source: 'opencode', state: 'open', created: Date.now(), updated: Date.now(), textAt: Date.now() });
      report.imported++;
    }
    // OpenCode 表里已经勾掉的：这边也勾掉
    const live = new Set(oc.map(t => t.session + '\n' + t.text));
    for (const x of d.items) if (x.source === 'opencode' && x.state !== 'done' && !live.has(x.session + '\n' + x.text)) { x.state = 'done'; x.doneAt = x.updated = Date.now(); }
    fixHeadless(d, dshVisible());
    d.lastScan = Date.now();
  });
  // 这轮新登了：整体再看一遍有没有同一件事登了两条（同一轮里两个对话同时登的、之前就有的）
  if (report.added) { try { report.deduped = (await dedupe({})).length; } catch { report.errors++; } }
  // 自己登的（add）、刚并进来的还没判过重不重要：补判（只判没判过的，便宜）
  try { report.prio = (await autoPrio({})).length; } catch { report.errors++; }
  // 归档候选 6 小时更新一次（分类按对话最后动静缓存，没新动静的不再问模型）
  try {
    const ar = load().archive;
    if (!ar || !ar.at || Date.now() - ar.at > 6 * 3600e3) report.archive = (await archiveScan({})).cand.length;
  } catch { report.errors++; }
  return report;
}

// ---- 归档候选（用户 2026-11-09）----
// 11-09 按「闲 2 天 + 没待办」归了 4 个，用户马上纠正：「oppo心力球是因为我个人原因搁置了，你要是归档了可能我就忘记了」
// 「AICG也是属于一个长期的目标」「学习日报我虽然很少看，但是也不代表没有意义」。所以：
//   · 只有「已了结」「一次性」的才当候选；长期项目（进行中或搁置）、例行产出（定时任务跑的日报 / 周报 / 巡查）、还挂着待办的永远不当；
//   · 不自动归：候选攒在 todo.json 的 archive.cand 里，面板上「可归档 N 个」给用户过目，他点了才归；点「别归」的记进 archive.keep，不再提；
//   · 怎么归：Codex 直接 `codex archive <thread-id>`；Claude 只有桌面端里的会话工具（archive_session）能归，
//     宿主把「归档这几个」那句话放进剪贴板交给 Claude 的格子，归完这边看 isArchived 自己把候选去掉；Antigravity 归不了，不列。
// 分类：能用规则定的先定（定时任务 = 例行、挂着待办 = 进行中、标题是已有长期项目 = 项目、闲不到 2 天不看），
//       剩下的问便宜模型（标题 + 最后一段话），结果按「最后动静的时间」缓存在 archive.cls，对话没新动静就不再问。
const ARCHIVE_IDLE_DAYS = 2;
const ROUTINE_KEEP = 3;             // 例行产出留最近几期（草案没定几期，先 3 期）
// 例行对话归成一组用的标题：去掉日子、编号、数字（「每日巡查 11-07」「日报 2026-11-08」→ 同一组）
function routineKey(t) { return String(t || '').replace(/20\d\d[-/.年]\d{1,2}[-/.月]\d{1,2}日?|\d{1,2}[-/月]\d{1,2}[日号]?|\d+/g, '').replace(/[\s　#（）()·\-—_:：]+/g, '').slice(0, 30); }
const ARCHIVE_ASKED_MS = 86400e3;   // 归档那句话交出去一天还没归掉：重新列回候选

function claudeSidebar() {
  const out = [];
  const root = path.join(process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local'), 'Claude-Data');
  const walk = (dir, depth) => {
    let names = [];
    try { names = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of names) {
      const p = path.join(dir, e.name);
      if (e.isDirectory() && depth < 4) walk(p, depth + 1);
      else if (e.isFile() && /^local_.*\.json$/.test(e.name) && dir.indexOf('claude-code-sessions') >= 0) {
        const j = readJson(p);
        if (!j || !j.sessionId) continue;
        out.push({ kind: 'claude', id: j.sessionId, cli: j.cliSessionId || '', title: j.title || '', cwd: j.cwd || '', last: j.lastActivityAt || j.createdAt || 0,
          created: j.createdAt || 0, archived: !!j.isArchived, routine: !!j.scheduledTaskId, task: j.scheduledTaskId || '', pinned: false });
      }
    }
  };
  walk(root, 0);
  return out;
}

const CODEX_STATE = path.join(HOME, '.codex', 'state_5.sqlite');
function codexSidebar() {
  // 侧栏里那些：桌面端开的（source = vscode）；命令行无头的（exec）、子 agent、审查员不算
  return sqlAll(CODEX_STATE, "select id, coalesce(nullif(name,''), title) as title, cwd, updated_at_ms as last, created_at_ms as created, archived, is_pinned as pinned, "
    + "substr(first_user_message, 1, 300) as first from threads where source = 'vscode'")
    .map(r => ({ kind: 'codex', id: r.id, cli: r.id, title: String(r.title || '').replace(/\s+/g, ' ').slice(0, 60), cwd: r.cwd || '', last: r.last || 0,
      created: r.created || 0, archived: !!r.archived, routine: false, pinned: !!r.pinned, first: r.first || '',
      // 定时任务跑出来的对话：第一句是 <scheduled-task name="qinggu-patrol" …>，按任务名归成一组（例行只留最近几期）
      task: ((/<scheduled-task name="([^"]+)"/.exec(String(r.first || '') + ' ' + String(r.title || '')) || [])[1] || '') }))
    .map(s => Object.assign(s, { routine: !!s.task }));   // 有任务名就是例行（跟 Claude 的 scheduledTaskId 一样按规则定，不问模型）
}

const CONV_NAME = { ongoing: '进行中', project: '长期·搁置', routine: '例行', done: '了结', oneoff: '一次性', recent: '最近还在动' };
const ARCHIVE_PROMPT = '你在帮用户整理 AI 助手对话的侧边栏，决定哪些对话可以归档（归档后还能找回，只是不在列表里提醒他）。'
  + '下面每行是「编号　标题 — 最后说的话（或第一句）」，带文件夹和闲了几天。给每个对话分一类：'
  + 'done = 事情做完了、有了结论，没有要接着做的；oneoff = 一次性的问答 / 连通测试 / 委托探针 / 打招呼 / 查个东西，用完就没用了；'
  + 'project = 长期在做的产品、长期目标或爱好项目（哪怕现在搁置、暂停，用户以后还会回来）；routine = 定时例行产出（日报、周报、巡查、复盘）；'
  + 'ongoing = 还有没做完、在等人或等用户拍板的事。拿不准就选 project 或 ongoing（误归会让用户忘掉这件事，不归只是列表长一点）。'
  + '只回一行 JSON：{"cls":{"编号":["类别","十个字以内的理由"],…}}。';

async function archiveScan(opts) {
  const now = Date.now();
  const d0 = load();
  const ar = d0.archive || {};
  const cls = Object.assign({}, ar.cls || {});
  const keep = new Set(ar.keep || []);
  const projects = new Set(projectNames(d0));
  const open = d0.items.filter(x => x.state !== 'done');
  const sessions = claudeSidebar().concat(codexSidebar()).filter(s => !s.archived);
  const rows = [];
  const ask1 = [];
  for (const s of sessions) {
    const key = s.kind + '-' + s.id;
    const idle = (now - s.last) / 86400e3;
    let c = null;
    if (s.routine) c = ['routine', '定时任务'];
    else if (s.pinned) c = ['project', '置顶了'];
    else if (open.some(x => owns(x, s.kind, s.cli))) c = ['ongoing', '还挂着待办'];
    else if ([...projects].some(p => p && s.title.includes(p))) c = ['project', '长期项目'];
    else if (idle < ARCHIVE_IDLE_DAYS) c = ['recent', '最近还在动'];
    else if (cls[key] && cls[key].last === s.last) c = [cls[key].cls, cls[key].why];
    else ask1.push(s);
    rows.push({ s, key, idle, c });
  }
  // 模型分：一批 20 个
  for (let i = 0; i < ask1.length && !opts.noModel; i += 20) {
    const part = ask1.slice(i, i + 20);
    const brief = part.map((s, j) => {
      const rec = sessionRec(s.kind, s.cli);
      const said = (rec && rec.reply) || s.first || '';
      return (j + 1) + '　' + (s.title || '（无标题）') + ' — ' + String(said).replace(/\s+/g, ' ').slice(0, 300)
        + '（' + (NAMES[s.kind] || s.kind) + (s.cwd ? '，文件夹 ' + s.cwd : '') + '，闲了 ' + Math.floor((now - s.last) / 86400e3) + ' 天）';
    }).join('\n');
    let r = null;
    for (let k = 0; k < 2 && !r; k++) { try { r = await ask(brief + (projects.size ? '\n\n用户已有的长期项目：' + [...projects].join('、') : ''), ARCHIVE_PROMPT); } catch {} }
    if (!r || !r.cls) continue;
    part.forEach((s, j) => {
      const v = r.cls[String(j + 1)];
      const a = Array.isArray(v) ? v : [v];
      if (!['done', 'oneoff', 'project', 'routine', 'ongoing'].includes(a[0])) return;
      cls[s.kind + '-' + s.id] = { cls: a[0], why: typeof a[1] === 'string' ? a[1].slice(0, 30) : '', last: s.last };
    });
  }
  for (const r of rows) if (!r.c && cls[r.key]) r.c = [cls[r.key].cls, cls[r.key].why];
  const asked = Object.assign({}, ar.asked || {});
  const cand = [];
  for (const r of rows) {
    if (!r.c || (r.c[0] !== 'done' && r.c[0] !== 'oneoff') || keep.has(r.key)) continue;
    if (asked[r.key] && now - asked[r.key] < ARCHIVE_ASKED_MS) continue;
    cand.push({ key: r.key, kind: r.s.kind, id: r.s.id, title: r.s.title, cwd: r.s.cwd, last: r.s.last, cls: r.c[0], why: r.c[1] || '' });
  }
  // 例行产出只留最近几期（草案：「例行的只留最近几期（如每日巡查）」）：同一个定时任务（没有任务号的按去掉数字 / 日子的标题）
  // 最近 ROUTINE_KEEP 期留着，更早的列进候选，也是用户点了才归
  const byTask = {};
  for (const r of rows) {
    if (!r.c || r.c[0] !== 'routine') continue;
    const k = r.s.task || (r.s.kind + ':' + routineKey(r.s.title) + ':' + (r.s.cwd || ''));
    (byTask[k] = byTask[k] || []).push(r);
  }
  for (const list of Object.values(byTask)) {
    list.sort((a, b) => b.s.last - a.s.last);
    list.slice(ROUTINE_KEEP).forEach((r, i) => {
      if (keep.has(r.key) || (asked[r.key] && now - asked[r.key] < ARCHIVE_ASKED_MS)) return;
      cand.push({ key: r.key, kind: r.s.kind, id: r.s.id, title: r.s.title, cwd: r.s.cwd, last: r.s.last, cls: 'routine',
        why: '例行旧期，留最近 ' + ROUTINE_KEEP + ' 期' });
    });
  }
  cand.sort((a, b) => a.last - b.last);
  const live = new Set(sessions.map(s => s.kind + '-' + s.id));
  for (const k of Object.keys(asked)) if (!live.has(k)) delete asked[k];   // 已经归掉了
  for (const k of Object.keys(cls)) if (!live.has(k)) delete cls[k];
  const inCand = new Set(cand.map(c => c.key));
  const all = rows.map(r => ({ key: r.key, kind: r.s.kind, title: r.s.title, last: r.s.last, cls: r.c ? r.c[0] : '', why: r.c ? r.c[1] : '', keep: keep.has(r.key) || undefined, cand: inCand.has(r.key) || undefined }));
  if (!opts.dry) withData(d => { d.archive = Object.assign(d.archive || {}, { cls, cand, asked, at: now }); });
  return { cand, all };
}

// 用户点了「归档」：Codex 的直接归，Claude 的拼一句交给 Claude 的会话工具去归；回 { done: [key], failed: [key], claude: 那句话 }
function archiveDo(keys) {
  const d = load();
  const cand = ((d.archive || {}).cand || []).filter(c => keys.includes('all') || keys.includes(c.key));
  const out = { done: [], failed: [], claude: '' };
  const cl = [];
  for (const c of cand) {
    if (c.kind === 'codex') {
      let ok = false;
      try {
        const r = require('node:child_process').spawnSync(process.platform === 'win32' ? 'codex.cmd' : 'codex', ['archive', c.id],
          { encoding: 'utf8', timeout: 60000, windowsHide: true, shell: process.platform === 'win32' });
        ok = r.status === 0;
      } catch {}
      (ok ? out.done : out.failed).push(c.key);
    } else if (c.kind === 'claude') cl.push(c);
  }
  if (cl.length) out.claude = '用 archive_session 归档这 ' + cl.length + ' 个对话（用户在虚拟窗口的待办面板里点过「归档」了，照归，不用再问）：'
    + cl.map(c => '「' + (c.title || '') + '」' + c.id).join('、') + '。归完说一声归了几个就行。';
  withData(dd => {
    const ar = dd.archive = dd.archive || {};
    ar.asked = ar.asked || {};
    for (const c of cl) ar.asked[c.key] = Date.now();
    ar.cand = (ar.cand || []).filter(c => !out.done.includes(c.key) && !cl.some(x => x.key === c.key));
  });
  return out;
}

// 用户点了「别归」：记下来，以后不再提
function archiveKeep(keys) {
  return withData(d => {
    const ar = d.archive = d.archive || {};
    ar.keep = [...new Set((ar.keep || []).concat(keys))];
    ar.cand = (ar.cand || []).filter(c => !keys.includes(c.key));
    return keys.length;
  });
}

function list(all) {
  const d = load();
  const day = Date.now() - 86400e3;
  const now = Date.now();
  const xs = d.items.filter(x => all || x.state !== 'done' || (x.doneAt || 0) > day)
    .map(x => { if (x.state !== 'done') refreshPrio(x); return x; })
    .map(x => Object.assign(x, { quad: quadOf(x, now), group: groupOf(x, now), idle: idleDays(x, now), stale: staleNote(x, now) || undefined }));
  const strip = new Set(forStrip(xs, now));
  for (const x of xs) if (strip.has(x)) x.strip = true;
  return panelOrder(xs.filter(x => x.state !== 'done'), now).concat(xs.filter(x => x.state === 'done'));
}

module.exports = { add, merge, dedupe, autoProject, setState, setProject, setTitle, retitle, cleanTitle, del, list, scan, continueText, titleOf, FILE,
  dueOf, quadOf, dueLabel, autoPrio, setPrio, archiveScan, archiveDo, archiveKeep, QUAD_NAME,
  groupOf, idleDays, staleNote, forStrip, panelOrder, refreshPrio, routineKey, GROUP_NAME };

function parse(argv) {
  const o = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const k = a.slice(2);
      if (['json', 'all', 'dry', 'verbose', 'auto', 'no-model'].includes(k)) o[k] = true;
      else o[k] = argv[++i];
    } else o._.push(a);
  }
  return o;
}

if (require.main === module) {
  const [cmd, ...rest] = process.argv.slice(2);
  const o = parse(rest);
  const out = s => process.stdout.write(s + '\n');
  (async () => {
    try {
      if (cmd === 'add') {
        const it = add({ kind: o.kind, session: o.session, title: o.title, cwd: o.cwd, project: o.project }, o._.join(' ') || (o.file ? fs.readFileSync(o.file, 'utf8') : ''));
        out('已登进待办 ' + it.id + (it.project ? '〔' + it.project + '〕' : '') + '：' + it.text);
      } else if (cmd === 'project' && o.auto) {
        const r = await autoProject({ dry: o.dry });
        for (const [name, ids] of Object.entries(r.topics)) out(name + '：' + ids.length + ' 条');
        if (!r.tagged.length) out('没有要补标的');
        for (const t of r.tagged) out((o.dry ? '会标：' : '已标：') + t.id + ' →〔' + t.project + '〕');
      } else if (cmd === 'project') {
        const name = o._[0] === '-' ? '' : (o._[0] || '');
        if (!o._[0] || o._.length < 2) throw new Error('用法：project <名|-> <编号…>');
        out(String(setProject(o._.slice(1), name)));
      } else if (cmd === 'title') {
        if (o.auto) {
          if (!o._.length) throw new Error('用法：title --auto <编号…>');
          for (const r of await retitle(o._)) out(r.id + '  ' + (r.to ? '「' + r.from + '」→「' + r.to + '」' : '「' + r.from + '」没改'));
        } else {
          if (o._.length < 2) throw new Error('用法：title <编号> <新标题> | title --auto <编号…>');
          const x = setTitle(o._[0], o._.slice(1).join(' '));
          out(x.id + '  「' + x.title + '」');
        }
      } else if (cmd === 'merge') {
        if (o._.length < 2) throw new Error('用法：merge <留下的> <并进去的…>');
        out(String(merge(o._[0], o._.slice(1))));
      } else if (cmd === 'dedupe') {
        const gs = await dedupe({ dry: o.dry });
        const all = load().items;
        const t = id => { const x = all.find(i => i.id === id); return id + '「' + (x ? x.title : '') + '」'; };
        if (!gs.length) out('没有重复的');
        for (const g of gs) out((o.dry ? '会并：' : '已并：') + t(g[0]) + ' ← ' + g.slice(1).map(t).join('、'));
      } else if (cmd === 'done' || cmd === 'open') {
        out(String(setState(o._, cmd)));
      } else if (cmd === 'del') {
        out(String(del(o._)));
      } else if (cmd === 'copy') {
        const x = load().items.find(i => i.id === o._[0]);
        if (x) out(continueText(x));
      } else if (cmd === 'scan') {
        out(JSON.stringify(await scan({ dry: o.dry, max: Number(o.max) || 0, minIdle: o['min-idle'] != null ? Number(o['min-idle']) : null, verbose: o.verbose })));
      } else if (cmd === 'prio') {
        if (o.auto) {
          const r = await autoPrio({ all: o.all, dry: o.dry });
          if (!r.length) out('没有要判的');
          for (const x of r) out(x.id + '  ' + (x.imp ? '重要' : '不重要') + (x.why ? '：' + x.why : '') + '　' + (WAIT_NAME[x.wait] || '') + (x.who ? '（' + x.who + '）' : '') + '　' + (PHASE_NAME[x.phase] || ''));
        } else {
          if (o._.length < 2) throw new Error('用法：prio <编号…> 重要|不重要|急|不急|等你拍板|等你动手|agent|等别人|等外部|进行中|搁置|等时机|自动 | prio --auto [--all] [--dry]');
          out(String(setPrio(o._.slice(0, -1), o._[o._.length - 1])));
        }
      } else if (cmd === 'archive') {
        const sub = o._[0] || 'list';
        if (sub === 'scan' || sub === 'list') {
          const r = sub === 'scan' ? await archiveScan({ dry: o.dry, noModel: o['no-model'] }) : { cand: (load().archive || {}).cand || [] };
          if (o.json) out(JSON.stringify(o.all && r.all ? r.all : r.cand));
          else if (o.all && r.all) for (const x of r.all.sort((a, b) => a.cls.localeCompare(b.cls) || b.last - a.last)) out((CONV_NAME[x.cls] || x.cls).padEnd(6, '　') + (x.cand ? '[候选] ' : '') + (NAMES[x.kind] || x.kind) + ' · ' + x.title + (x.why ? '（' + x.why + '）' : '') + (x.keep ? ' [别归]' : ''));
          else { if (!r.cand.length) out('没有可归档的'); for (const c of r.cand) out(c.key + '  ' + (NAMES[c.kind] || c.kind) + ' · ' + c.title + '（' + c.cls + '：' + c.why + '）'); }
        } else if (sub === 'do') {
          const r = archiveDo(o._.slice(1));
          out(JSON.stringify(r));
        } else if (sub === 'keep') {
          out(String(archiveKeep(o._.slice(1))));
        } else throw new Error('用法：archive scan|list|do <key…|all>|keep <key…>');
      } else if (cmd === 'list' || !cmd) {
        const xs = list(o.all);
        if (o.json) out(JSON.stringify(xs));
        else {
          let q = 0;
          for (const x of xs) {
            const g = x.state === 'done' ? 9 : x.group;
            if (g !== q) { q = g; out('── ' + (g === 9 ? '做完的' : GROUP_NAME[g]) + ' ──'); }
            const due = dueLabel(x);
            out(x.id + (x.strip ? ' ★' : '  ') + '[' + QUAD_NAME[x.quad] + '·' + (WAIT_NAME[x.wait] || '没判') + (x.who ? ':' + x.who : '') + '·' + PHASE_NAME[x.phase || 'active'] + '·' + x.idle + '天]'
              + (x.project ? '〔' + x.project + '〕' : '') + ' ' + (NAMES[x.kind] || x.kind) + ' · ' + x.title
              + (due ? '  ⏱' + due : '') + (x.stale ? '  ⚑' + x.stale : '') + (x.impWhy ? '  {' + x.impWhy + '}' : '')
              + ' — ' + x.text + (x.source === 'scan' ? '（推断）' : ''));
          }
        }
      } else {
        out('用法见文件开头');
        process.exitCode = 2;
      }
    } catch (e) { process.stderr.write(String(e && e.message || e) + '\n'); process.exitCode = 1; }
  })();
}
