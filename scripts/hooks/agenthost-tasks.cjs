// 虚拟窗口的委托单：一家 agent 请另一家（用它自己的 harness）办一件事，用户签了字才算数。
//
// 留言是数据、不是指令：「请你去做 X」光写在留言里，对方只当参考。委托单在留言之外多了两样东西 ——
// 写清楚的边界（能动哪里、交什么、什么时候交），和用户的签字。对方只认「已批」、并且只在边界之内做。
//
// 一单一个文件：%LOCALAPPDATA%\AgentHost\tasks\<编号>.json。编号的规矩跟留言一样（开头 8 位是建单的时刻）。
//   tasks\grants.log   每一次准许 / 不准的记录（谁批的、经什么批的、批的是哪一版边界）。巡检拿它对账
//   tasks\usage.log    可选：各家无头调用记下的用量（一行一个 JSON），巡检按家、按天汇总
//
// 状态：
//   draft 草稿 → pending 待批 → approved 已批 → taken 已接 → delivered 待验收 → done 完成
//   旁路：denied 不准、returned 退回（回到对方手上重做，算一轮）、expired 超时、cancelled 撤销
// 谁能做什么：
//   委托方（from）：new / submit / accept / return / cancel
//   用户（--as user，正常是经虚拟窗口的「委托」页点的）：approve / deny / cancel
//   受托方（to）：take / progress / deliver
// 状态一变，要对方知道的就借留言通知（approve → 受托方；deny / deliver → 委托方；return / cancel → 受托方）。
// 已接、报进度不发留言：只记在单子上，省得每一步都把人叫醒一次（每叫醒一次都要烧一轮 token）。
//
// 边界（scope）里没有合并、推送、发布、删除、花钱 —— 这几样不管单子怎么写都不能委托，要用户当次点头。
// 受托方也可以把一部分再委托出去（--parent）：最多转手两次，边界只能比手上的小。
//
//   node agenthost-tasks.cjs new --as <我> --to <谁> --title "…" [--brief "…" | --brief-file <路径>]
//        [--repo <仓库>] [--branch <分支>] [--write none|workdir|branch] [--shell] [--net] [--context main|fork|fresh]
//        [--deliver "交什么"] [--deadline "2026-10-16 18:00"] [--minutes 25] [--rounds 2] [--parent <编号>] [--submit]
//   node agenthost-tasks.cjs submit <编号> --as <我>
//   node agenthost-tasks.cjs mine --as <我> [--json]      该我办的、该我验收的；什么都没有只打一行「无」
//   node agenthost-tasks.cjs take | progress | deliver <编号> --as <我> ["话"]
//   node agenthost-tasks.cjs accept | return | cancel <编号> --as <我> ["话"]
//   node agenthost-tasks.cjs approve | deny <编号> --as user ["话"]
//   node agenthost-tasks.cjs show <编号> | list [--all] [--json] | patrol [--json] [--fix]
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

let notes = null;
try { notes = require('./agenthost-notes.cjs'); } catch {}

const NAME = /^[a-z0-9][a-z0-9_-]{0,31}$/;
const ID = /^[a-z0-9]{6,32}$/;
const USER = 'user';
const MAX_TITLE = 120, MAX_BRIEF = 16000, MAX_TEXT = 4000;
const MAX_HOPS = 2;              // 最多转手几次
const MAX_ACTIVE = 5;            // 一家手上同时最多几单（已批 + 已接 + 退回）
const DEFAULT_MINUTES = 25, DEFAULT_ROUNDS = 2;
const WRITES = ['none', 'workdir', 'branch'];
const CONTEXTS = ['main', 'fork', 'fresh'];
const LIVE = ['draft', 'pending', 'approved', 'taken', 'returned', 'delivered'];
const MIN = 60e3, HOUR = 3600e3;
const BOM = String.fromCharCode(0xFEFF);

function baseDir() { return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'AgentHost'); }
function rootDir() { return path.join(baseDir(), 'tasks'); }
function fileOf(id) { return path.join(rootDir(), id + '.json'); }
function norm(s) { return String(s == null ? '' : s).trim().toLowerCase(); }
function fail(code, message) { const e = new Error(message); e.code = code; return e; }
function noBom(s) { return s && s.charAt(0) === BOM ? s.slice(1) : s; }
function readText(file) { try { return noBom(fs.readFileSync(file, 'utf8')); } catch { return null; } }

