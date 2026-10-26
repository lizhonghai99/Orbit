# Orbit

常驻 **Runtime** 管理独立助手进程；**Avalonia Shell** 是薄客户端。会话树落在 Runtime，以后换 Web/CLI 壳也走同一事件与复看模型。

**版本**：0.0.1 · License：[MIT](LICENSE) · [English](README.md)

延伸阅读：[架构](docs/architecture-zh.md) · [助手能力](docs/agents-zh.md) · [安全](SECURITY.md) · [变更](CHANGELOG.md) · [贡献](CONTRIBUTING.md)

## 环境要求

| 项 | 说明 |
|----|------|
| OS | **Windows**（Shell / Playwright / Git Bash / DPAPI 已验证；CI 为 `windows-latest`） |
| SDK | .NET **10**（见 [`global.json`](global.json)） |
| Coder | **Git Bash**（`bash.exe`；可用 `ORBIT_BASH_PATH` 覆盖）——**无** PowerShell 回退 |
| Mail | Playwright Chromium（构建后装一次） |

## Visual Studio

1. 打开仓库根目录的 `Orbit.slnx`。
2. **生成 → 生成解决方案**（建议 Release；已开 `TreatWarningsAsErrors`）。
3. 建议多启动项目：右键解决方案 → **设置启动项目** → `Orbit.Runtime` 与 `Orbit.Shell` 均设为「启动」。
4. 或先 F5 跑 `Orbit.Runtime`，再在 Shell 里点「启动 Runtime」（若 5050 已在听则直接刷新）。
5. **只启动一个 Runtime**（同仓库双开会互清 Agent 进程）。Shell 读 `%LOCALAPPDATA%/Orbit/runtime.token`，请求带 `X-Orbit-Token`。

默认地址：`http://127.0.0.1:5050`。

## 结构

| 项目 | 角色 |
|------|------|
| `Orbit.Contracts` | 清单、DTO、鉴权辅助、错误码 |
| `Orbit.Runtime` | 发现 / 启停 / 会话 JSONL / 审批 / SSE |
| `Orbit.Shell` | 薄壳：会话、切模型、设置、能力面板（Skills / Tools / **MCP**） |
| `Orbit.Agents.Hosting` | Agent **基建**：AG-UI、Responses 工厂、Skills、MCP、Harness |
| `Orbit.Agents.Browser` | 共享 Playwright 包（仅 profile；不含领域逻辑） |
| `Orbit.Agents.Mail` | Outlook Web 整理（HITL；永不自动删除） |
| `Orbit.Agents.Research` | BackgroundAgents + Workflow Scope→Outline→Draft + 检查点 |
| `Orbit.Agents.Coder` | FileAccess + FileMemory + 审批 Git Bash；宿主 Workspace 必填 |
| `agents/*.json` | 静态发现（仅顶层；`deferred/` 不加载） |
| `data/` | 本地会话 / profile / MCP / workspace（**不进 git**） |

测试：`Orbit.Contracts.Tests`、`Orbit.Agents.Hosting.Tests`、`Orbit.Agents.Coder.Tests`、`Orbit.Runtime.Tests`、`Orbit.Shell.Tests`（撰写时 **327** 全绿）。

### Skills（每 Agent 自有）

- 目录：`src/Orbit.Agents.{Mail|Research|Coder}/skills/<skill-id>/SKILL.md`
- 启动时注入（`SkillCatalog.BuildInstructionsAppendix`）
- 进程内 `GET /skills`；Runtime `GET /agents/{id}/skills`；Shell 右侧展示
- **加载与执行在 Agent 进程**（同 MCP / 工具）

### 附件

- Shell `+` → 选文件 → `POST /sessions/{id}/attachments`
- Runtime 存 `data/attachments/{agentId}/{sessionId}/…`，写 `meta.attachment_uploaded`
- Agent Running 时再转发到 Agent `POST /attachments`（**尚未**进入模型上下文）

## 当前已实现

