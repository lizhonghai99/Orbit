# Spike：P0 审批 / 历史 / Harness

对照总复习：`D:\LearnCode\MAF\GeneralReview`（尤其 `10Harness.md`、`04Agent.md`）。  
各助手能力剖面见 [agents-zh.md](agents-zh.md)。

## Orbit 已用 vs 未用（Harness 七积木）

| 积木 | 总复习 | Orbit 现状 |
|------|--------|------------|
| Skills | ✅ | ✅ `AgentSkillsProvider` |
| Compaction | ✅ | ✅ `ContextWindowCompactionStrategy` → 管道 `CompactionProvider` + 历史 `AsChatReducer` |
| ChatHistory / Session 持久 | ✅ | ✅ Agent 内 Provider；Runtime `.agent.json`（AG-UI Client） |
| `UsePerServiceCallChatHistoryPersistence` | ✅ | ✅ 已挂（配合 `UseProvidedChatClientAsIs`） |
| `ApprovalRequiredAIFunction` | ✅ | ✅ Mail / Coder 突变与 `bash` 等 |
| `UseToolApproval` | ✅ | ✅ + `ReadOnlyToolsAutoApprovalRule`（只读 skill / FileAccess 读） |
| `AgentModeProvider` | ✅ | ✅ Standard 手拼 |
| `TodoProvider` | ✅ | ✅ Standard 手拼 |
| `FileMemoryProvider` | ✅ | ✅ Mail / Research / Coder：`data/file-memory/{id}` |
| `FileAccessProvider` | ✅ | ✅ Research / Coder（Coder 跟会话 Workspace） |
| `BackgroundAgentsProvider` | ✅ | ✅ Research：`AngleResearcher` 并发委派 |
| Workflow | ✅ | ✅ Research：`run_research_pipeline` Scope→Outline→Draft + 检查点 |
| `AsHarnessAgent` 全家桶 | ✅ | ❌ **刻意不用**；手拼 Standard，看得见每一块 + 每 Agent 白名单 |
| 结构化输出 / DevUI / MCP Hosted | 其它章 | ❌ 未接（MCP 用 `ModelContextProtocol` 自挂） |

原则：**Harness = 护栏环境，不是新运行时。** 框架有积木就接积木；Orbit 自建多进程宿主、会话 JSONL、Workspace、本机日志。

## P0-1 HITL

状态：**工具审批唯一通道**（邮件 DTO / apply 已删）。

- Mail：`create_folder` / `move_mail` / `archive_mail` → `ApprovalRequiredAIFunction`
- Coder：`bash` + FileAccess 写 → 审批；FileAccess 读可自动过
- Skills：`load_skill` 等只读 → 自动过；脚本类仍可能人批
- Runtime：`ToolApprovalRequestContent` → `source: tool` → `ResumeApprovalAsync`
- Runtime **不解析**领域 payload；旧 JSONL 非工具审批卡仅记录关闭

## P0-2 历史

状态：**已落地**。ChatHistory / `.agent.json`；Shell 轨迹投影会话事件（含 `tool_invocation`）。

## 生命周期（已落地）

- Stopping：拒新 run → drain → `run_interrupted` → Kill
- Agent 意外退出 / Runtime 启动：补 `meta.run_interrupted`
- Shell Stop / follow-up 排队：见架构文档
- 遥测：FunctionInvoker → Runtime `meta.tool_invocation` → 轨迹合并

## Compaction / FileAccess / Shell Harness 面板

与 [agents-zh.md](agents-zh.md)「横切」及 [architecture-zh.md](architecture-zh.md) 一致；此处不重复展开。

## 下一刀候选

1. MCP 热插拔 / 与 per-run Workspace 对齐 cwd  
2. FileAccess 只读子目录白名单  
3. OTLP 导出（本机已有 Serilog 文件日志）