// 说哪种话：跟留言脚本一致（它看 prefs.txt 和系统语言）；它不在就看系统
function T(zh, english) {
  if (notes && typeof notes.lang === 'function') return notes.lang() === 'en' ? english : zh;
  let loc = '';
  try { loc = Intl.DateTimeFormat().resolvedOptions().locale || ''; } catch {}
  return /^zh/i.test(loc) ? zh : english;
}

function text(s, max, what) {
  s = noBom(String(s == null ? '' : s)).replace(/\r\n?/g, '\n').trim();
  if (s.length > max) throw fail('too-long', T(what + '太长（最多 ' + max + ' 字）：长的东西写成文件，这里给路径', what + ' is too long (max ' + max + ' chars): put it in a file and give the path'));
  return s;
}

function makeId(ms) {
  if (notes) return notes.makeId(ms);
  let r = '';
  while (r.length < 4) r += Math.floor(Math.random() * 36).toString(36);
  return Math.floor(ms).toString(36).padStart(8, '0') + r;
}

function load(id) {
  id = norm(id);
  if (!ID.test(id)) throw fail('bad-id', T('委托编号不对：', 'Bad task id: ') + (id || T('（空）', '(empty)')));
  const t = readText(fileOf(id));
  if (t === null) throw fail('missing', T('没有这一单：', 'No such task: ') + id);
  try { return JSON.parse(t); } catch { throw fail('broken', T('这一单的文件坏了：', 'The task file is broken: ') + id); }
}

function save(task) {
  fs.mkdirSync(rootDir(), { recursive: true });
  const file = fileOf(task.id);
  const tmp = file + '.' + process.pid + '.tmp';
  try {
    fs.writeFileSync(tmp, JSON.stringify(task, null, 1));
    fs.renameSync(tmp, file);
  } catch (e) { try { fs.unlinkSync(tmp); } catch {} throw e; }
  return task;
}

function all() {
  let names;
  try { names = fs.readdirSync(rootDir()); } catch { return []; }
  const out = [];
  for (const n of names) {
    if (!n.endsWith('.json') || !ID.test(n.slice(0, -5))) continue;
    const t = readText(path.join(rootDir(), n));
    try { const o = JSON.parse(t); if (o && o.id) out.push(o); } catch {}
  }
  return out.sort((a, b) => a.created - b.created);
}

function log(task, who, what, now) {
  task.log = task.log || [];
  task.log.push({ at: now || Date.now(), who, what: String(what || '').slice(0, MAX_TEXT) });
}

function move(task, to, who, what, now) {
  task.state = to;
  task.updated = now || Date.now();
  log(task, who, what, task.updated);
}

// 时刻：「2026-10-16 18:00」按北京时间算（这台电脑的系统时区不是北京，见各家规矩）；也认 ISO 和毫秒数
function parseTime(s) {
  s = String(s || '').trim();
  if (!s) return 0;
  if (/^\d{12,14}$/.test(s)) return Number(s);
  const m = /^(\d{4})-(\d{1,2})-(\d{1,2})[ T](\d{1,2}):(\d{2})$/.exec(s);
  if (m) return Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4] - 8, +m[5]);
  const t = Date.parse(s);
  if (!isNaN(t) && /[zZ]|[+-]\d{2}:?\d{2}$/.test(s)) return t;
  throw fail('bad-time', T('看不懂这个时刻（写成 2026-10-16 18:00，北京时间）：', 'Cannot read the time (write it like 2026-10-16 18:00, Beijing time): ') + s);
}

function showTime(ms) {
  if (!ms) return '-';
  const d = new Date(ms + 8 * HOUR);
  const p = n => String(n).padStart(2, '0');
  return d.getUTCFullYear() + '-' + p(d.getUTCMonth() + 1) + '-' + p(d.getUTCDate()) + ' ' + p(d.getUTCHours()) + ':' + p(d.getUTCMinutes());
}

