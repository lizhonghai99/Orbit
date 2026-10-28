# Orbit Agents 能力说明

[English](agents.md) · 版本 **0.0.1**

本文按业界常见的 Agent 产品文档维度，说明当前内置助手各自「能做什么 / 挂了什么 / 不能做什么」。  
实现上优先使用框架 Harness 积木（Skills、压缩、记忆、工具审批等），Orbit 自建的是多进程宿主与会话树，不是另造一套 Agent 运行时。

相关：[架构](architecture-zh.md) · [Harness / HITL spike](spike-p0-hitl-history.md)

---

## 能力维度（读表用）

| 维度 | 含义 |
|------|------|
| **定位** | 解决什么用户问题 |
| **协议** | 与 Runtime 的对话通道（本版均为 AG-UI） |
| **Workspace** | 是否依赖宿主工作区文件夹 |
| **Mode / Todo** | 规划模式与待办清单（Harness） |
| **Skills** | 渐进披露的 `SKILL.md` 手册 |
| **记忆** | FileMemory（会话工作笔记）等 |
| **文件** | FileAccess（持久共享盘 / 工作区文件） |
| **压缩** | ContextWindow 上下文压缩（全 Agent 默认开启） |
| **工具审批** | HITL：突变工具需用户批准（全 Agent 默认开启） |
| **子 Agent** | BackgroundAgents 并发委派 |
| **编排** | Workflow 多步图 / 检查点 |
| **MCP** | 进程启动时挂载的外部工具 |
| **领域工具** | 该 Agent 自有工具（非 Harness 通用积木） |
| **安全边界** | 明确禁止或必须人工确认的行为 |

**平台始终注入（不在 `harness`）**：`ask_user_*`。

清单里的 `harness` 写入 `ORBIT_HARNESS`；按能力需要勾选，含 `compaction` / `tool_approval`。清单省略或 `[]` → 默认 `mode,todo,skills,compaction,tool_approval`（磁盘 / Background 仍需显式 opt-in）。**注意：** 代码里 `CreateAgent(harness: new HashSet())` 空集合表示「全关」，与清单 `[]` 语义不同。

---

## 总览对照

| 维度 | 邮件助手 `mail` | 研究助手 `research` | 编程助手 `coder` |
|------|-----------------|---------------------|------------------|
| 定位 | Outlook 网页收件箱整理 | 多角度调研 + 结构化简报 | 工作区内读改代码 / 构建测试 |
| Workspace | `none` | `optional` | **`required`** |
| Mode / Todo | ✅ | ✅ | ✅ |
| Skills | ✅ `outlook-triage` | ✅ `concurrent-research` | ✅ `minimal-diff` / `long-running` |
| FileMemory | ✅ | ✅ | ✅ |
| FileAccess | ❌ | ✅ | ✅（跟会话工作区） |
| Compaction | ✅ | ✅ | ✅ `coder_generator`；planner/evaluator 不含 |
| ToolApproval | ✅ | ✅ | ✅ |
| BackgroundAgents | ❌ | ✅ `AngleResearcher` | ❌（用 Handoffs） |
| Workflow / harness | ❌ | ✅ Scope→Outline→Draft | ✅ Short/Long-Running + FeatureList |
| MCP | 可配（`data/mcp/mail.json`） | 可配（常用搜索类） | 可配 |
| 登录 | Outlook Web（浏览器 profile） | 不需要 | 不需要 |
| 典型 HITL | 建夹 / 移动 / 归档 | 写文件、MCP 突变、`ask_user_*` | 写/删文件、`bash` |

---

## 邮件助手（Mail）

**清单**：`agents/mail.agent.json` · 项目：`Orbit.Agents.Mail`

### 定位

在 Outlook 网页上帮用户看懂收件箱、给出整理建议，并在用户批准后执行建文件夹 / 移动 / 归档。**永不自动删除邮件。**

### Harness 剖面

| 模块 | 状态 |
|------|------|
| Mode / Todo | 开 — 多步整理时先 plan，再 execute |
| Skills | 开 — `outlook-triage` |
| FileMemory | 开 — `data/file-memory/mail/`（与 `MailboxRegistry` 同根） |
| Compaction / ToolApproval | 开 |
| FileAccess / Background | 关 |

### 领域能力

- 浏览器：登录墙检测、收件箱导航、多邮箱 profile（`MailboxRegistry`）
- 只读：列出邮件、整理建议（markdown）
- 突变（各需工具审批）：`create_folder` / `move_mail` / `archive_mail`
- 人机问答：`ask_user_choice` / `ask_user_confirm`（平台注入）

### Skill

| Id | 作用 |
|----|------|
| `outlook-triage` | 登录 → 列表 → 建议 → HITL 突变的标准流程 |

### 记忆与上下文

- **FileMemory**：跨轮整理规则 / 文件夹计划；邮箱登记也落在同一根目录
- **无** FileAccess：不读写宿主工作区文件
- 会话历史走 AgentSession + ContextWindow 压缩
- 浏览器状态在本机 profile 目录：`data/browser-profiles/mail/{profileId}/`