- 读 `agents/*.json` 发现助手；Runtime 启停 + `/health`
- **手装 Harness + 每 Agent 白名单**（→ `ORBIT_HARNESS`）。清单为空时默认 `mode,todo,skills,compaction,tool_approval`；`file_access` / `file_memory` / `background` 需显式 opt-in。若漏写 `tool_approval` 但存在 ApprovalRequired 工具，Hosting 会自动开启并打警告日志。
- 平台始终注入 `ask_user_choice` / `ask_user_confirm`（Shell 卡片）
- AG-UI：`POST /sessions/{id}/run`、Stop cancel、审批 resume
- 会话：JSONL append-only + `.meta.json`；删除 / 归档（归档触发 Summary）
- **增量同步 + SSE**：`GET /sessions/{id}?after=` 与 `GET /sessions/{id}/events`——忙时对话走推送，不再 1.2s 全量拉
- **Workspace（宿主级）**：`data/workspaces.json` 单一 active；会话创建快照路径；run 经 `X-Orbit-Workspace` → `OrbitRunContext`；切换前确认中断；路径变化 cancel + 重启 Agent
- Shell：中英 i18n、Workspace chip、对话|**轨迹**、Stop + follow-up、推理开关/强度、History 归档/删除
- 遥测：FunctionInvoker span + `X-Orbit-Run-Id`；`tool_catalog` 每会话一次
- LLM：DeepSeek / Moonshot / Custom → `%LOCALAPPDATA%/Orbit/llm.settings.json`（Key 用 **DPAPI** CurrentUser 加密）。从环境变量导入须显式调用，启动不静默复制。切模型会重载 Agent。
- **本机鉴权（双密钥）**：Shell ↔ Runtime 用落盘的 Shell token（`runtime.token`）；Runtime ↔ Agent 用内存中的 **Agent token**（注入 `ORBIT_RUNTIME_TOKEN`，不落盘，Shell 拿不到）。请求头仍是 `X-Orbit-Token`；`/health` 除外（Agent `/health` 会报 `authRequired`）。
- API 错误：`{ "code", "message", "details?" }`（`OrbitErrorCodes`）
- 日志：Serilog 滚动文件 `%LOCALAPPDATA%/Orbit/logs/`（`runtime-*.log` / `agent-{id}-*.log` / `shell-*.log`，14 天）。可能含会话内容，按敏感数据处理（见 [SECURITY.md](SECURITY.md)）。

### Workspace

| 能力 | 说明 |
|------|------|
| 单一 active | Open / Activate；同路径复用；拒绝盘符根与系统目录 |
| 会话快照 | 创建时固化；之后不可变 |
| per-run 根 | FileAccess / Coder `bash` 读 `OrbitRunContext.Workspace` |
| mismatch | 会话路径 ≠ Agent 启动路径 → `409 workspace_mismatch` |
| 切换重启 | 有 in-flight run 时需 `confirm`；否则 cancel + Restart |
| 符号链接 | Open 会解析 junction/symlink **最终目标** 再做不安全根校验（解析失败则 fail-closed） |

**Coder bash 护栏**：**只认 Git Bash**（`ORBIT_BASH_PATH` 可覆盖）。bash 缺失则直接失败——策略按 bash 语义校验，换成 PowerShell 等于「校验一种语言、跑另一种」。默拒 + 可执行文件白名单；禁嵌套 shell / `curl`/`wget` / `$()`/反引号/`..`/`~`/`$HOME`/`$env:`；绝对路径须在 workspace 内；环境变量白名单（保留真实 `HOME`/`USERPROFILE` 以便 git 读配置）；Windows Job Object 笼住进程树（关闭即杀；默认 ≤32 进程 / 2GB，可用 `ORBIT_BASH_JOB_MAX_PROCESSES` / `ORBIT_BASH_JOB_MEMORY_MB` 调）。

这只是**第一层护栏，不是沙箱**。**0.0.1 产品决定**：只开**可信 Workspace**。白名单里的 `node`/`python`/`npm`/`pip`/`dotnet` 等价于任意代码执行 + 联网。详见 [SECURITY.md](SECURITY.md) / [助手能力 · Coder](docs/agents-zh.md)。

**已知限制**：MCP stdio cwd 仍是进程创建时的 `ORBIT_WORKSPACE`（非 per-run；靠重启刷新）。FileAccess 只读/子目录白名单未做。附件已落盘但尚未进入模型上下文。

### Harness（内置清单）

| Agent | `harness` | 说明 |
|-------|-----------|------|
| Mail | `mode,todo,file_memory,skills,compaction,tool_approval` | 无 FileAccess；笔记与邮箱登记同根 |
| Coder | `mode,todo,file_memory,file_access,skills,compaction,tool_approval` | bash 自有；笔记 ≠ 仓库文件 |
| Research | 上述 + `background` | 完整研究栈 |
| （清单为空时默认） | `mode,todo,skills,compaction,tool_approval` | 磁盘 / Background 仍需 opt-in |

早期单次 inputTokens 样本（2026-10-26，Mail/Coder 打开 `file_memory` **之前**）已过时——若要对照发版数字请打 tag 后重测。领域工具仍会叠加在 Harness schema 之上。