// ---- 边界 ----

function makeScope(o, to) {
  const write = norm(o.write || 'none');
  if (!WRITES.includes(write)) throw fail('bad-scope', T('--write 只能是 none / workdir / branch', '--write must be none, workdir or branch'));
  // 谁来做（用户 2026-10-15 定的三种）：main = 排进对方的主对话（看得见、要等它忙完这一轮）；
  // fork = 从主对话分叉出子对话（马上做、带上下文，最贵）；fresh = 起一个全新的后台实例（马上做、不带上下文）。
  // 空 = 让后台按情况挑（见 agent-bus 的 responder.py）
  const context = norm(o.context || '');
  if (context && !CONTEXTS.includes(context)) throw fail('bad-scope', T('--context 只能是 main / fork / fresh', '--context must be main, fork or fresh'));
  const scope = {
    context,
    repo: String(o.repo || '').trim(),
    branch: String(o.branch || '').trim(),
    write,
    shell: !!o.shell,
    net: !!o.net,
    minutes: Math.max(1, Math.min(240, Number(o.minutes) || DEFAULT_MINUTES)),
    rounds: Math.max(1, Math.min(5, Number(o.rounds) || DEFAULT_ROUNDS)),
  };
  if (write === 'branch') {
    if (!scope.repo) throw fail('bad-scope', T('要写分支就得说是哪个仓库（--repo）', 'Writing a branch needs --repo'));
    if (!scope.branch.startsWith(to + '/')) {
      throw fail('bad-scope', T('分支名必须以「' + to + '/」开头（受托方自己的前缀；main 这类不能委托）',
        'The branch must start with "' + to + '/" (the assignee\'s own prefix; main and the like cannot be delegated)'));
    }
  }
  return scope;
}

// child 是不是在 parent 之内（再委托时用：边界只能缩小）
function within(child, parent) {
  const rank = { none: 0, workdir: 1, branch: 2 };
  if (rank[child.write] > rank[parent.write]) return T('写的权限比手上的大', 'wider write access than the parent task');
  if (child.shell && !parent.shell) return T('手上的单子不许跑命令', 'the parent task does not allow shell');
  if (child.net && !parent.net) return T('手上的单子不许联网', 'the parent task does not allow network');
  if (child.repo && parent.repo && child.repo !== parent.repo) return T('换了仓库', 'a different repository');
  if (child.minutes > parent.minutes) return T('时长比手上的长', 'longer than the parent task');
  return '';
}

function scopeLine(s) {
  const w = { none: T('只读', 'read-only'), workdir: T('只写临时目录', 'temp dir only'), branch: T('写分支 ', 'branch ') + s.branch };
  return w[s.write] + (s.repo ? ' · ' + s.repo : '') + ' · ' + (s.shell ? T('能跑命令', 'shell') : T('不跑命令', 'no shell'))
    + ' · ' + (s.net ? T('能联网', 'network') : T('不联网', 'no network')) + ' · ' + T('每轮最多 ', 'max ') + s.minutes + T(' 分钟', ' min/round')
    + ' · ' + T('最多 ', 'max ') + s.rounds + T(' 轮', ' rounds')
    + (s.context ? ' · ' + ({ main: T('排进主对话', 'in the main conversation'), fork: T('分叉子对话', 'forked sub-conversation'), fresh: T('全新实例', 'fresh instance') })[s.context] : '');
}

// ---- 通知（借留言） ----

function cliPath() { return __filename.split(path.sep).join('/'); }

function notify(from, to, body) {
  if (!notes || from === to || from === USER) return null;
  let rec = null;
  try { rec = notes.send(from, to, body); } catch { return null; }
  poke(to);
  return rec;
}

