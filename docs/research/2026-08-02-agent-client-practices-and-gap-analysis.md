# Agent 客户端实践与 Pi Roundtable 里程碑差距审计

- 日期：2026-08-02
- 范围：Pi、PromptX、Multica、Proma、Cherry Studio、ReasoniX；Windows 本地优先的 Pi Roundtable
- 决策边界：只保留 Pi 运行时；Oh My Pi 仅作为历史上的进程监督、流式协议和取消语义参考，不兼容其协议、会话或命令

## 结论

Pi Roundtable 已经越过“静态 UI 原型”阶段：Windows 客户端、C++ 会议内核、Pi Runtime Host、规范化事件、公开/私聊、暂停恢复、工具审批、受限 SubAgent、加密持久化、自包含 x64 打包和签名清单更新链路均有实现。仓库提供不回显凭据的真实 DeepSeek/Pi 本机验收脚本，用于核对严格单/多点名、Markdown/LaTeX 源码回退和自由讨论自主申请；每次结果只保存在被忽略的本机 `out/`，不能把一次临时运行当成仓库内永久证明。

本轮 P0/P1 已把本地闭环推进到可恢复、可审计、可长期试用的状态。后续优先级依次是：

1. 完成 0.1→0.2 的真实安装更新、GitHub 发布和安装生命周期验收；
2. 在不放宽安全边界的前提下决定是否引入真正的离线数学排版；
3. 把已经实现的设备鉴权、私有 audience 和 PostgreSQL 后端接到 Windows/Android，并补 TLS、限流、E2EE 与多副本通知；
4. 在 Windows 本地质量门稳定前，不扩大运行时兼容面；Oh My Pi 继续只作历史架构参考。

## 证据口径

本审计使用以下状态词：

- **implemented**：仓库内存在可执行实现和相应测试面；
- **scaffolded**：已有接口、页面或开发服务器，但未完成生产闭环；
- **planned**：文档或设计中存在，尚无可验收实现；
- **pending**：已确认需要做，但当前里程碑没有完成；
- **verified**：在本机实际构建或运行中通过；易漂移的真实服务结果必须注明验收时间和证据边界。

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
- Pi Runtime Host：每角色独立 Session、逐角色模型/System Prompt/Skill/MCP、取消、失败归一化、持久命令回执、精确 deny-by-default tool allowlist、工具审批、最多两个且不可递归的隔离 SubAgent；冻结角色输出 token 上限会传递到 Pi Session。公开编排已实现确定性的议程/自由讨论/收敛状态机、发言权优先级与公平性、短轮次提示、角色自主申请、隔离有界观察器和 critical 抢答、无进展/软硬预算以及自动收敛或暂停。
- Windows：Session-first WinUI 3 壳、会话/角色/Skill/MCP/设置、公开记录/私聊、Windows Credential Manager、DPAPI + SQLite 事件/命令存储、重放、暂停/恢复、本地 Runtime 监督、会话导出/导入预检和签名清单更新器；自动主持状态带显示模式、当前议题、轮次/发言/抢答预算和发言申请，并提供模式切换、继续与下一议题控制。恢复会重建并传递完整调度快照。
- `@` 角色边界：公开消息仍对所有参与者可见，但只有被 `@` 的目标进入顺序发言队列；每个目标收到含自身 `roleId`/显示名的独占提示，禁止代写其他被提及角色的回答。
- 原生 Markdown：标题、段落、粗体、斜体、删除线、列表、任务项、表格、引用、代码块/复制、分隔线、安全链接和数学源码均在 WinUI 原生控件中呈现；原始 HTML 和危险 URL 不执行。
- 长记录：ListView 虚拟化；默认合并跟随最新流式内容；用户离开底部后不抢滚动并显示“跳到最新”；发送、重试或切换上下文时恢复跟随。
- 受控远端后端：签名设备令牌、meeting/audience/runtime/expiry 范围、私有 replay/SSE audience 过滤、可选 PostgreSQL 事务存储和幂等迁移。
- Windows x64：自包含发布、内置 Node/Pi Runtime Host/C++ Core、WiX 4 MSI 构建管线。

### Verified / pending 边界

- Runtime Host、协议、同步服务、Windows、C++ Core 的本机自动测试可重复运行；最终发布仍以当次 CI 和打包脚本结果为准。
- Windows 长记录虚拟化和自适应断点有自动化/静态覆盖。真实交互桌面已在 150% DPI 下完成 720/900/1280/1520 DIP、Markdown、设置页、键盘焦点以及亮色/暗色/系统高对比像素验收；高对比测试会保存并在 `finally` 中恢复原系统设置。托管桌面若连 `PrintWindow` 与屏幕捕获都不可用，脚本仍会把 `visualStatus` 标为 `pending`，只保存明确标注为非视觉等价物的 UIA 结构快照。96/192 DPI（Windows 100%/200%）的自动门禁已实现，但仍需各自在真实匹配会话产生证据。
- 真实 DeepSeek/Pi 的单点名、双点名、Markdown/LaTeX 源码回退、暂停/恢复与自由讨论自主申请已在本机完成一次功能与像素验收。结果、模型发现值、截图和输出摘要均不提交；当次 `functionalStatus` / `visualStatus` 只是时间受限的现场证据。
- 凭据通过 Windows Credential Manager 或仅限当前用户的一次性命名管道传递，证据树（含 data-root）逐文件扫描 Key；仓库与提交不包含测试 Key。

