// 虚拟窗口（agent-host）的 OpenCode 状态插件。OpenCode 没有「命令钩子」，只有插件：
// 放进 ~/.config/opencode/plugin/ 它启动时自己加载（桌面端、命令行都读这个目录）。
// 跟 agenthost-hook.cjs 写同一种会话文件：%LOCALAPPDATA%\AgentHost\sessions\opencode-<会话id>.json
//   { kind, sessionId, cwd, event, state, tool, at, slot, entry, reply }   entry：opencode-desktop / cli
//
// 不拦截、不改参数、不替用户批准任何东西。任何异常都吞掉，别让 OpenCode 因为我们出问题。
// 除了记状态，只多做一件事：别的 agent 给 OpenCode 留了话还没看（留言，见 agenthost-notes.cjs）时，
// 趁用户发话把它们附在那条消息后面交给模型（chat.message）；它这一轮做完、会话闲下来时有从没送到过的新留言，
// 就替它发一句把它叫住接着处理（见 holdForNotes）。信箱空着就什么都不加。
// 由虚拟窗口「装钩子」时写进来、卸载时删掉；别手改，改了下次装会被覆盖。
//
// 事件 → 状态：
//   session.status busy / retry        → working
//   session.idle、session.status idle  → done（还有没答完的权限 / 提问就仍是 asking）
//   permission.updated / .asked、question.asked → asking；对应的 replied / rejected 之后回到 working
//   session.deleted                     → stopped
// 子 agent（task 工具开的子会话，带 parentID）不算：它不是一张卡片，而且它先做完会把父会话盖成「做完了」。
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import { createRequire } from "node:module"

const DIR = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"), "AgentHost", "sessions")
const SLOT = process.env.AGENTHOST_SLOT || ""
// 桌面端的服务跑在 Electron 的 node 子进程里，命令行是 Bun：宿主桌面那一格只认桌面端的记录，
// 你在终端里自己开的 opencode 不算（跟 Claude 的 CLAUDE_CODE_ENTRYPOINT 一个意思）
const ENTRY = process.versions && process.versions.electron ? "opencode-desktop" : "cli"
const ASK = new Set(["permission.updated", "permission.asked", "question.asked"])
const ANSWERED = new Set(["permission.replied", "question.replied", "question.rejected"])
// 写会话文件被别的进程挡住时（见 flush）：哪些错算被挡住，隔多久再试（毫秒）
const BUSY = new Set(["EPERM", "EBUSY", "EACCES"])
const WAITS = [0, 15, 30, 60, 120, 250, 500]

// 留言的存取在 agenthost-notes.cjs（装钩子时释放到 %LOCALAPPDATA%\AgentHost\bin）。用到时才加载；没有它就当没有留言这回事
const NOTES_CJS = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"), "AgentHost", "bin", "agenthost-notes.cjs")
let notesMod
function notes() {
  if (notesMod === undefined) {
    try { notesMod = createRequire(import.meta.url)(NOTES_CJS) } catch { notesMod = null }
  }
  return notesMod
}

// 长对话提醒（agenthost-hygiene.cjs，同一个目录）：会话上下文过线（默认 25 万），趁用户发话附一段提醒，跟留言同一种附法
const HYGIENE_CJS = path.join(path.dirname(NOTES_CJS), "agenthost-hygiene.cjs")
let hygieneMod
function hygiene() {
  if (hygieneMod === undefined) {
    try { hygieneMod = createRequire(import.meta.url)(HYGIENE_CJS) } catch { hygieneMod = null }
  }
  return hygieneMod
}

// 给附上去的那一段起一个编号，照 OpenCode 自己的规矩（prt_ + 12 位十六进制的时刻 + 14 位随机）：
// 时刻取现在，排在用户刚发的那一段后面
function partId() {
  const t = ((BigInt(Date.now()) * 0x1000n + 1n) & 0xffffffffffffn).toString(16).padStart(12, "0")
  const abc = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
  let r = ""
  for (let i = 0; i < 14; i++) r += abc[Math.floor(Math.random() * abc.length)]
  return "prt_" + t + r
}

const NOTES_ENV = process.env.AGENTHOST_NOTES || ""
const NOTES_OFF = /^(0|off|no|false)$/i.test(NOTES_ENV)
const NOTES_ON = /^(1|on|yes|true)$/i.test(NOTES_ENV)
// 我们替它发的那一句发出去之后，这么久还没见它进会话：当它没成（单测里调小）
const OURS_WAIT = Number(process.env.AGENTHOST_OURS_WAIT_MS) > 0 ? Number(process.env.AGENTHOST_OURS_WAIT_MS) : 30000