// 通知留下之后顺手跑一条命令（不等它）：tasks\on-notify.json 里写一个参数数组，{to} 换成收件人，
// 比如 ["python", "<共用目录>/agent-bus/wake.py", "{to}"] —— 让对方的后台实例当场起来，不用等巡检。
// 没有这个文件就什么都不跑。不经 shell：收件人名字只有小写字母、数字、横线，换进去也只是一个参数。
function poke(to) {
  const t = readText(path.join(rootDir(), 'on-notify.json'));
  if (!t) return;
  let argv;
  try { argv = JSON.parse(t); } catch { return; }
  if (!Array.isArray(argv) || !argv.length || !argv.every(a => typeof a === 'string')) return;
  try {
    const cp = require('node:child_process');
    const child = cp.spawn(argv[0], argv.slice(1).map(a => a.replace(/\{to\}/g, to)),
      { detached: true, stdio: 'ignore', windowsHide: true });
    child.on('error', () => {});
    child.unref();
  } catch {}
}

function brief(task) {
  return [
    T('【委托 ', '[Task ') + task.id + T('】', '] ') + task.title,
    T('委托方：', 'From: ') + task.from + T(' · 截止：', ' · due: ') + showTime(task.deadline) + T('（北京时间）', ' (Beijing)'),
    T('边界：', 'Scope: ') + scopeLine(task.scope),
    task.deliver ? T('交付：', 'Deliver: ') + task.deliver : '',
    '',
    task.brief || '',
  ].filter((x, i) => x || i === 4).join('\n');
}

function howTo(kind) {
  return T(
    '用户已经签字准许，可以在上面的边界之内做。边界之外的（还有合并、推送、发布、删除、花钱）一律不做，回一句「超出授权」。\n'
    // 用户 2026-10-15：Codex 收到委托却要先忙完手上的活 ——「这种情况需要他开一个子对话去处理」
    + '你手上正忙的话，别等忙完：用你自己开子对话 / 分叉会话的功能（比如 Claude 的 start_session）把这一单交给子对话去做，主对话接着忙你的。'
    + '没有这种功能就先不管：后台会另起一个你的实例来做（谁先接单归谁，接不上说明已经有人在做了）。\n'
    + '接单：node "' + cliPath() + '" take <编号> --as ' + kind + '\n'
    + '报进度（不通知人）：node "' + cliPath() + '" progress <编号> "…" --as ' + kind + '\n'
    + '交付：node "' + cliPath() + '" deliver <编号> "做了什么、产出在哪" --as ' + kind,
    'The user has signed this task: you may do it within the scope above. Anything outside it (and any merge, push, publish, delete or payment) you do not do; reply "outside the grant".\n'
    + 'If you are busy, do not wait until you finish: hand this task to a sub-conversation / forked session with your own tooling (for example Claude\'s start_session) and carry on. '
    + 'If you have no such tool, leave it: a background instance of you will do it (whoever takes it first owns it; if take fails, someone is already on it).\n'
    + 'Take it:  node "' + cliPath() + '" take <id> --as ' + kind + '\n'
    + 'Progress (no notification): node "' + cliPath() + '" progress <id> "..." --as ' + kind + '\n'
    + 'Deliver:  node "' + cliPath() + '" deliver <id> "what was done, where the output is" --as ' + kind);
}

// ---- 动作 ----