### Scaffolded

- Android：Compose Material 3 自适应 UI 脚手架；未接入生产同步和私有 audience。

### Pending

- 真正的数学排版引擎。当前只把 LaTeX 作为安全、可复制的源码样式呈现，不宣称已经排版公式。
- Windows/Android 到远端后端的完整认证同步、客户端内容 E2EE、key-envelope 管理、TLS、限流、保留任务和多副本通知。
- 正式可信且带时间戳的 MSI Authenticode 发布、ARM64；代码签名顺序、签名存在性、发布哈希顺序和临时证书清理已经自动验证，但临时自签名证书不构成生产信任。
- 100%/200% 的真实 DPI 视觉证据；严格聚合器会拒绝缺少 96/144/192 任一实际 DPI 或三主题报告的矩阵。
- 更长周期的真实会议仍需继续校准角色观察器触发阈值、探测预算与误抢答率；当前实现先用严格的 exact-evidence、类别和次数边界保守启用。

## 本轮实施细节

### `@` 只让目标角色回答自己

路由层把一个或多个 mention 解析成目标角色并顺序排队。修复同时约束 UI 解析和交给单个 Pi adapter 的提示，不修改公共用户消息和规范化事件：

- 明确当前唯一答复身份的显示名与 `roleId`；
- 要求只从本角色职责和视角作答；
- 禁止为其他被 mention 角色拟稿、模拟、总结成替代答复或创建其答复分栏；
- 仍允许引用公开记录并与其他角色观点发生分歧；
- 私聊仍只进入指定角色的隔离上下文。
- 独立的 `@` 是普通正文/Markdown 标点，不会被误判为一个空角色名；未知的非空 `@角色` 仍会显式报错，避免无意回退成全员回应。

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

### 真实讨论编排与防无限讨论

公开发言仍然只有一个角色占用话筒，但模型思考和非发言角色的有界观察可以并行。调度不依赖谁先返回，而按“主持明确安排 → 有证据的 critical 纠错 → 主持角色 → 回应上一位 → 普通申请”，再叠加请求序号、角色 ID 和连续发言惩罚确定顺序：

- 议程模式完成当前发言队列后不会暗自切题；主持人确认已经过所需轮次，再显式进入下一议题。自由讨论要求短、直接、像实际对话的发言；收敛模式只整理已有决策、异议、待补证据和行动。
- 非发言角色不是持续“监听”的常驻 Session。只有公开文本达到阈值或发言结束时，Runtime 才创建无工具、无 Skill、无 MCP、无 SubAgent 的隔离 Pi 观察器；重复观察先去重再扣预算，完成帧即使没有新增文字也有一次 final probe，模型调用通过可取消的三路并发门。
- 自动抢答必须指出当前原文中的连续证据，只允许事实、安全、需求或会议流程错误触发取消；结果来晚时自动降级为排队回应，不能打断后来开始的另一位角色。
- 连续发言、每段/每角色打断、观察次数、无进展、软发言/轮次和硬发言/轮次都有确定计数。软限制或连续无进展进入收敛；硬限制暂停自动主持并等待人类继续、改议程或结束。
- Windows 不展示隐藏清洗、路由或观察过程，只展示可核验的模式、议题、预算、队列和公开打断交接；输入仍只使用正文中的 `@角色名`。

## 执行结果与剩余计划

### P0：可靠本地 alpha — implemented；自动回归 verified，发布/视觉门禁按现场结果判定

1. Durable command receipt 已进入受保护的 SQLite 存储，四个强杀切点验证副作用不超过一次。
2. MCP allowlist 已精确到 tool，空列表不授权；未列出工具不能因审批而越权暴露。
3. 可控 provider/MCP 已覆盖取消、打断、超时、重试、批准/拒绝/首次记忆批准、暂停/崩溃恢复。
4. x64 打包脚本运行 C++、TypeScript、Windows 测试，并要求资源、严格 ICE03 allowlist、可复现生产依赖和 MSI 哈希门禁；发布验收另以非注册式管理提取、提取目录启动和像素矩阵核对 MSI 实际负载。`-SuppressMsiValidation` 不构成验证结果。

### P1：长期桌面使用质量 — implemented；公式排版与 100%/200% 现场证据 pending