## LLM（OpenAI Responses）

1. Shell → **LLM 设置**
2. 选 DeepSeek 或 Moonshot，确认 Responses Endpoint（误填 `.../anthropic` 会规范化）
3. 填 API Key，**拉取模型列表**，保存 → `%LOCALAPPDATA%/Orbit/llm.settings.json`
4. 加载 Agent 后发自然语言；由模型决定是否调工具

进程环境变量（Runtime 仅为**当前激活** profile 注入）：

| 变量 | 含义 |
|------|------|
| `ORBIT_LLM_PROFILE` | `deepseek` / `moonshot` / `custom` |
| `ORBIT_LLM_MODEL` | 当前模型 id |
| `ORBIT_LLM_PROTOCOL` | `responses` |
| `DEEPSEEK_API_KEY` / `DEEPSEEK_END_POINT` | DeepSeek |
| `MOONSHOT_API_KEY` / `MOONSHOT_END_POINT` | Moonshot（Responses 用 `https://api.moonshot.cn/v1`） |
| `ORBIT_REASONING_ENABLED` | `1`/`0` |
| `ORBIT_REASONING_EFFORT` | `low` / `medium` / `high` / `extrahigh` |

## 邮件 Agent（Outlook Web）

1. 加载 **邮件整理**，新建会话
2. 「打开登录页」→ 在 Chromium 里登录
3. 「进入收件箱」→「整理收件箱」
4. 对 `approval` / `ask_user_*` 批准或拒绝
5. 批准「归档」会打开该邮件并尝试 Outlook **归档**（**不会自动删除**）

浏览器 profile：`data/browser-profiles/{agentId}/{profileId}/`——见 [docs/browser-profiles.md](docs/browser-profiles.md)。

```powershell
pwsh src/Orbit.Agents.Mail/bin/Debug/net10.0/playwright.ps1 install chromium
```

## MCP（常用在 Research）

MCP 在 **Agent 进程启动时**连接（无热插拔）。配置：`data/mcp/{agentId}.json`（不进 git）。示例：`docs/examples/`。

| 字段 | 说明 |
|------|------|
| `transport` | `stdio` 或 `http`（仅 https） |
| `trusted` | **默认 false**；未信任不连接 |
| `enabled` | false → 跳过 |
| `toolPrefix` | `null` → `mcp_{id}_`；`""` → 保留服务端名 |
| `timeoutSeconds` | Create / ListTools / Call 共用（默认 30） |
| `requiresApproval` / `approvalTools` | MCP 变更走 HITL |
| `env` / `headers` | 支持 `${VAR}`；禁止明文 key 进库 |

Shell 能力面板可增删、信任开关；**保存后必须 Restart agent**。审计：`data/audit/mcp.jsonl`。

```json
{
  "servers": [{
    "id": "tavily",
    "transport": "http",
    "url": "https://mcp.tavily.com/mcp/",
    "headers": { "Authorization": "Bearer ${TAVILY_API_KEY}" },
    "trusted": true,
    "enabled": true,
    "toolPrefix": "",
    "timeoutSeconds": 30
  }]
}
```

## Roadmap

**0.0.1 已落地**

- 会话 `?after=` + SSE `/sessions/{id}/events`
- 双密钥本机鉴权；结构化 API 错误码
- Coder：仅 Git Bash + 可信 Workspace 立场
- CI：Release 构建（警告当错误）、测试跑两遍、MTP 覆盖率 artifact、`dotnet format --verify-no-changes`

**后续**

- 附件进入模型上下文；Anthropic Messages
- MCP 热插拔；MCP cwd 跟 per-run workspace
- Workspace 只读/子目录白名单；更深 bash 隔离
- 覆盖率数值门禁（本版仅采集）

## 文档语言约定

| 载体 | 语言 |
|------|------|
| `README.md` / `docs/architecture.md` / `docs/agents.md` | 英文 |
| `README-zh.md` / `docs/architecture-zh.md` / `docs/agents-zh.md` | 中文 |
| C# XML / 行内注释 | **仅英文** |
| Shell UI | `I18n/strings.*.json` |

## CLI 自测

```bash
dotnet restore Orbit.slnx -p:Configuration=Release
dotnet build Orbit.slnx -c Release --no-restore
dotnet test Orbit.slnx -c Release --no-build
dotnet run --project src/Orbit.Runtime
# 另开终端
$token = Get-Content "$env:LOCALAPPDATA/Orbit/runtime.token"
curl -H "X-Orbit-Token: $token" http://127.0.0.1:5050/agents
```
