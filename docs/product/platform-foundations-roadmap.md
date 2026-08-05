# Pi Roundtable 基础能力与 Windows 优先路线图

- 更新日期：2026-08-05
- 适用范围：Pi-only、Windows local-first；Android 维持 UI-only
- 状态口径：`implemented` 表示代码与自动化存在，`connected` 表示用户路径可达，
  `verified` 表示相应验收已在当前候选版本完成，`pending` 不得写成已交付

`v0.4` 到 `v0.8` 的逐项代码、测试、真实场景与发版门禁由
[v0.4-v0.8 implementation backlog](v0.4-v0.8-implementation-todo.md) 跟踪。
本文件继续说明产品边界与执行顺序；backlog 是后续版本的可勾选交付清单。

## 1. 目标与不可破坏的边界

Pi Roundtable 的基础能力不是把更多第三方实现直接塞进桌面进程，而是建立可演进的
私有运行时层、规范化领域层和原生 Windows 交互层：

1. `protocol/schema` 只描述跨平台可观察事实，不承载原始 Pi session、provider cache、
   Windows DPAPI 密文或 SDK 私有类型。
2. `core` 继续保持确定性 C++20 reducer，不读取文档、不访问网络和数据库，也不执行插件。
3. 所有 Pi SDK 行为留在 `packages/runtime-host`；一个会议同时只有一个权威 Runtime Owner，
   写入仍由 `runtimeGeneration` 围栏保护。
4. Windows 在第一周期拥有本地模型运行时、机密、角色记忆和文档处理；同步服务只中继并
   持久化规范化事件，Android 不在本机启动 Pi。
5. 长上下文、长期记忆、文档内容和插件输出都视为不可信输入。模型不能成为授权层，
   记忆召回也不能覆盖 System Prompt、用户本轮要求或工具审批策略。

## 2. 能力基线

| 能力 | 基础层 | 用户路径 | 当前验证 | 下一验收门槛 |
| --- | --- | --- | --- | --- |
| 自动上下文压缩 | implemented | connected | runtime-host 单元/集成测试 | 真实长会话记录压缩前后 token、遗漏率和延迟 |
| 稳定前缀与 provider cache affinity | implemented | connected | 参数与稳定提示词测试 | 由真实 provider usage 验证 cache read/write，未支持的 provider 必须安全忽略 |
| 角色长期记忆修订库 | implemented | connected | Windows 加密、候选审核、修订与冻结召回测试 | meeting-close 候选、安全扫描、保留/隔离和真实长会话验收 |
| 结构化本地存储 | implemented v3 + platform artifact slice | connected for events, memory and composer artifacts | SQLite 精确 schema、v2→v3 原子迁移、CAS 引用/配额/重启测试 | 完整 platform migration journal、备份/恢复、损坏隔离和数据保留 |
| Pi Skill | implemented | connected | 已核验路径和 digest 测试 | 安装/更新/撤销的用户反馈与审计记录 |
| MCP 工具插件 | implemented | connected | allowlist、审批、传输和冲突配置测试 | 运行配额、可取消任务和内容安全诊断 |
| provider 内建 Web Search | implemented | connected for granted OpenAI routes | 受控 provider fixture、授权/模型/网络策略和隐私诊断测试 | 真实 provider、兼容 provider adapter 与长会话配额验收 |
| raw Pi extension | intentionally unsupported in-process | unavailable | 能力声明测试 | 只有隔离进程 + 规范化工具桥完成后才可重新评估 |
| Markdown | implemented renderer + safe input | renderer connected; document input pending | parser/render 与文档测试 | composer 附件、复制/导出一致性、无障碍 |
| LaTeX/TeX | source fallback + safe input | source fallback connected | 代码块/数学源码测试 | KaTeX 级排版、超时/复杂度限制、复制源码 |
| DrawIO | bounded XML normalization | pending | XXE、大小和文本抽取测试 | 附件预览；输出应生成独立文件而非把 XML 塞入事件 |
| XLSX/DOCX/PPTX | bounded OOXML text normalization | pending | magic、宏、遍历、膨胀和最小包测试 | 附件 UI、结构保真预览、独立导出 worker |
| PDF | signature metadata preflight only | pending | 签名与大小测试 | 有界正文提取/OCR、页数/字体/图像炸弹限制、独立导出 worker |
| x64 MSI | implemented | connected | 完整负载构建；生命周期依赖管理员会话 | 默认 ICE、安装/修复/升级/降级阻止/卸载全部重跑 |
| ARM64 MSI | pending | unavailable | none | x64 release gate 稳定后单独实施 |

