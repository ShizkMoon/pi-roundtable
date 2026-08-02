# Agent 客户端实践与 Pi Roundtable 里程碑差距审计

- 日期：2026-08-02
- 范围：Pi、PromptX、Multica、Proma、Cherry Studio、ReasoniX；Windows 本地优先的 Pi Roundtable
- 决策边界：只保留 Pi 运行时；Oh My Pi 仅作为历史上的进程监督、流式协议和取消语义参考，不兼容其协议、会话或命令

## 结论

Pi Roundtable 已经越过“静态 UI 原型”阶段：Windows 客户端、C++ 会议内核、Pi Runtime Host、规范化事件、公开/私聊、暂停恢复、工具审批、受限 SubAgent、加密持久化和自包含 x64 打包均有实现。2026-08-02 的真实 DeepSeek 验收还证明了三角色、两轮、每轮每角色恰好一次完成输出的本地全链路。

当前最重要的工作不再是继续扩大参考项目兼容面，而是让现有闭环具备可恢复、可审计、可长期使用的质量。优先级依次是：

1. 让命令幂等、工具授权和恢复场景经得住进程重启，而不只在单进程内成立；
2. 用真实 Windows 场景覆盖取消、交接、审批、失败重试和恢复，而不只验证正常生成；
3. 控制长记录的性能和信息密度，并补全无障碍、缩放和多宽度视觉矩阵；
4. 在本地 alpha 稳定后，再建设认证、私有 audience、持久数据库和 E2EE 的远程平面。

## 证据口径

本审计使用以下状态词：

- **implemented**：仓库内存在可执行实现和相应测试面；
- **scaffolded**：已有接口、页面或开发服务器，但未完成生产闭环；
- **planned**：文档或设计中存在，尚无可验收实现；
- **pending**：已确认需要做，但当前里程碑没有完成；
- **verified**：在本机实际构建、运行或真实服务调用中通过。

外部项目的官方文档和官方仓库用于提取做法，不把宣传描述等同于可靠性证明。没有可复现测试的数据，均只视为设计线索。

## 参考项目迁移矩阵

| 参考面 | 可迁移做法 | 在 Pi Roundtable 中的落点 | 明确不复制的内容 |
| --- | --- | --- | --- |
| Pi | 版本化 Session、压缩/分支摘要、扩展边界、窄 Provider/Model 层 | Pi SDK 只留在 `packages/runtime-host`；客户端只看规范化事件；每角色独立 Pi session | 原始 Pi session、provider 内部事件、隐藏推理和 SDK 类型不进入公共协议 |
| PromptX | 角色身份/原则/知识/记忆分层；工作记忆与长期记忆分离；工具按作用域授权 | 冻结参与者清单、版本化 System Prompt、后续可审计 recall/remember 事件、角色/会议/路径级 capability grant | 不复制 DPML 为跨平台协议；不把自然语言生成的角色或记忆直接视为已批准事实 |
| Multica | 可复用 Agent 配置与一次 Task/Run 分离；Workspace 与 Squad；领导者介导委派 | 长期 Role Profile 与 Session Participant Manifest 分离；父角色独占 SubAgent 结果 | 不把 Squad 数据结构直接变成会议协议；不让子代理成为第二套会议编排器 |
| Proma | Workspace 范围的 Session、运行时选择、事件总线与窗口间状态传播 | Session-first Windows 信息架构；Runtime Host 进程边界；规范化事件驱动 UI | 不做 Proma Runtime 兼容；公开仓库与产品默认行为可能漂移，不能据此承诺一致体验 |
| Cherry Studio | Provider/Model 注册表；Agent 附件；Session Runtime；流管理；工具注册与远程抓取安全边界 | 工作区 Provider/Model/Skill/MCP 目录；逐角色冻结附件；凭据引用；有界流式呈现 | 不照搬 Electron 内部类型；“可重试”必须由 Pi 能力和本产品事件语义共同证明 |
| ReasoniX | append-only/checkpoint 思路；并行安全屏障；工具调用修复和 anti-storm；计划/权限/编辑的人类门禁 | 事件日志、恢复检查点、并发 SubAgent 上限、工具审批、预算/循环上限候选 | ReasoniX 以本地编码 Agent 为主，不是多角色圆桌；其 SubAgent 不是会议协调原语 |

## 当前里程碑状态

### Implemented

- 公共协议：版本化 Schema、公开/私有可见性、`runtimeGeneration`、工作区/会话/冻结参与者清单。
- C++20 Core：权威 Runtime Owner、全局顺序、租约代次、角色生命周期、中断与交接、窄 C ABI。
- Pi Runtime Host：每角色独立 Session、逐角色模型/System Prompt/Skill/MCP、取消、失败归一化、工具审批、最多两个且不可递归的隔离 SubAgent。
- Windows：Session-first WinUI 3 壳、会话/角色/Skill/MCP/设置、公开记录/私聊、Windows Credential Manager、DPAPI + SQLite 事件存储、重放、暂停/恢复、本地 Runtime 监督。
- `@` 角色边界：公开消息仍对所有参与者可见，但只有被 `@` 的目标进入顺序发言队列；每个目标收到含自身 `roleId`/显示名的独占提示，禁止代写其他被提及角色的回答。
- 原生 Markdown：标题、段落、粗体、斜体、列表、引用、代码块、分隔线、安全链接和行内数学标记均在 WinUI 原生控件中呈现。
- 长记录跟随：默认合并跟随最新流式内容；用户离开底部后不抢滚动并显示“跳到最新”；发送、重试或切换上下文时恢复跟随。
- Windows x64：自包含发布、内置 Node/Pi Runtime Host/C++ Core、WiX 4 MSI 构建管线。