function create(o, now) {
  now = now || Date.now();
  const from = norm(o.as), to = norm(o.to);
  if (!NAME.test(from) || from === USER) throw fail('bad-name', T('没说你是谁（--as <名字>）', 'Say who you are: --as <name>'));
  if (!NAME.test(to) || to === USER) throw fail('bad-name', T('受托方名字不对：', 'Bad assignee: ') + (to || T('（空）', '(empty)')));
  if (from === to) throw fail('self', T('不能委托给自己', 'You cannot delegate to yourself'));
  if (notes) {
    const known = notes.peers().map(p => p.id);
    if (!known.includes(to)) throw fail('unknown', T('没有叫 ' + to + ' 的。现在认得的：', 'Nobody is called ' + to + '. Known: ') + known.join(' '));
  }
  const title = text(o.title, MAX_TITLE, T('标题', 'The title'));
  if (!title) throw fail('empty', T('要有标题（--title）', 'A title is needed (--title)'));
  let body = o.brief || '';
  if (o.briefFile) {
    body = readText(o.briefFile);
    if (body === null) throw fail('no-file', T('读不了这个文件：', 'Cannot read the file: ') + o.briefFile);
  }
  body = text(body, MAX_BRIEF, T('说明', 'The brief'));
  const scope = makeScope(o, to);
  let hops = 0, parent = '';
  if (o.parent) {
    const p = load(o.parent);
    if (p.to !== from || !['taken', 'approved'].includes(p.state)) {
      throw fail('bad-parent', T('只能从自己手上已批 / 已接的单子再委托', 'You can only sub-delegate from a task you hold (approved or taken)'));
    }
    hops = (p.hops || 0) + 1;
    if (hops > MAX_HOPS) throw fail('too-deep', T('已经转手 ' + MAX_HOPS + ' 次了，再往下要用户来定', 'Already handed on ' + MAX_HOPS + ' times; the user has to decide further'));
    const why = within(scope, p.scope);
    if (why) throw fail('wider', T('再委托的边界不能比手上的大：', 'A sub-task cannot have a wider scope: ') + why);
    parent = p.id;
  }
  // 去重：同一个委托方、同一个受托方、同一个标题，还没了结的只能有一单
  const dup = all().find(t => t.from === from && t.to === to && t.title === title && LIVE.includes(t.state));
  if (dup) throw fail('duplicate', T('已经有一样的一单还没了结：', 'The same task is still open: ') + dup.id);
  const task = {
    id: makeId(now), from, to, title, brief: body, deliver: text(o.deliver, MAX_TEXT, T('交付', 'Deliver')),
    scope, deadline: parseTime(o.deadline), hops, parent,
    state: 'draft', created: now, updated: now, rounds: 0, grant: null, result: '', log: [],
  };
  log(task, from, T('建单', 'created'), now);
  save(task);
  if (o.submit) return submit(task.id, from, now);
  return task;
}

function need(task, who, roles, states) {
  if (!roles.includes(who)) throw fail('not-yours', T('这一步不归你做', 'This step is not yours') + '（' + task.id + '）');
  if (!states.includes(task.state)) {
    throw fail('bad-state', T('这一单现在是「' + stateName(task.state) + '」，不能这么做', 'This task is ' + task.state + '; you cannot do that now'));
  }
}

function submit(id, as, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.from], ['draft']);
  move(task, 'pending', who, T('提交，等用户签字', 'submitted for the user\'s signature'), now);
  return save(task);
}

// 用户签字。via：经什么签的（host = 虚拟窗口的「委托」页；cli = 命令行）。巡检对账时，经命令行签的会单独报给用户
function approve(id, as, words, opt, now) {
  opt = opt || {}; now = now || Date.now();
  const task = load(id); const who = norm(as);
  need(task, who, [USER], ['pending']);
  if (opt.write || opt.noShell || opt.noNet) {   // 当场改小：只能收，不能放
    const s = Object.assign({}, task.scope);
    if (opt.write) {
      const rank = { none: 0, workdir: 1, branch: 2 };
      if (!WRITES.includes(opt.write) || rank[opt.write] > rank[s.write]) throw fail('wider', T('签字时只能把边界改小', 'You can only narrow the scope when signing'));
      s.write = opt.write;
    }
    if (opt.noShell) s.shell = false;
    if (opt.noNet) s.net = false;
    task.scope = s;
  }
  const active = all().filter(t => t.to === task.to && ['approved', 'taken', 'returned'].includes(t.state)).length;
  if (active >= MAX_ACTIVE) throw fail('busy', T(task.to + ' 手上已经有 ' + active + ' 单了，先等它做完几单', task.to + ' already holds ' + active + ' tasks'));
  const via = process.env.AGENTHOST_SIGNER === 'host' ? 'host' : 'cli';
  task.grant = { by: USER, at: now, via, scope: Object.assign({}, task.scope) };
  move(task, 'approved', USER, T('准许', 'approved') + (words ? '：' + words : ''), now);
  save(task);
  grantLog(task, 'approve', via, now);
  notify(task.from, task.to, brief(task) + '\n\n' + howTo(task.to));
  return task;
}

function deny(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [USER], ['pending']);
  const via = process.env.AGENTHOST_SIGNER === 'host' ? 'host' : 'cli';
  move(task, 'denied', USER, T('不准', 'denied') + (words ? '：' + words : ''), now);
  save(task);
  grantLog(task, 'deny', via, now);
  notify(task.to, task.from, T('【委托 ' + task.id + ' 用户没准】', '[Task ' + task.id + ' denied by the user] ') + task.title + (words ? '\n' + words : ''));
  return task;
}