export default async ({ client, directory }) => {
  const children = new Set()
  const pending = new Map()   // 会话 → 还没答的权限 / 提问 id
  const recs = new Map()      // 会话 → 最后写下的记录
  const retries = new Map()   // 会话 → 等着再写一次的定时器
  const armed = new Set()     // 用户发过话、之后还没闲下来过的会话：头一次闲下来才算「这一轮做完」（见 onIdle）
  const deciding = new Set()  // 正在看要不要叫住的会话（要先问一下这一轮是怎么停的）：这工夫别把它记成做完
  const ours = new Map()      // 我们替它发的那一句正在路上的会话 → { at, brief }
  const active = new Map()    // 会话 → 最近一次有动静的时刻（在忙、出了一段内容、有人发话）
  const missed = new Map()    // 会话 → 最近一次「闲下来了」却因为那一句还在路上没理会的时刻
  const ctxTokens = new Map() // 会话 → 最近一条助手消息的上下文长度（input + cache.read + cache.write）

  // 把这个会话最新的记录写到盘上：写 .tmp → 改名盖上去。改名那一下只要别的进程正开着目标文件就失败
  //（EPERM；宿主每 2 秒读一遍，外部程序也可能在读）。以前异常被吞掉：这次更新丢了，.tmp 留在目录里。
  // 现在过一会儿再试，加起来 1 秒。这里是常驻进程，不能堵着等，用定时器 ——
  // 一个会话只留一个，到点写的是那时最新的记录：旧的一次重试不会把后来更新的记录盖掉。
  function flush(sid, n) {
    clearTimeout(retries.get(sid))
    retries.delete(sid)
    const file = path.join(DIR, "opencode-" + sid + ".json")
    const tmp = file + "." + process.pid + ".tmp"
    try {
      fs.mkdirSync(DIR, { recursive: true })
      fs.writeFileSync(tmp, JSON.stringify(recs.get(sid)))
      fs.renameSync(tmp, file)
    } catch (e) {
      // 等的时候不留 .tmp（进程这时退了也没有垃圾），再试时重写
      try { fs.unlinkSync(tmp) } catch {}
      if (!BUSY.has(e && e.code) || n >= WAITS.length) return
      const t = setTimeout(() => flush(sid, n + 1), WAITS[n])
      if (t && t.unref) t.unref()   // 别为了这一下拖着 OpenCode 不退出
      retries.set(sid, t)
    }
  }

  function write(sid, state, event, extra) {
    if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$/.test(sid)) return
    const prev = recs.get(sid)
    const rec = {
      kind: "opencode", sessionId: sid, cwd: directory || process.cwd(), event, state,
      tool: (extra && extra.tool) || (prev && prev.tool) || "",
      at: Date.now(), slot: SLOT, entry: ENTRY,
      reply: (extra && extra.reply) || (state === "done" ? "" : (prev && prev.reply) || ""),
    }
    recs.set(sid, rec)
    flush(sid, 0)
  }

  // 这一轮最后说的话（给读这些文件的外部程序用，截 2000 字）：做完之后再去问一次，拿不到就算了
  async function fillReply(sid) {
    try {
      const r = await client.session.messages({ path: { id: sid } })
      const msgs = (r && r.data) || r || []
      for (let i = msgs.length - 1; i >= 0; i--) {
        const m = msgs[i]
        if (!m || !m.info || m.info.role !== "assistant") continue
        const txt = (m.parts || []).filter((p) => p.type === "text" && p.text).map((p) => p.text).join("\n").trim()
        if (!txt) continue
        const cur = recs.get(sid)
        if (cur && cur.state === "done") write(sid, "done", cur.event, { reply: txt.slice(0, 2000) })
        return
      }
    } catch {}
  }

  function sessionOf(type, p) {
    if (p.sessionID) return p.sessionID
    if (p.part && p.part.sessionID) return p.part.sessionID
    if (p.info) return type.startsWith("session.") ? p.info.id : p.info.sessionID
    return ""
  }

  // 用户发话：这一家有这个会话还没见过的留言，就附在这条消息后面（synthetic：模型看得到，界面上不显示）。
  // 头一次轮到没标已读的都提一遍，之后只送新来的 —— 跟钩子小程序同一套（见 agenthost-notes.cjs 的 forSession）。
  // 子 agent 的会话不送；起 OpenCode 的程序带了 AGENTHOST_NOTES=off（无头问一句话就走的）也不送。
  async function attachNotes(input, output) {
    const n = notes()
    if (!n || !output || !Array.isArray(output.parts) || !output.message) return
    const sid = (input && input.sessionID) || output.message.sessionID || ""
    if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$/.test(sid)) return
    const c = n.counts("opencode")
    if (!c.new && !c.open) return
    const log = (what) => n.logDelivery("opencode", "chat.message", sid, what + " (new " + c.new + ", open " + c.open + ")")
    // 没送的原因只在还有「没送到过的」时记
    const forced = /^(1|on|yes|true)$/i.test(process.env.AGENTHOST_NOTES || "")
    if (/^(0|off|no|false)$/i.test(process.env.AGENTHOST_NOTES || "")) { if (c.new) log("skip: AGENTHOST_NOTES=off"); return }
    if (n.deliveryOff("opencode")) { if (c.new) log("skip: deliver.off"); return }
    if (children.has(sid) && !forced) { if (c.new) log("skip: subagent"); return }
    // 这个会话还没见过哪些、头一次轮到要不要都提一遍、记成送到过：都在 forSession 里（跟钩子小程序同一套）
    const r = n.forSession("opencode", sid, { start: false })
    if (!r.text) return
    output.parts.push({ id: partId(), sessionID: sid, messageID: output.message.id, type: "text", text: r.text, synthetic: true })
    log("sent " + r.shown.length + " of " + r.total + " unread" + (r.stale ? ", " + r.stale + " stale" : "") + (r.moved && r.moved.length ? ", " + r.moved.length + " now open" : ""))
  }

  // 长对话：上下文过线就附一段提醒（模型看得到、界面不显示）。子 agent、无头的（AGENTHOST_NOTES=off）不送
  function attachHygiene(sid, output) {
    const h = hygiene()
    if (!h || children.has(sid) || NOTES_OFF) return
    // 这个进程里还没见过它的助手消息（OpenCode 刚开、命令行接着上一次的会话）：用上次记下的长度
    let n = ctxTokens.get(sid) || 0
    if (!n) { try { const rec = h.load("opencode", sid); n = (rec && rec.tokens) || 0 } catch {} }
    if (!n) return
    if (!output || !Array.isArray(output.parts) || !output.message) return
    const text = h.checkTokens("opencode", sid, n, { cwd: directory || process.cwd() })
    if (!text) return
    output.parts.push({ id: partId(), sessionID: sid, messageID: output.message.id, type: "text", text, synthetic: true })
  }

  // 这一轮做完、会话闲下来了：有从没送到过的新留言，就替它发一句把它叫住接着处理（跟钩子小程序的 endNote 一个意思）。
  // OpenCode 没有「拦住别停」的钩子，办法是往这个会话里再发一条消息：界面上看得见的只有一句（brief），
  // 留言正文放在模型看得到、界面不显示的那一段里（synthetic，跟 chat.message 附的那一段一样）。
  // 几道闸：
  //   - 用户发话之后头一次闲下来才算「这一轮做完」（armed）；之后再闲下来（它自己跑的命令、压缩上下文）不算，
  //     我们替它发的那一句引出来的那一轮做完也不算 —— 一个会话一轮只叫住一次；
  //   - 这一轮得是正常做完的：OpenCode 不管怎么停（做完、被你按停、出错）发的都是同一种「闲下来了」，
  //     所以先看最后一条回复 —— 带着错误的（按停也算）、根本没有回复的，不接话；
  //   - 子 agent 的会话不叫；
  //   - 只在桌面端做：命令行里跑一句就退的（opencode run）会话一闲下来进程就没了，叫不住。
  //     起它的程序带了 AGENTHOST_NOTES=on 的照做。
  // 先过一遍不用等的那几道闸；过了返回要用的东西，没过返回 null
  function mayHold(sid, type) {
    const n = notes()
    if (!n || typeof n.turnEndOff !== "function") return null    // 留言脚本没有、或者是旧的（没跟着一起换）
    if (children.has(sid)) return null
    if (ENTRY !== "opencode-desktop" && !NOTES_ON) return null
    const c = n.counts("opencode")
    if (!c.new) return null
    const log = (what) => n.logDelivery("opencode", type, sid, what + " (new " + c.new + ", open " + c.open + ")")
    if (NOTES_OFF) { log("skip: AGENTHOST_NOTES=off"); return null }
    if (n.deliveryOff("opencode")) { log("skip: deliver.off"); return null }
    if (n.turnEndOff("opencode")) { log("skip: turnend.off"); return null }
    const send = client && client.session && (client.session.promptAsync || client.session.prompt)
    if (typeof send !== "function") { log("skip: no way to prompt this session"); return null }
    return { n, log, send, wait: send === client.session.promptAsync }
  }

  // 返回真 = 叫住了（调用方别把它记成「做完了」）
  async function holdForNotes(sid, ctx) {
    const { n, log, send } = ctx
    const body = { parts: [] }
    let ended = "ok"
    try {
      const got = await client.session.messages({ path: { id: sid } })
      const msgs = (got && got.data) || got || []
      let replied = false
      for (let i = msgs.length - 1; i >= 0; i--) {
        const info = msgs[i] && msgs[i].info
        if (!info) continue
        if (info.role === "assistant") {
          if (!replied && info.error) ended = "ended with an error or was stopped"
          replied = true
          continue
        }
        if (info.role !== "user") continue
        if (!replied) ended = "no reply"
        // 接着用它上一句用的 agent 和型号：不指定的话 OpenCode 退回默认的 agent —— 它本来在 plan 里，不该被换到 build
        if (info.agent) body.agent = info.agent
        if (info.model && info.model.providerID && info.model.modelID) body.model = { providerID: info.model.providerID, modelID: info.model.modelID }
        break
      }
    } catch { ended = "could not read the session" }
    if (ended !== "ok") { log("skip: this turn did not finish normally (" + ended + ")"); return false }
    // 认领（new → open）在 forSession 里做：两个会话同时闲下来也只叫住认领到的那一个
    const r = n.forSession("opencode", sid, { end: true })
    if (!r.text) return false
    body.parts = [
      { type: "text", text: r.brief || "[notes]" },
      { type: "text", text: r.text, synthetic: true },
    ]
    const mark = { at: Date.now(), brief: body.parts[0].text }
    ours.set(sid, mark)
    // 没发出去：这个会话没看到，别算它见过（留言已经是「送到过」，它下次有人发话时照常提）；灯也别停在「在干活」
    const failed = (why) => {
      if (ours.get(sid) === mark) ours.delete(sid)
      try { if (typeof n.forget === "function") n.forget("opencode", sid, r.shown) } catch {}
      log("held at turn end but the prompt failed: " + why)
      const cur = recs.get(sid)
      if (cur && cur.state === "working" && (active.get(sid) || 0) <= mark.at) write(sid, "done", "session.idle")
    }
    const sent = Promise.resolve().then(() => send.call(client.session, { path: { id: sid }, body }))
      .then((res) => (res && res.error ? (typeof res.error === "string" ? res.error : JSON.stringify(res.error).slice(0, 200)) : ""),
        (e) => String((e && e.message) || e).slice(0, 200) || "error")
    if (ctx.wait) {
      // promptAsync：一收下就返回，等它一下就知道发没发出去
      const why = await sent
      if (why) { failed(why); return false }
    } else {
      // 老接口 prompt 要等这一整轮跑完才返回：不等它，出了错再收拾
      sent.then((why) => { if (why) failed(why) })
    }
    log("held at turn end: sent " + r.shown.length + " of " + r.total + " unread" + (r.moved && r.moved.length ? ", " + r.moved.length + " now open" : ""))
    // 那一句要是一直没被认出来（收下了却没跑起来；或者跑了、可它进会话时的样子我们没认出来）：到点把记号撤掉。
    // 这工夫一点动静都没有、或者最后一个动静之后它已经闲下来过：灯别停在「在干活」
    const t = setTimeout(() => {
      if (ours.get(sid) !== mark) return
      ours.delete(sid)
      const cur = recs.get(sid)
      const last = active.get(sid) || 0
      if (cur && cur.state === "working" && (last <= mark.at || (missed.get(sid) || 0) >= last)) {
        write(sid, "done", "session.idle")
        fillReply(sid)
      }
    }, OURS_WAIT)
    if (t && t.unref) t.unref()
    return true
  }

  // 我们替它发的那一句还在路上吗（发出去不久、还没见它进会话）
  function oursPending(sid) {
    const o = ours.get(sid)
    if (!o) return false
    if (Date.now() - o.at < OURS_WAIT) return true
    ours.delete(sid)
    return false
  }

  // 会话闲下来了。prev 是这个会话上一条记录
  async function onIdle(sid, type) {
    // 刚把它叫住、或者正在看要不要叫：别记成做完（记下来过这么一次，那一句要是一直不进会话，到点靠它收场）
    if (oursPending(sid) || deciding.has(sid)) { missed.set(sid, Date.now()); return }
    pending.delete(sid)
    const ctx = armed.delete(sid) ? mayHold(sid, type) : null
    if (ctx) {
      deciding.add(sid)
      let held = false
      try { held = await holdForNotes(sid, ctx) } catch {}
      deciding.delete(sid)
      if (held) { write(sid, "working", type); return }
    }
    write(sid, "done", type)
    fillReply(sid)
  }

  return {
    "chat.message": async (input, output) => {
      try {
        const sid = (input && input.sessionID) || (output && output.message && output.message.sessionID) || ""
        if (sid) active.set(sid, Date.now())
        // 是不是我们替它发的那一句：头一段就是那句看得见的话。不算用户发话，留言已经在里面了。
        //（用户正好这时也发了一句的话，那一句照常算用户发话，不会被当成我们的吞掉。）
        const o = sid ? ours.get(sid) : null
        if (o && output && Array.isArray(output.parts) && output.parts[0] && output.parts[0].text === o.brief) {
          ours.delete(sid)
          return
        }
        if (sid && !children.has(sid)) armed.add(sid)
        await attachNotes(input, output)
        attachHygiene(sid, output)
      } catch {}
    },
    event: async ({ event }) => {
      try {
        const type = (event && event.type) || ""
        const p = (event && event.properties) || {}
        if ((type === "session.created" || type === "session.updated") && p.info && p.info.parentID) {
          children.add(p.info.id)
          return
        }
        const sid = sessionOf(type, p)
        if (!sid || children.has(sid)) return
        // 助手消息带着用量：记下上下文长度（长对话提醒用）
        if (type === "message.updated" && p.info && p.info.role === "assistant" && p.info.tokens) {
          const t = p.info.tokens, c = t.cache || {}
          const total = (t.input || 0) + (c.read || 0) + (c.write || 0)
          if (total && total !== ctxTokens.get(sid)) {
            ctxTokens.set(sid, total)
            // 记到盘上：宿主悬停提示、下次开 OpenCode 都看得到
            const h = hygiene()
            if (h) { try { h.record("opencode", sid, { tokens: total, approx: false }, { cwd: directory || process.cwd() }) } catch {} }
          }
        }
        const prev = recs.get(sid)
        const asks = pending.get(sid) || new Set()
        if (type === "message.part.updated" || ASK.has(type)
          || (type === "session.status" && p.status && (p.status.type === "busy" || p.status.type === "retry"))) active.set(sid, Date.now())

        if (ASK.has(type)) {
          asks.add(p.id || p.requestID || type)
          pending.set(sid, asks)
          write(sid, "asking", type)
        } else if (ANSWERED.has(type)) {
          asks.delete(p.permissionID || p.requestID || p.id)
          if (asks.size === 0) { pending.delete(sid); write(sid, "working", type) }
        } else if (type === "session.status") {
          const st = (p.status && p.status.type) || ""
          if (st === "busy" || st === "retry") {
            if (asks.size === 0 && (!prev || prev.state !== "working")) write(sid, "working", type)
          } else if (st === "idle" && prev && prev.state !== "done") {
            await onIdle(sid, type)
          }
        } else if (type === "session.idle") {
          if (!prev || prev.state !== "done") await onIdle(sid, type)
        } else if (type === "session.deleted") {
          pending.delete(sid)
          armed.delete(sid)
          ours.delete(sid)
          active.delete(sid)
          missed.delete(sid)
          write(sid, "stopped", type)
        } else if (type === "message.part.updated" && prev && prev.state === "working") {
          // 一轮跑很久时宿主会把 20 分钟没动静的「在干活」当成卡死：干活途中隔一分钟续一下；顺手记下在用的工具
          const tool = p.part && p.part.type === "tool" ? p.part.tool : ""
          if (Date.now() - prev.at > 60000 || (tool && tool !== prev.tool)) write(sid, "working", prev.event, { tool })
        }
      } catch {}
    },
  }
}