### Verified

- Runtime Host 定向测试：42/42。
- Windows 测试：22/22，包含安全 Markdown、LaTeX 源码回退和记录跟随阈值。
- 真实 DeepSeek/Pi：`deepseek-v4-flash`，三角色、两轮、每轮各角色恰好一个完成输出，共 6 个非空输出；证据只保存角色、字符数、SHA-256、事件归属和截图，不保存 Key。
- 最新本机证据：`out/e2e/deepseek-20260802-012507-1a5c4e66/evidence.json`；临时 Windows 凭据在退出后已删除。

### Scaffolded

- 同步服务：租约、内存事件日志、游标回放、HTTP/SSE 开发面；无认证，拒绝不可信私有事件。
- Android：Compose Material 3 自适应 UI 脚手架；未接入生产同步和私有 audience。

### Pending

- 进程重启后仍有效的 durable command receipt / 幂等去重。
- MCP 精确到 tool 的可视化 allowlist 编辑和变更事件；当前批准并附加的 server 在 allowlist 为空时会暴露其全部发现工具。
- 真实取消、打断交接、工具批准/拒绝、provider 失败/重试、暂停恢复和崩溃恢复 E2E。
- 真正的数学排版引擎。当前只把 LaTeX 作为安全、可复制的源码样式呈现，不宣称已经排版公式。
- 超长 transcript 的分段虚拟化/折叠、代码块横向滚动与复制操作、审批焦点/过期状态、完整无障碍矩阵。
- 认证远程同步、服务端持久数据库、私有 audience 授权、E2EE、限流和分布式租约。
- 代码签名、真实安装/卸载/修复/升级矩阵、ARM64；Android 真实同步。

## 本轮实施细节

### `@` 只让目标角色回答自己

路由层本来已经能够把多个 mention 解析成目标角色并顺序排队，缺陷在于所有角色收到同一个泛化提示。修复只包裹交给单个 Pi adapter 的 prompt，不修改公共用户消息和规范化事件：

- 明确当前唯一答复身份的显示名与 `roleId`；
- 要求只从本角色职责和视角作答；
- 禁止为其他被 mention 角色拟稿、模拟、总结成替代答复或创建其答复分栏；
- 仍允许引用公开记录并与其他角色观点发生分歧；
- 私聊仍只进入指定角色的隔离上下文。

这一区分保留了圆桌的公共可见性，也避免把“谁能看到消息”错误地等同于“谁应当代答”。

### Markdown 与 LaTeX 边界

当前选择 Markdig 解析受限 Markdown AST，再映射为 WinUI 原生控件，而不是给每条消息嵌一个 WebView：

- 禁用原始 HTML；
- 只有 `http`/`https` URL 生成可点击链接；`javascript:`、`file:` 等只作为普通文本；
- 图片暂不发起远程请求，显示隐藏提示；
- 流式文本以约 80 ms 合并重绘，减少逐 token 重建控件；
- LaTeX 代码以 `Cambria Math` 和“公式 · LaTeX 源码”标签显示，可选择、可复制；
- 完整 KaTeX/WebView2 或原生公式排版只有在隔离、缓存、可访问性和离线资源策略明确后再引入。

### 信息密度与直觉交互

记录区保持一条消息一个安静卡片，角色、时间、正文和状态形成稳定层级。长消息不再显示 Markdown 语法噪声；输入区保持固定可用。自动跟随采用公开/私聊独立状态，并对流式更新做合并：

- 正在阅读底部时随最新内容推进；
- 用户上滚后不抢视口；
- 明确按钮恢复到最新；
- 历史会话载入和 Markdown 延迟布局使用短暂稳定窗口，避免滚动到最后一条的顶部而不是末尾。

## 后续详细执行计划

### P0：可靠本地 alpha

1. **Durable command receipt**
   - receipt 写入受保护的本地事件/命令存储；
   - 重启后重复 command ID 返回先前结果或明确终态，不再次触发模型/工具副作用；
   - 验收：在模型请求前、响应中、事件落盘前后四个切点强杀进程，恢复后副作用次数始终为 0 或 1。
2. **精确工具授权**
   - Workspace catalog 维护 server；Role/Participant Manifest 保存明确 tool allowlist；
   - 空列表的产品语义从“全部工具”改为显式选择或清楚的全量授权确认；
   - 验收：未列出的工具不进入 Pi 工具表，批准事件也不能越权扩大范围。