## 3. 上下文压缩与前缀缓存

### 3.1 已实现策略

- 角色身份、职责、工具边界和稳定行为规则构成确定性的 System Prompt 前缀。
- 议题、当前发言者、`@` 路由、轮次要求等动态状态留在用户 turn，避免每轮破坏前缀。
- 运行时在模型最终解析后读取它的实际 context window，再计算压缩阈值；默认约在窗口
  62% 触发，压缩后保留约 20% 的近期上下文，避免把 provider 的输出余量吃满。
- 同一角色 session 使用稳定 session affinity，并向支持的 provider 传递短期 cache retention；
  不支持缓存的 provider 不得因此失败。
- 压缩策略属于 Runtime Host 私有实现，不新增公共事件，也不把 provider usage 明细广播给
  其他角色。

### 3.2 后续评估指标

真实长会话验收至少记录：首次与后续请求的输入 token、cache read/write、压缩发生点、压缩
耗时、关键约束召回率、最近 N 轮逐字保留率、工具结果是否错误进入稳定前缀。不能只凭
“API 接受了 cache 参数”宣称命中缓存。

本设计吸收 ReasonIX 的稳定指令与受限背景记忆分层思路，而不复制其应用架构；以当前
[Session memory retrieval](https://github.com/esengine/DeepSeek-Reasonix/blob/main-v2/docs/SESSION_MEMORY_RETRIEVAL.md)
为主要参考，历史版本的 pinned prefix 只能作为演进背景。

## 4. 长期角色记忆与结构化存储

### 4.1 已实现存储模型

`RoleMemoryStore` 以 `workspaceId + roleProfileId + memoryId` 标识一条逻辑记忆。逻辑记录只
移动 `currentRevision` 或进入 soft-superseded 状态，正文修订 append-only，包含：

- `identity`、`preference`、`fact`、`decision`、`lesson` 五类语义；
- `user_approved`、`meeting_close_policy`、`automatic_policy` 三类写入授权；
- 来源会议/事件、置信度、创建与更新时间；
- 基于 expected revision 的 compare-and-swap，避免并发覆盖；
- 当前 Windows 用户 DPAPI 加密；SQLite schema v3 与事件库共享写入门，并新增加密候选、
  append-only recall audit、不可变 context snapshot 与 bounded retention job 表。
- 独立 `platform.db` v1 已实现 composer artifact 所需的最小 descriptor/binding 索引；完整
  module catalog、迁移日志、诊断与备份边界仍按
  [ADR 0013](../adr/0013-local-database-ownership-and-forward-migrations.md) 继续为 `planned`。

### 4.2 接线策略

当前已接线的路径采用显式策略控制，而不是后台静默改人格：

1. 人工创建/编辑永远可用，并可查看修订来源、撤销或归档。
2. `review_required` 只生成候选；用户批准后才进入 active memory。
3. `meeting_close` 自动生成差异候选仍为 `pending`；当前只接通人工候选，且角色运行时禁用
   记忆修改，不能静默改变已冻结的上下文。
4. `selective` 召回在每个角色启动时冻结，最多 4 条、总计 6,000 字符；当前按更新时间、
   记忆类型和稳定 ID 确定性排序，语义相关性排序仍为 `pending`。
5. 召回正文放入标记为“不可信背景事实”的私有角色上下文，不能生成公共事件，不能进入
   会话 JSON/Markdown 导出，也不能携带 DPAPI 密文。
6. 自动写入需要 safety scan、来源证据和可见撤销；人格/System Prompt 演进与事实记忆分表。

Hermes 的会话开始冻结记忆、受控写入和 provider hook 是此处的重要参考，但本项目仍由
Windows Runtime Owner 实施自己的边界：
[memory](https://hermes-agent.nousresearch.com/docs/user-guide/features/memory)、
[memory providers](https://hermes-agent.nousresearch.com/docs/user-guide/features/memory-providers)、
[context compression and caching](https://hermes-agent.nousresearch.com/docs/developer-guide/context-compression-and-caching)。

## 5. Pi 插件兼容边界

本项目不追求“任意 Pi 扩展原样可运行”。兼容的单位是能力，而不是把第三方 JavaScript
加载进权威会议进程：

- **Skill**：经 catalog、安装 digest 和真实路径边界核验后，作为 Pi 原生资源加载。
- **工具插件**：优先提供 MCP server；工作区 grant 固定 transport、endpoint/command、
  tool allowlist、approval mode 和 execution mode。同一 `serverId` 的冲突授权直接拒绝。
- **raw extension**：当前不兼容。Pi 官方扩展拥有完整 OS 权限，会议进程又持有 provider
  凭据和本地文件能力，二者不能共享信任域。
- **未来隔离桥**：若确有不可替代扩展，应在低权限子进程中运行，只暴露有 schema 的
  规范化工具，限制工作目录、环境、输出、超时和并发，并复用 MCP 审批面。

参考边界：Pi 的
[session format](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/session-format.md)
与 [extensions](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/extensions.md)。

## 6. 文档与富内容管线

### 6.1 输入架构

所有文档先经过 `DocumentPipeline`，产出小型 `DocumentArtifactPreflight`：格式、文件名、
字节数、处理模式、规范化文本和警告。确认后，原文件由独立 `platform.db` descriptor/
meeting binding 与 SHA-256 content-addressed store 管理；导入会重新打开并校验预检摘要，
配额只清理未绑定的 LRU 内容。原文件不是聊天字符串，跨设备协议未来只引用受作用域
保护的 artifact ID。

安全门槛包括：32 MiB 原始文件、128 MiB OOXML 解压总量、2048 个 ZIP entry、文本上限、
扩展名与 magic 双校验、路径穿越/压缩炸弹防护、禁用 DTD/external entity、拒绝宏格式。
Markdown/TeX 保持 inert source；Office/DrawIO 当前只抽取有界文本；PDF 不在没有专用解析器
时假装拥有正文。

### 6.2 输出架构

复杂输出不得在 WinUI UI thread 或 Runtime Host 主进程内同步生成。后续采用独立 artifact
worker：

- Markdown/TeX/DrawIO 可从规范中生成文本型文件，并保留原子写入和覆盖确认；
- XLSX/DOCX/PPTX/PDF 使用固定版本的生成器与模板，输出到用户选择的目录；
- 每个 worker 有 CPU/内存/时间/页数或 sheet/slide 数预算，失败只返回结构化诊断；
- 生成后重新打开验证，Office 文件检查包关系，PDF 做页渲染 smoke test；
- 聊天事件只包含摘要与 artifact reference，不携带数十 MiB 的 base64。

## 7. Windows MSI 体积与生命周期

构建只裁剪复制到 staging 的 Node runtime，绝不修改仓库依赖。当前允许移除：source map、
TypeScript declaration 和 tsbuild metadata；随后必须运行 `node --check`、协议包 import 和
Runtime Host import。不能仅按扩展名删除 `.json`、原生 `.node`、字体、WinUI resources 或
ONNX/DirectML 文件。

每个 release candidate 的门禁顺序：

1. C++、TypeScript、Windows tests；
2. self-contained publish 与 staging import smoke test；
3. WiX harvest 与默认 ICE validation；
4. 独立 QA Product/UpgradeCode 的 install、launch、repair、major upgrade、downgrade blocking、
   second repair、uninstall 和残留检查；
5. 最终资产 hash、SBOM、NOTICE 与发布后重下载校验。

正式证书签名、干净 VM 生产 UpgradeCode 演练、真实 96/144/192 DPI 和高对比矩阵
均为可选诊断；未执行时记录为 `pending`，不阻止个人项目发版。

`-SuppressMsiValidation` 只用于已由等价产物通过 ICE 的受限重打包场景；非管理员会话、
Windows Installer 服务不可达或仅成功生成 MSI，都不能写成 lifecycle verified。

## 8. 分阶段执行顺序

### R1：当前基础层收口

- [x] 实际模型窗口驱动的自动压缩、稳定前缀与 cache/session affinity
- [x] Pi Skill/MCP/raw extension 能力声明和冲突配置 fail-closed
- [x] SQLite v1→v2 迁移、角色记忆修订、DPAPI、CAS 与 soft supersession
- [x] Markdown/TeX/DrawIO/OOXML/PDF 安全输入预检基础
- [x] MSI staging 开发文件裁剪与运行时 import smoke test
- [ ] 管理员环境完成当前候选包默认 ICE 与完整 MSI lifecycle

### R2：Windows 用户路径

- [x] 角色记忆管理页：候选、批准/拒绝、修订历史、停用/恢复与 provenance
- [x] 会话启动时冻结一次 bounded memory recall，并注入 host-private role context
- [ ] 公开与私聊 composer 的附件选择、预检摘要、移除、发送失败保留。状态：公开路径
  `implemented`，私聊附件路径 `pending`
- [x] 会话分组移动和带影响摘要的删除；事件、命令、SubAgent、候选、audit、snapshot 与
  meeting artifact binding 完整清理，失败通过 staged delete 在重启时恢复
- [x] 恢复时读取完整 normalized history，修复缺失/落后的 checkpoint，并按角色 audience
  生成有界可见上下文；不声称恢复原始 Pi session
- [ ] KaTeX 级数学排版，同时保留可复制源码与安全回退
- [ ] PDF 有界正文提取；所有格式都显示来源文件与截断警告

R2 验收必须证明：记忆/附件不改变 `protocol/schema`，不进入无权限 audience，不泄漏本地
绝对路径或密文，旧会话/旧数据库可正常恢复，长记录和窄窗口布局不退化。

### R3：结构化输出与可观测性

- [ ] artifact worker 与 Markdown/TeX/DrawIO/DOCX/XLSX/PPTX/PDF 输出
- [ ] content-addressed local artifact index、配额、保留策略、备份/恢复。状态：CAS、事务索引、
  shared binding、配额与 orphan reconciliation `implemented`；保留策略和备份/恢复 `pending`
- [ ] 内容安全 OTel 指标：context ratio、compaction、cache、retrieval、artifact worker
- [ ] 隔离 Pi extension bridge 的可行性原型；只有安全验收通过才调整兼容状态

### R4：第二平台

只有 Windows R2/R3 的数据迁移、真实会议、安装升级卸载、DPI/高对比和恢复门禁稳定后，
才扩展 Android 的远程查看/控制和 artifact projection；Android 仍不成为本地 Pi owner。

## 9. 代码质量约束

- 依赖方向由 UI → application service → storage/runtime adapter，禁止 ViewModel 直接写 SQL、
  解 ZIP 或解释 Pi event。
- 大型 ViewModel 逐个 use case 抽离，不进行一次性重写；每次抽离先锁定行为测试。
- 注释覆盖公共接口、线程/所有权、信任边界、状态机不变量和不直观的兼容约束；不追求对
  自解释赋值逐行注释的虚假百分比。
- 数据库 schema、私有 initialize frame 和 artifact descriptor 都要版本化；迁移只能向前，
  失败必须保留原数据并给出可恢复诊断。
- 每项功能同时具备成功、取消、超时、损坏输入、权限拒绝、重启恢复与旧版本兼容测试，
  才能从 `implemented` 升为相应路径的 `verified`。