function grantLog(task, what, via, now) {
  try {
    fs.appendFileSync(path.join(rootDir(), 'grants.log'),
      JSON.stringify({ at: now, id: task.id, what, via, from: task.from, to: task.to, scope: task.scope }) + '\n');
  } catch {}
}

function take(id, as, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.to], ['approved', 'returned']);
  move(task, 'taken', who, T('接单', 'taken'), now);
  return save(task);
}

function progress(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.to], ['taken']);
  task.updated = now || Date.now();
  log(task, who, text(words, MAX_TEXT, T('进度', 'Progress')) || T('还在做', 'still working'), task.updated);
  return save(task);
}

function deliver(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.to], ['taken', 'approved', 'returned']);
  const result = text(words, MAX_TEXT, T('交付说明', 'The delivery note'));
  if (!result) throw fail('empty', T('交付要写做了什么、产出在哪', 'Say what was done and where the output is'));
  task.result = result;
  task.rounds = (task.rounds || 0) + 1;
  move(task, 'delivered', who, T('交付：', 'delivered: ') + result, now);
  save(task);
  notify(task.to, task.from, T('【委托 ' + task.id + ' 交付了，请验收】', '[Task ' + task.id + ' delivered — please verify] ') + task.title + '\n' + result + '\n\n'
    + T('自己跑、自己看，别只信对方说的。验收：\n', 'Check it yourself rather than trusting the report. Then:\n')
    + 'node "' + cliPath() + '" accept ' + task.id + ' --as ' + task.from + '\n'
    + 'node "' + cliPath() + '" return ' + task.id + T(' "哪里不对" --as ', ' "what is wrong" --as ') + task.from);
  return task;
}

function accept(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.from], ['delivered']);
  move(task, 'done', who, T('验收通过', 'accepted') + (words ? '：' + words : ''), now);
  return save(task);
}

function giveBack(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.from], ['delivered']);
  const why = text(words, MAX_TEXT, T('退回原因', 'The reason'));
  if (!why) throw fail('empty', T('退回要写哪里不对', 'Say what is wrong'));
  if ((task.rounds || 0) >= task.scope.rounds) {
    move(task, 'expired', who, T('退回，但轮数用完了，等用户定：', 'returned, but no rounds left; the user decides: ') + why, now);
    save(task);
    return task;
  }
  move(task, 'returned', who, T('退回：', 'returned: ') + why, now);
  save(task);
  notify(task.from, task.to, T('【委托 ' + task.id + ' 被退回，请再做一轮】', '[Task ' + task.id + ' returned — one more round] ') + task.title + '\n' + why + '\n\n' + howTo(task.to));
  return task;
}

function cancel(id, as, words, now) {
  const task = load(id); const who = norm(as);
  need(task, who, [task.from, USER], ['draft', 'pending', 'approved', 'taken', 'returned', 'delivered']);
  const wasHeld = ['approved', 'taken', 'returned'].includes(task.state);
  move(task, 'cancelled', who, T('撤销', 'cancelled') + (words ? '：' + words : ''), now);
  save(task);
  if (wasHeld) notify(task.from, task.to, T('【委托 ' + task.id + ' 撤销了，别再做】', '[Task ' + task.id + ' cancelled — stop working on it] ') + task.title + (words ? '\n' + words : ''));
  return task;
}

// 该我办的（我是受托方：已批 / 退回 / 已接）和该我验收的（我是委托方：待验收）
function mine(as) {
  const who = norm(as);
  return all().filter(t => (t.to === who && ['approved', 'returned', 'taken'].includes(t.state))
    || (t.from === who && t.state === 'delivered'));
}