1. transcript 使用原生虚拟化，1000 条消息场景验证实际容器数量保持有界。
2. 代码块、公式源码支持复制；外部链接显示完整目标并确认后交给默认浏览器。
3. 720/900/1280/1520 DIP 断点、UIA 名称/焦点和三主题结构有自动覆盖，并已在 150% DPI 的真实交互桌面完成亮色、暗色和系统高对比像素核验；100%/200% 仍需在真实匹配 DPI 会话补齐报告。
4. 工具审批具有到期状态与焦点恢复；失败角色可以就地重试，不重跑已完成角色。
5. 会话可导出规范化 JSON/Markdown，并在非破坏性导入前完成 schema、ID、模型路由和凭据引用预检。

### P1：受控远程平面 — backend implemented；client integration pending

1. 签名设备令牌绑定用户、设备、meeting、audience、runtime 和 expiry，并支持验证密钥轮换。
2. PostgreSQL store、事务租约/序号/事件、持久 cursor 和私有 audience 过滤已实现；内存模式明确只供本机开发。
3. E2EE 内容加密、envelope 管理 API、TLS、限流、保留任务和多副本通知仍 pending。
4. 服务器继续只转发/持久化规范化事件，默认不执行模型。

### P2：分发与第二平台 — pending

1. 隔离 MSI 生命周期已在完整 22,594 文件生产负载 verified 安装、启动、修复、升级、降级阻止、再次修复、卸载与零残留；每个 release candidate 继续重跑，并补正式 Authenticode/SmartScreen 声誉验收。
2. ARM64 打包。
3. Android 作为 UI-only 客户端接入认证同步、断线游标恢复和前台流。
4. 不新增 Oh My Pi 或其他运行时兼容面；Pi 保持唯一运行时。

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
| 自由讨论申请发言 | 多个观察器可并行判断；公开发言严格串行并按确定优先级/公平性出队 | 用模型返回速度决定顺序；同一角色无限连讲 |
| critical 抢答 | 先出现 `floor.requested`/`floor.granted`，再取消当前发言并公开交接原因 | 无原文证据的普通补充直接打断；迟到结果打断下一位角色 |
| 无进展/超预算 | 连续无进展或软限制自动进入收敛；硬限制暂停并等待主持 | 主持角色自行延长硬预算或收敛后继续无限循环 |
| 自动主持恢复 | 模式、议程状态、计数器和未处理申请随新 generation 恢复 | 只恢复 UI 标签、丢失队列或重置预算 |

## 一手参考来源

- Pi：[Compaction](https://pi.dev/docs/latest/compaction)、[Session format](https://pi.dev/docs/latest/session-format)、[Extensions](https://pi.dev/docs/latest/extensions)、[官方仓库](https://github.com/earendil-works/pi)
- PromptX：[Roles](https://promptx.deepractice.ai/docs/roles)、[Memory](https://promptx.deepractice.ai/docs/memory)、[ToolX](https://promptx.deepractice.ai/docs/toolx)、[ToolSandbox](https://github.com/Deepractice/PromptX/blob/main/docs/toolsandbox.md)
- Multica：[Agents](https://multica.ai/docs/agents)、[Squads](https://multica.ai/docs/squads)、[Tasks](https://multica.ai/docs/tasks)、[Workspaces](https://multica.ai/docs/workspaces)、[Providers](https://multica.ai/docs/providers)
- Proma：[官方仓库](https://github.com/proma-ai/Proma)、[README](https://github.com/proma-ai/Proma/blob/main/README.en.md)、[Agent session manager](https://github.com/proma-ai/Proma/blob/main/apps/electron/src/main/lib/agent-session-manager.ts)、[Agent event bus](https://github.com/proma-ai/Proma/blob/main/apps/electron/src/main/lib/agent-event-bus.ts)
- Cherry Studio：[官方仓库](https://github.com/CherryHQ/cherry-studio)、[Provider registry](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/provider-model/provider-registry.md)、[Agent loop](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/agent-loop.md)、[Session runtime](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/agent-session-runtime.md)、[Tool registry](https://github.com/CherryHQ/cherry-studio/blob/main/docs/references/ai/tool-registry.md)、[Security](https://github.com/CherryHQ/cherry-studio/blob/main/SECURITY.md)
- ReasoniX：[官方站点](https://reasonix.io/)、[主仓库](https://github.com/esengine/DeepSeek-Reasonix)、[Architecture](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/ARCHITECTURE.md)、[ACP](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/ACP.md)、[Session memory](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/SESSION_MEMORY_RETRIEVAL.md)、[SubAgent profiles](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/SUBAGENT_PROFILES.md)

外部产品和文档会继续变化；迁移判断基于 2026-08-01 至 2026-08-02 的官方公开面，采纳前仍需在本产品真实任务中 A/B 和回归。