3. **故障 E2E 套件**
   - 真实/可控 provider 覆盖取消、打断交接、超时、失败重试；
   - mock MCP 覆盖批准、拒绝、首次记忆批准和副作用计数；
   - 暂停/恢复与崩溃恢复核验 sequence、generation、私有 audience 和重复输出。
4. **可发布门禁**
   - 每次 x64 包运行 C++、TypeScript、Windows 测试；
   - 管理提取和提取目录启动；
   - 凭据内容扫描必须为零；MSI 记录 SHA-256。

### P1：长期桌面使用质量

1. transcript 分段虚拟化或折叠，验证 1,000 条消息、长代码块和持续流式输入下的内存/帧率；
2. 代码块复制、外部链接确认、公式源码复制；若引入真公式排版，必须离线、无脚本注入、可缓存并提供纯文本可访问替代；
3. 宽度 720/900/1280/1520、100%/200% 缩放、浅色/深色/高对比、键盘和屏幕阅读器截图/自动化矩阵；
4. 工具审批加入到期状态、焦点恢复和来源说明；失败卡片提供就地重试且不重复已完成角色；
5. 可导出的规范化 Markdown/JSON 会话包和非破坏性导入预检。

### P1：受控远程平面

1. 身份认证和设备/用户 audience；
2. PostgreSQL 等持久事件存储和可验证游标；
3. 私有事件授权过滤、密钥轮换、E2EE 设计与迁移；
4. 远程只转发/持久化规范化事件，默认不执行模型。

### P2：分发与第二平台

1. 代码签名、SmartScreen/安装/卸载/升级/修复矩阵；
2. ARM64 打包；
3. Android 作为 UI-only 客户端接入认证同步、断线游标恢复和前台流；
4. 在 Windows 本地 alpha 的 P0 验收完成前，不并行扩展新的运行时兼容面。

## 验收矩阵

| 场景 | 必须观察到 | 禁止出现 |
| --- | --- | --- |
| `@A @B` 公开消息 | A、B 各自一次回答；回答可互相引用；事件公开 | A 替 B 输出完整答复；未 mention 角色抢占本轮 |
| 单个 `@A` | 只有 A 进入答复队列 | 所有角色一起生成 |
| Markdown 流式 | 标题/列表/强调随流式稳定成形；输入可操作 | 原始 HTML 执行；每 token 明显抖动 |
| LaTeX | 公式源码清晰、可选择、可复制 | 把源码回退宣称为完整公式排版 |
| 长记录 | 默认跟随末尾；上滚后显示“跳到最新”且不抢动 | 每轮完成后回到记录顶部 |
| 失败重试 | 只重试目标失败角色，保留先前输出 | 重跑所有已完成角色或重复副作用 |
| 暂停恢复 | 新 `runtimeGeneration`、旧实例写入被拒绝、事件顺序连续 | 假装恢复 Pi 私有 token/session 状态 |

## 一手参考来源

- Pi：[Compaction](https://pi.dev/docs/latest/compaction)、[Session format](https://pi.dev/docs/latest/session-format)、[Extensions](https://pi.dev/docs/latest/extensions)、[官方仓库](https://github.com/earendil-works/pi)
- PromptX：[Roles](https://promptx.deepractice.ai/docs/roles)、[Memory](https://promptx.deepractice.ai/docs/memory)、[ToolX](https://promptx.deepractice.ai/docs/toolx)、[ToolSandbox](https://github.com/Deepractice/PromptX/blob/main/docs/toolsandbox.md)
- Multica：[Agents](https://multica.ai/docs/agents)、[Squads](https://multica.ai/docs/squads)、[Tasks](https://multica.ai/docs/tasks)、[Workspaces](https://multica.ai/docs/workspaces)、[Providers](https://multica.ai/docs/providers)
- Proma：[官方仓库](https://github.com/proma-ai/Proma)、[README](https://github.com/proma-ai/Proma/blob/main/README.en.md)、[Agent session manager](https://github.com/proma-ai/Proma/blob/main/apps/electron/src/main/lib/agent-session-manager.ts)、[Agent event bus](https://github.com/proma-ai/Proma/blob/main/apps/electron/src/main/lib/agent-event-bus.ts)
- Cherry Studio：[官方仓库](https://github.com/CherryHQ/cherry-studio)、[Provider registry](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/provider-model/provider-registry.md)、[Agent loop](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/agent-loop.md)、[Session runtime](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/agent-session-runtime.md)、[Tool registry](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/tool-registry.md)、[Security](https://github.com/CherryHQ/cherry-studio/blob/main/SECURITY.md)
- ReasoniX：[官方站点](https://reasonix.io/)、[主仓库](https://github.com/esengine/DeepSeek-Reasonix)、[Architecture](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/ARCHITECTURE.md)、[ACP](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/ACP.md)、[Session memory](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/SESSION_MEMORY_RETRIEVAL.md)、[SubAgent profiles](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/SUBAGENT_PROFILES.md)

外部产品和文档会继续变化；迁移判断基于 2026-08-01 至 2026-08-02 的官方公开面，采纳前仍需在本产品真实任务中 A/B 和回归。