// ---- 巡检：只做账，不起模型 ----
// 已批没人接、做着超时了、快到截止、经命令行签的字、签字记录对不上 —— 列出来；--fix 把过了截止的改成超时并通知委托方
function patrol(opt, now) {
  opt = opt || {}; now = now || Date.now();
  const out = [];
  const signed = new Map();
  for (const line of (readText(path.join(rootDir(), 'grants.log')) || '').split('\n')) {
    try { const g = JSON.parse(line); if (g.what === 'approve') signed.set(g.id, g); } catch {}
  }
  for (const t of all()) {
    if (!LIVE.includes(t.state)) continue;
    const since = now - (t.updated || t.created);
    if (t.deadline && now > t.deadline && t.state !== 'delivered') {
      out.push({ id: t.id, kind: 'overdue', to: t.to, text: T('过了截止还没交：', 'past due, not delivered: ') + t.title });
      if (opt.fix) {
        move(t, 'expired', 'patrol', T('过了截止', 'past due'), now); save(t);
        notify(t.to, t.from, T('【委托 ' + t.id + ' 过了截止，没交】', '[Task ' + t.id + ' is past due] ') + t.title);
      }
      continue;
    }
    if (t.state === 'approved' && since > (opt.idle || 30 * MIN)) {
      out.push({ id: t.id, kind: 'untaken', to: t.to, text: T(t.to + ' 还没接（批了 ' + Math.round(since / MIN) + ' 分钟）：', t.to + ' has not taken it (' + Math.round(since / MIN) + ' min): ') + t.title });
    }
    if (t.state === 'taken' && since > t.scope.minutes * MIN * 2) {
      out.push({ id: t.id, kind: 'quiet', to: t.to, text: T(t.to + ' 接了之后 ' + Math.round(since / MIN) + ' 分钟没动静：', t.to + ' silent for ' + Math.round(since / MIN) + ' min: ') + t.title });
    }
    if (t.state === 'pending') out.push({ id: t.id, kind: 'pending', to: USER, text: T('等你签字：', 'waiting for your signature: ') + t.from + ' → ' + t.to + ' ' + t.title });
    if (t.deadline && t.deadline - now < HOUR && t.deadline > now) out.push({ id: t.id, kind: 'due', to: t.to, text: T('一小时内到截止：', 'due within an hour: ') + t.title });
    if (t.grant) {
      const g = signed.get(t.id);
      if (!g) out.push({ id: t.id, kind: 'unsigned', to: USER, text: T('有签字但签字记录里没有，可能是伪造的：', 'signed but missing from the grant log, possibly forged: ') + t.title });
      else if (g.via !== 'host') out.push({ id: t.id, kind: 'cli-signed', to: USER, text: T('这一单是经命令行签的字，不是在虚拟窗口里点的：', 'signed from the command line, not in the Virtual Window: ') + t.title });
    }
  }
  return out;
}

function stateName(s) {
  const zh = { draft: '草稿', pending: '待批', approved: '已批', taken: '已接', delivered: '待验收', done: '完成', denied: '不准', returned: '退回', expired: '超时', cancelled: '撤销' };
  return T(zh[s] || s, s);
}

module.exports = {
  create, submit, approve, deny, take, progress, deliver, accept, giveBack, cancel,
  mine, patrol, all, load, brief, scopeLine, stateName, showTime, parseTime, rootDir, USER,
};

// ---- 命令行 ----

function asciiJson(o) {
  const s = JSON.stringify(o);
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    out += c < 127 ? s.charAt(i) : String.fromCharCode(92) + 'u' + c.toString(16).padStart(4, '0');
  }
  return out;
}

function line(t) {
  return t.id + '  ' + stateName(t.state) + '  ' + t.from + ' → ' + t.to + '  ' + t.title
    + (t.deadline ? T('  截止 ', '  due ') + showTime(t.deadline) : '');
}

