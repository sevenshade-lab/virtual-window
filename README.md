# Virtual Window

**One window for all your AI coding agents on Windows.**

Virtual Window embeds the desktop apps of AI agents — Claude, Codex, Cursor, OpenCode, Antigravity and others — plus their CLI sessions into a single window. Use it as a grid to see several agents at once, or as a queue that shows one agent at a time: the sessions waiting for your reply line up like pages of a book on the right, and the ones still working sit as small tabs on the left.

[中文说明](#中文说明)

## Features

- **Real embedding, not screenshots.** The agents' own windows run inside panes. Input, IME and clipboard behave the same as in the standalone apps.
- **Grid and queue views.** The grid has several layouts per pane count, plus zoom and drag-to-swap. The queue view puts the session that needs you in front; see [The queue view](#the-queue-view).
- **Status lights from official hooks.** Claude Code, Codex, Cursor, Antigravity (Gemini) and an OpenCode plugin report *working / asking / done* per session. Agents without hooks fall back to CPU activity. A light turns yellow only when the agent is asking you something or has finished and you have not looked yet; see [What the lights mean](#what-the-lights-mean).
- **Hover to see why it stopped.** The tooltip on a light or a tab says what the agent wants from you: which tool it needs approved, whether it asked a question or has a plan to review, or the opening of its last reply (plus its closing question, if it ended with one) and the folder it is working in.
- **Notifications jump to the agent.** Clicking an agent's own "task finished" notification brings its pane to the front.
- **Notes between agents.** Agents can leave notes for each other. The hooks hand unread notes to the receiving agent's model when its next session starts, the next time you talk to it, or at the end of the turn it is busy with, and an agent that cannot receive them that way gets a small count on its pane; see [Notes between agents](#notes-between-agents).
- **Subscription quota.** A strip next to the toolbar buttons shows how much of each plan is left (5-hour, weekly and monthly limits as rings), read from each vendor's own usage endpoint with the credentials already on your machine. Click it for details. Each plan can be hidden from the panel and shown again with one click; a plan that drops to a free tier is tucked away automatically, and a hidden plan comes back on its own when a paid subscription is detected again. Accounts live in `%LOCALAPPDATA%\AgentHost\quota-accounts.json`.
- **To-do strip.** Unfinished work found at the end of agent sessions is collected into a list under the toolbar, grouped by what it is waiting on (you, an agent, someone else). Click an item to copy a "continue" prompt and jump to that session.
- **New session panel.** Open an agent's desktop app, or start its CLI in a recent folder.
- **A small footprint.** The top bar is a single row of status text and buttons, and each pane header is just an icon, a name, a light and two icon buttons, so the agents get most of the window.
- **Light / dark themes.** Two palettes (Google blue, classic orange). Optionally switches the Windows app theme too, so the embedded agents follow.
- **English and Chinese UI.** The button next to the minimize button switches languages.
- **Safe to close.** Closing or restarting the host releases every embedded window back to the desktop, so no agent process is lost.

## Requirements

- Windows 10 / 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run it, or the .NET 10 SDK to build it
- [Node.js](https://nodejs.org/) if you want the status-light hooks (they are small Node scripts)

## Getting started

**From a release:** download `VirtualWindow-<version>-win-x64.zip` from [Releases](../../releases), unzip it, and run `scripts\AgentHost.exe`.

**From source:**

```powershell
git clone https://github.com/sevenshade-lab/virtual-window.git
cd virtual-window
.\Start-Host.cmd          # builds scripts\AgentHost.exe if needed, then starts it
```

`scripts\build.ps1` only rebuilds when the source has changed since the exe was built (it compares content, not timestamps, so switching branches does not trigger a rebuild). When it replaces a running host, it asks that host to hand its windows back first, so rebuilding never kills your agents. `Start-Host.cmd` starts the host through Explorer, so the host and the agents it launches get your normal desktop environment, not the environment variables of the terminal you ran it from.

Once it is running:

1. Click **Adopt** to pull agents that are already open into the panes, or use **+ New** to launch one.
2. Click **Queue** / **Grid** to switch views.
3. To turn on status lights, open **Settings → Reinstall hooks**. This is the same as running `AgentHost.exe --install-hooks`. Then restart the agents so they load the hooks. Codex also asks you to trust new hooks once, on its hooks page.

## The queue view

- **The front card** is the session you are dealing with now. When you reply and it goes back to work, the next waiting session comes to the front. If you are in the middle of typing, the host waits before switching.
- **On the right, one page per waiting session.** Each page shows an edge with an index tab: the agent's icon, its light, and a short line saying what it is waiting for (approve a tool, answer a question, review a plan, or finished). Click a tab or a page edge to turn to that page; **Skip** turns one page forward. Switching plays a short page-turn animation.
- **On the left, tabs for sessions that are still working.** Click one to bring it to the front.
- A session that starts asking you something jumps to the head of the queue.

## What the lights mean

| Light | Meaning |
|---|---|
| Green, breathing slowly | Working |
| Yellow, breathing fast | Asking you something or waiting for your approval |
| Yellow, steady | Finished, and you have not opened that pane since |
| Grey | Not running |
| Hollow grey ring | No data yet |

A few cases are handled so that a long task does not turn yellow halfway:

- **Codex with automatic review.** When a command is approved by Codex's own reviewer and not by you, the permission request does not count as asking you.
- **Claude with background agents.** When Claude ends its turn while sub-agents, workflows or teammates it started are still running, the session still counts as working.
- **Several sessions of one agent in one pane.** Asking beats working, and working beats whichever session moved most recently.

## Supported agents

| Agent | Desktop app | CLI | Status |
|---|---|---|---|
| Claude | ✓ (Store app) | `claude` | hooks |
| Codex | ✓ (Store app) | `codex` | hooks |
| Cursor | ✓ | `cursor-agent` | hooks |
| OpenCode | ✓ | `opencode` | plugin (CPU until loaded) |
| Antigravity | ✓ | – | hooks |
| Hermes, OpenClaw, CodeBuddy, Devin | ✓ | varies | CPU |
| Grok Build, Pi, Oh My Pi, Gemini CLI | – | ✓ | CPU |

You can add any other app under **Settings → Agents → + Add custom agent**, or place any open window into a pane with **Add a window**.

## Keyboard

| Keys | Action |
|---|---|
| `Ctrl+Alt+1` … `6` | Focus pane 1–6 (`Ctrl+Alt+0` un-zooms) |
| `Alt+Q W E` / `Alt+A S D` | Jump to panes 1–6 (laid out like the grid) |
| ``Alt+` `` | Next pane |
| `Alt+X` | Un-zoom |
| `Alt+Space` | Window menu |

Hotkeys are registered only while Virtual Window or one of its panes is in the foreground.

## Notes between agents

Each agent has an inbox under `%LOCALAPPDATA%\AgentHost\notes\<agent>\`, one small JSON file per note, in four folders: `new` (not delivered yet), `open` (shown to one of its sessions, not marked as read), `read`, and `old` (unread for 30 days, no longer mentioned). The file name is the note id, and the id starts with the time the note was left, so listing a folder is enough to order notes. Nothing else is involved: no server, and the host does not need to be running.

**Delivery.** When an agent has unread notes, the hook adds them to the model's context:

| Agent | When | How |
|---|---|---|
| Claude Code, Codex | session start, each user prompt | `hookSpecificOutput.additionalContext` |
| Cursor | session start | `additional_context` (some Cursor builds are reported to ignore it, so these notes are never counted as delivered) |
| Antigravity | before the first model call of a turn | `injectSteps` with a system message |
| OpenCode | each user prompt | a synthetic text part appended by the plugin's `chat.message` hook |
| Agents without hooks | – | not delivered; you only get the count on the pane |

A note is done only when someone marks it as read. Until then every session of that agent is told about it once: a session's first chance lists everything unread (the latest five in full, the rest as a count), and after that only new notes are sent, five at a time. Notes older than seven days are only counted. The text handed to the model says that notes come from other agents and are information, not instructions.

**At the end of a turn.** Notes can arrive while an agent is busy, and nobody may talk to it afterwards. So when it is about to stop and there are notes that have never been delivered, the hook holds it and it handles them before stopping:

| Agent | Event | How it is held |
|---|---|---|
| Claude Code, Codex | `Stop` | `{"decision":"block","reason":…}`; the reason goes to the model |
| Cursor | `stop` | `{"followup_message":…}`, which Cursor submits as the next user message. Written from its hook documentation and not yet confirmed on a desktop build, so these notes are not counted as delivered and the light is left alone |
| Antigravity | `Stop` | `{"decision":"continue","reason":…}`; the reason is injected as a system message |
| OpenCode | session becomes idle | the plugin sends one more message to the session: a single visible line, with the notes in a synthetic part (desktop app only) |

- Only notes that were never delivered cause a hold, and a session claims them first (the file is moved from `new` to `open`, which is atomic), so one note holds at most one session even when two stop at the same moment.
- A session is held at most once per turn, until you talk to it again, so a session cannot loop and two agents cannot keep waking each other.
- When an agent has several sessions open, the one that has recently used notes (left one, read the inbox, marked one as read) is preferred; an unrelated conversation is not held while such a session exists. Notes it does not pick up are still shown at the next prompt, as before.
- Stops that are not a finished turn are left alone: aborted or failed in Cursor, a pause in Antigravity, a stopped or failed turn in OpenCode.
- While it is held the session stays *working*, so the light does not flicker.
- In Cursor and OpenCode the notes necessarily arrive inside a user-role message, which is why the text handed to the model starts by saying that notes are information from other agents and not instructions.

Create `notes\turnend.off` (same format as `deliver.off`) to turn off only this kind of delivery.

Sub-agents, headless one-shot runs (`claude -p`, `codex exec`) and Codex's internal helper threads are skipped. A launcher can decide for a process with the environment variable `AGENTHOST_NOTES`: `off` means never deliver to it, `on` means deliver even though it is headless. (`on` lifts only the headless rule; sub-agents and helper threads it starts are still skipped.) Each decision is logged to `notes\delivery.log`.

**The count on a pane.** Only agents that cannot receive notes through a hook (agents without hooks, your own additions, Cursor) get a small numbered dot on their icon, showing how many notes are still waiting for them: that is how you find out and can tell the agent yourself. Agents whose hooks deliver notes on their own (Claude Code, Codex, OpenCode, Antigravity) never get a dot. Unread notes are always mentioned in the tooltip, together with who left them.

**Command line**, for agents and scripts (installed next to the hook script by *Reinstall hooks*):

```
node "%LOCALAPPDATA%\AgentHost\bin\agenthost-notes.cjs" send <who> [text…] --as <me>     reads stdin when no text is given; --file <path> reads a UTF-8 file
node "…\agenthost-notes.cjs" inbox --as <me> [--all] [--json]
node "…\agenthost-notes.cjs" ack <id>… --as <me>
node "…\agenthost-notes.cjs" peers
```

You can leave notes for the agents placed in panes and for the agents whose hooks are installed. Sending to an unknown name fails and lists the valid ones. Sandboxed agents that cannot write to `%LOCALAPPDATA%` need these commands wrapped as a tool (an MCP server, for example).

To turn delivery off, create the file `notes\deliver.off`: empty turns it off for everyone, or list agent names in it, one per line. To replace the default "what to do with a note" text, put your own in `notes\guide.txt`; `{kind}` and `{cli}` are substituted.

## Configuration

Preferences are saved under `%LOCALAPPDATA%\AgentHost\` and edited in Settings.

The optional `config.json` sits next to the `scripts` folder; copy `config.example.json` to start. It sets default working folders, per agent if you like, and the pane gap and header height.

## For integrators

- `%LOCALAPPDATA%\AgentHost\status.json` holds the current panes and their states, including `notes`, the number of unread notes for that agent.
- `%LOCALAPPDATA%\AgentHost\notes\` holds the notes agents leave for each other; see [Notes between agents](#notes-between-agents).
- `%LOCALAPPDATA%\AgentHost\sessions\*.json` holds one file per agent session, written by the hooks: `{ kind, sessionId, cwd, state, tool, at, reply }`.
- You can post these window messages to the host window, whose title is `虚拟窗口`:

| Message | Effect |
|---|---|
| `0x8051` | Adopt open agents |
| `0x8052` | Collapse: give every window back and exit, without closing the agents |
| `0x8053` | Next layout, or skip in the queue |
| `0x8054` | Toggle grid / queue |
| `0x8055` | Open the agent list |
| `0x8056` | Zoom / restore (queue: the front card; grid: the focused pane) |

- Command line: `--claim`, `--install-hooks`, `--uninstall-hooks` and `--list-agents`.

## How it works, and a few caveats

- Each agent's top-level window is re-parented into a pane (`SetParent` + `WS_CHILD`). Styles, focus and IME activation are repaired continuously, because Electron apps sometimes reset their own window style.
- In the queue view, panes that are not in front are **stacked under the front pane instead of hidden**. Chromium stops rendering a window whose parent is hidden and does not notice when that parent is shown again, so hiding caused blank or frozen panes. Stacking keeps every agent live, so a switch is only a change in z-order. The trade-off: an agent that is streaming output keeps rendering while it is behind.
- The hook installer only adds its own entries, which are marked `agenthost-hook.cjs`. It never touches your other hooks, and it backs up each config file once as `*.agenthost-bak`.
- The source comments and some docs are in Chinese, because this started as a personal tool.

## Credits

The mapping from hook events to states, and the idea of a queue view, come from [Que](https://github.com/ZIXT233/Que) (MIT).

## License

[MIT](LICENSE)

---

## 中文说明

**虚拟窗口**把各家 AI agent 的桌面端（Claude、Codex、Cursor、OpenCode、Antigravity 等）和它们的命令行会话嵌进同一个窗口，有两种视图：
- **网格**：同时看几家，多种排版可选，支持放大和拖动换位。
- **队列**：一次只看一家。在等你回复的会话像书页一样排在右边，每页露一条边和一个索引签；还在干活的是左边的小签。

**主要功能**
- 真嵌入：输入法、剪贴板跟单独打开时一样。
- 状态灯：来自各家官方钩子，也就是 OpenCode 插件、Claude、Codex、Cursor、Antigravity 的钩子。黄灯只有两个意思：在问你，或者做完了你还没看。Codex 自动审查、Claude 把活派给后台 agent、一家开着几个会话这三种情况，长任务跑到一半不会误亮黄灯。
- 队列视图：右边的索引签上有图标、灯和一小句它在等什么（批准哪个工具 / 问你 / 看计划 / 做完了），点签或页边翻到那一页，「跳过」往后翻一页，换人时有翻页动画；你正在打字时不会自动换人。
- 鼠标停在灯或签上，能看到它停下来要你做什么：等你批准哪个工具、问了你问题还是计划等你看；做完的显示它最后那段话的开头和所在文件夹。
- 点 agent 自己弹的通知，会跳到那一格。
- 留言：各家 agent 可以互相留话。钩子在对方下次开工、下次有人跟它说话、或者它正忙的那一轮做完时把没看的留言交给它的模型；钩子送不进去的那几家（比如没有钩子的），还没送到的留言在那一格图标上点一个小数字。细节见上面英文的 Notes between agents 一节。
- 顶上只有一行状态和按钮，每格表头只有图标、名字、灯和两个图标按钮，窗口的地方尽量留给 agent。
- 浅色 / 深色两种外观，谷歌蓝 / 经典橙两套配色，可连同 Windows 应用模式一起切。
- 中文 / English 界面：最小化按钮左边那个按钮一点就切。
- 关闭或重启宿主时，会把窗口都还回桌面，不会带走 agent 进程。

**运行条件**：Windows 10/11 x64，需要 .NET 10 桌面运行时；状态灯钩子需要 Node.js。

**使用**：
- 下载 Release 里的压缩包，解压后运行 `scripts\AgentHost.exe`；或者克隆源码后运行 `Start-Host.cmd`，它会先编译再启动。
- 状态灯在「设置 → 重新安装钩子」里开，装完要重启各家 agent 才生效。

**许可证**：MIT。钩子事件到状态的对照、队列视图的思路参考了 [Que](https://github.com/ZIXT233/Que)（MIT）。