### 安全边界

- 禁止删除；突变必须走工具审批卡
- 同时只推进一张待批卡
- Runtime 不解析邮件业务 DTO，不代执行 `/actions/apply`
- **导航闸门**：Playwright `Goto` 与 Mail 打开 href 走 `NavigationAllowlist` + `MailBrowserPolicy.AllowedHosts`（空名单 fail-closed）。**未覆盖**：`window.open`/弹窗目标、站点中途 HTTP 重定向——在「有人坐在 headed 窗口前」威胁模型下可接受；不要把 allowlist 当成完整浏览器沙箱。

### 局限

- 依赖 Outlook Web DOM（en-US 区域启发式 + 主题中文启发式）
- Playwright 安装与首次登录需人工完成

---

## 研究助手（Research）

**清单**：`agents/research.agent.json` · 项目：`Orbit.Agents.Research`

### 定位

两类研究路径（可按意图单用或串联）：

1. **BackgroundAgents**：并发委派 `AngleResearcher` 做多角度事实搜集  
2. **Workflow**：`run_research_pipeline` 走 Scope → Outline → Draft，带磁盘检查点  

### Harness 剖面

| 模块 | 状态 |
|------|------|
| Mode / Todo | 开 |
| Skills | 开 — `concurrent-research` |
| FileMemory | 开 — `data/file-memory/research/` |
| FileAccess | 开 — 导出/落盘（写需审批） |
| Background | 开 — 子 Agent `AngleResearcher` |
| Compaction / ToolApproval | 开（清单显式） |

### 领域能力

- `run_research_pipeline(topic, focus?, evidence?, resume?)`
- BackgroundAgents 协议工具：`start_task` / `wait_for_first_completion` / `get_task_results` / …
- 父 Agent 与子 Agent 均可挂 MCP（如搜索）；子 Agent 无完整 Harness，由父编排

### Skill

| Id | 作用 |
|----|------|
| `concurrent-research` | 并发 BackgroundAgents 纪律 + 何时调用 Workflow |

### 记忆与上下文

- **FileMemory**：跨轮研究笔记（会话工作记忆）
- **FileAccess**：可导出材料；写/删需审批
- **Checkpoint**：Workflow 图状态落在 Agent 侧检查点目录（可 `resume=true`）
- Compaction：长调研上下文自动压缩

### 安全边界

- 禁止捏造引用；搜索失败应如实说明
- 写文件与需批 MCP 工具走 HITL
- 子任务应并行启动，禁止串行 start→wait→start

### 局限

- MCP 在**进程启动**时加载，改配置后需重启 Agent
- MCP stdio 的 cwd 跟进程创建时的 `ORBIT_WORKSPACE`，不是每轮 `OrbitRunContext`（切换工作区后需重启 Agent）
- Workspace 为 `optional`：无工作区时仍可聊；有工作区时 FileAccess 跟会话根

---

## 编程助手（Coder）

**清单**：`agents/coder.agent.json` · 项目：`Orbit.Agents.Coder`

### 定位

对外一个 Coder；对内两条路由 + 四个 handoff **id**（id 固定，路由名统一用英文）：

| 路由 | 链路 |
|------|------|
| **Short-Running** | `coder_router` → `coder_generator` → `coder_evaluator`（HITL handoff） |
| **Long-Running** | `coder_router` → `coder_planner`；每批 **`run_coding_turn`**（context reset） |

| Id | 职责 |
|----|------|
| `coder_router` | 选 Short-Running / Long-Running |
| `coder_planner` | 仅 Long-Running：`run_feature_list`（resume/reset）+ **`run_coding_turn`**（入口审批） |
| `coder_generator` | Short-Running 实现（HITL）；Long-Running 在 coding turn 内嵌套、**新 session** |
| `coder_evaluator` | Short-Running QA（HITL）；Long-Running 在 coding turn 内嵌套、**新 session** |

### Harness 剖面

| 参与者 | 模块 |
|--------|------|
| `coder_generator` | mode / todo / skills / file_memory / file_access / compaction / tool_approval |
| `coder_planner` / `coder_evaluator` / `coder_router` | tool_approval（router 另有 ask_user） |

### 领域能力

- **coder_generator**：FileAccess、`bash`、`get_feature_list` / `set_feature_status`  
- **coder_evaluator**：`bash`、`get_feature_list` / `set_feature_status`（done/failed + VERDICT）  
- **coder_planner**：`run_feature_list`、`get_feature_list` / `set_feature_status`  
- **coder_router**：handoff + `ask_user_*`

### 路径细节

1. Router → Short-Running 的 generator，或 Long-Running 的 planner  
2. Long-Running：`run_feature_list` → **`run_coding_turn`**（Shell 审批卡 → claim → **全新** G/E session；15 分钟超时；种子只有 FeatureList）  
3. 已有清单：`resume=true` 下一批，或 `reset=true`（归档后重规划）；`Attempt` / 卡住的 `in_progress` / 每 feature 的 coding-turn（`MaxCodingTurns`）有上限  
4. Short-Running：generator → evaluator；FAIL→generator **最多约 2 次**，再请用户确认；PASS → router；会话消息软上限终止  