function cli(argv) {
  const args = [];
  const o = {};
  const flags = { '--shell': 'shell', '--net': 'net', '--submit': 'submit', '--json': 'json', '--all': 'all', '--fix': 'fix', '--no-shell': 'noShell', '--no-net': 'noNet' };
  const values = { '--as': 'as', '--to': 'to', '--title': 'title', '--brief': 'brief', '--brief-file': 'briefFile', '--repo': 'repo', '--branch': 'branch',
    '--write': 'write', '--deliver': 'deliver', '--deadline': 'deadline', '--minutes': 'minutes', '--rounds': 'rounds', '--parent': 'parent', '--context': 'context', '--file': 'file' };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--') { for (i++; i < argv.length; i++) args.push(argv[i]); break; }
    const eq = a.indexOf('=');
    const key = eq > 0 ? a.slice(0, eq) : a;
    if (flags[a]) o[flags[a]] = true;
    else if (values[key]) o[values[key]] = eq > 0 ? a.slice(eq + 1) : (argv[++i] || '');
    else args.push(a);
  }
  const cmd = (args.shift() || '').toLowerCase();
  const out = s => process.stdout.write(s + '\n');
  let words = args.slice(1).join(' ');
  if (o.file) { words = readText(o.file); if (words === null) throw fail('no-file', T('读不了这个文件：', 'Cannot read the file: ') + o.file); }
  const one = t => { if (o.json) out(asciiJson(t)); else out(line(t)); return 0; };
  switch (cmd) {
    case 'new': return one(create(o));
    case 'submit': return one(submit(args[0], o.as));
    case 'approve': return one(approve(args[0], o.as, words, o));
    case 'deny': return one(deny(args[0], o.as, words));
    case 'take': return one(take(args[0], o.as));
    case 'progress': return one(progress(args[0], o.as, words));
    case 'deliver': return one(deliver(args[0], o.as, words));
    case 'accept': return one(accept(args[0], o.as, words));
    case 'return': return one(giveBack(args[0], o.as, words));
    case 'cancel': return one(cancel(args[0], o.as, words));
    case 'show': {
      const t = load(args[0]);
      if (o.json) { out(asciiJson(t)); return 0; }
      out(brief(t)); out('');
      out(T('状态：', 'State: ') + stateName(t.state) + (t.result ? T('\n交付：', '\nDelivered: ') + t.result : ''));
      for (const l of t.log || []) out('  ' + showTime(l.at) + '  ' + l.who + '  ' + l.what);
      return 0;
    }
    case 'mine': {
      const list = mine(o.as);
      if (o.json) { out(asciiJson({ tasks: list })); return 0; }
      if (!list.length) { out(T('无', 'none')); return 0; }   // 什么都没有只打一行：定时来查的那几家省 token
      for (const t of list) {
        out(line(t));
        if (t.to === norm(o.as) && t.state !== 'taken') { out(brief(t)); out(''); out(howTo(t.to).replace(/<编号>|<id>/g, t.id)); out(''); }
      }
      return 0;
    }
    case 'list': {
      const list = all().filter(t => o.all || LIVE.includes(t.state));
      if (o.json) { out(asciiJson({ tasks: list })); return 0; }
      if (!list.length) out(T('没有委托。', 'No tasks.'));
      for (const t of list) out(line(t));
      return 0;
    }
    case 'patrol': {
      const found = patrol(o);
      if (o.json) { out(asciiJson({ findings: found })); return 0; }
      if (!found.length) out(T('无', 'none'));
      for (const f of found) out(f.id + '  ' + f.text);
      return 0;
    }
  }
  process.stderr.write([
    T('虚拟窗口的委托单（用户签了字才算数）', 'Virtual Window tasks (they count only once the user signs)'),
    '  new --as <me> --to <who> --title "…" [--brief "…"|--brief-file <path>] [--repo R] [--branch B] [--write none|workdir|branch] [--shell] [--net] [--deliver "…"] [--deadline "2026-10-16 18:00"] [--minutes N] [--rounds N] [--parent <id>] [--context main|fork|fresh] [--submit]',
    '  submit <id> --as <me>      mine --as <me> [--json]      show <id>      list [--all] [--json]',
    '  take | progress | deliver <id> --as <me> ["…"]      accept | return | cancel <id> --as <me> ["…"]',
    '  approve | deny <id> --as user ["…"] [--write none|workdir] [--no-shell] [--no-net]      patrol [--json] [--fix]',
  ].join('\n') + '\n');
  return cmd ? 2 : 0;
}

if (require.main === module) {
  let code = 0;
  try { code = cli(process.argv.slice(2)); }
  catch (e) { process.stderr.write((e && e.message ? e.message : String(e)) + '\n'); code = 2; }
  process.exitCode = code;
}
