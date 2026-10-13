// 虚拟窗口（agent-host）的 OpenCode 状态插件。OpenCode 没有「命令钩子」，只有插件：
// 放进 ~/.config/opencode/plugin/ 它启动时自己加载（桌面端、命令行都读这个目录）。
// 跟 agenthost-hook.cjs 写同一种会话文件：%LOCALAPPDATA%\AgentHost\sessions\opencode-<会话id>.json
//   { kind, sessionId, cwd, event, state, tool, at, slot, entry, reply }   entry：opencode-desktop / cli
//
// 只做观察：不拦截、不改参数、不替用户批准任何东西。任何异常都吞掉，别让 OpenCode 因为我们出问题。
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

export default async ({ client, directory }) => {
  const children = new Set()
  const pending = new Map()   // 会话 → 还没答的权限 / 提问 id
  const recs = new Map()      // 会话 → 最后写下的记录
  const retries = new Map()   // 会话 → 等着再写一次的定时器

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

  return {
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
        const prev = recs.get(sid)
        const asks = pending.get(sid) || new Set()

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
            pending.delete(sid)
            write(sid, "done", type)
            fillReply(sid)
          }
        } else if (type === "session.idle") {
          if (!prev || prev.state !== "done") {
            pending.delete(sid)
            write(sid, "done", type)
            fillReply(sid)
          }
        } else if (type === "session.deleted") {
          pending.delete(sid)
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