### Skill

| Id | 作用 |
|----|------|
| `minimal-diff` | 先读后写、优先最小替换、改完总结 |
| `long-running` | Long-Running + `run_coding_turn` context reset |

### 记忆与上下文

- **FeatureList**：Long-Running 跨 turn 唯一真相，`data/feature-lists/coder/`  
- **Context reset**：`run_coding_turn` 内 `CreateSessionAsync`；外层 AG-UI 聊天**不是** G/E 的上下文  
- **Compaction**：按**参与者** harness 挂到 ChatClient（generator 开；planner/evaluator 不开）  
- Long-Running：`run_coding_turn` **入口**要审批；turn 内 bash/写文件在可信 Workspace 自主跑；Evaluator **只读** FileAccess
- Short-Running FailGate 是**提示级**（按 AG-UI `RunId`）；handoff 图另有约 80 条消息的外层上限
- **中断/恢复（用框架，不另造）**：
  - Coder AG-UI/handoff：持久化 `AgentSession`（内嵌 MAF workflow checkpoint）；审批用 `CreateResponse` 续跑
  - Research 管线：显式 `CheckpointManager` + `ResumeStreamingAsync`（没有 FeatureList 这种产品落盘）
  - FeatureList：续跑靠磁盘 `feature-list.json`（`resume=true`），不叠一层写了却从不 `ResumeStreamingAsync` 的 checkpoint
  - 契约测：`HandoffApprovalResumeTests`  
- Short-Running：写文件/`bash` 仍 HITL

### 安全边界

- `bash`：可执行文件白名单；禁嵌套 shell、`curl`/`wget`、`$()` / `$env:` 等；绝对路径须落在工作区内
- Windows Job Object 限制进程树与内存；**不是**完整沙箱
- **产品决定（0.0.1）**：白名单含 `node` / `python` / `npm` / `pip` / `dotnet` ⇒ **等价于当前用户在工作区内任意代码执行**。本版**不**上 AppContainer；对策是 **只打开可信 Workspace**，并预期构建需要主机网络。若要收紧：从白名单砍掉解释器，或后续做真正沙箱——二者择一，见 `SECURITY.md`
- 写文件与 `bash` 均需用户审批

### 局限

- 必须先在 Shell 打开工作区
- 缺 Git Bash 时 `bash` 直接失败（fail closed）
- 工作区切换若有进行中的 run，需确认后 cancel + 重启 Agent

---

## 横切：压缩、记忆、Skills、审批

### 压缩（Compaction）

- 实现：`ContextWindowCompactionStrategy`（管道内 `CompactionProvider` + 历史 `AsChatReducer`）
- 默认窗口与 `OrbitLlmContextWindow` / 环境变量对齐
- **清单 `harness` 含 `compaction` 时挂载**（Agent 管道 + Runtime 会话 `AsChatReducer`）
- 压缩只跟 harness 走（Short-Running 的 `coder_generator`）；Long-Running 靠 `run_coding_turn` context reset + FeatureList

### 记忆

| 种类 | 谁有 | 存哪 | 用途 |
|------|------|------|------|
| Chat / AgentSession | 全部 | Runtime `.agent.json` + 会话 JSONL | 多轮对话续跑 |
| FileMemory | 全部 | `data/file-memory/{agentId}/` | 抗 compaction 工作笔记 |
| FileAccess | Research / Coder | 工作区或共享盘 | 持久文件 |
| 浏览器 Profile | Mail | `data/browser-profiles/...` | Cookie / 登录态 |

### Skills

- 路径：`src/Orbit.Agents.{Id}/skills/<skill-id>/SKILL.md`
- 启动注入目录摘要；正文经 `load_skill` 渐进披露
- 只读 skill 工具可由自动批准规则放行；脚本类仍可能要人批

### 工具审批（HITL）

- 框架：`ApprovalRequiredAIFunction` / `UseToolApproval`
- Runtime 只 resume 工具中断（`source: tool`），不执行领域副作用
- Shell：待批卡片 + 轨迹中的审批事件

### 轨迹 vs 日志

- **轨迹（会话 JSONL）**：user / assistant / tool_call / tool_result 等**全量落盘**（便于追溯）；Shell 列表里的短摘要只是 UI 投影裁切
- **日志**：`%LOCALAPPDATA%/Orbit/logs/`（`runtime-*.log` / `agent-*.log` / `shell-*.log`，Serilog）按 Information+ **全量写文件**（按天滚、留 14 天）
- **Compaction**：只影响发给模型的上下文，由 MAF 策略自动处理；Orbit 不再对工具结果 / bash 输出做业务侧截断

---

## 清单与扩展

新增助手时：

1. `agents/{id}.agent.json`（含 `harness` / `workspace`）
2. `src/Orbit.Agents.{Name}/` + 可选 `skills/`
3. 能力说明补一节（本文件 + 英文版）
4. 不要把领域 DTO 泄漏进 Runtime；突变走工具审批
