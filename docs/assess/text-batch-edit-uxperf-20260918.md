# TextBatchEdit（文本批量编辑）用户体验 + 性能静态评估

## 0. 元信息

| 项 | 内容 |
|---|---|
| 评估日期 | 2026-09-18 |
| 评估角色 | 临时 research（只读，不改产品代码、不构建、不启动/操作 EPLAN、不联系其他 agent） |
| 工作区 | worktree `chore-probe-textbatch-uxperf`（基点 develop `1ac6ebd`） |
| 评估对象 | `EA.EplAddIn.TextBatchEdit/`（net472 / WinForms / DataGridView 非模态窗） |
| 主要源码 | `EA.EplAddIn.TextBatchEdit/TextBatchEditForm.cs`（2643 行）、`TextBatchEditAction.cs`（398 行）、`AddInLogger.cs`（309 行） |
| 输入 | `docs/design/text-batch-edit/HANDOFF.md`（§4.4 / §7.1 / §8.1-A）、仓库 `AGENT.md` |
| 方法 | 纯静态代码走查 + 微软官方文档/参考源码核对；**未运行、未构建、未连 EPLAN** |
| 证据等级 | 【代码实锤】＝本仓 file:line 可直接定位机制；【静态推断】＝合理但需实测确认，标 `[UNCERTAIN]`；【待真机实测】＝静态无法判断 |

> 约定：下文所有行号均以本次 worktree 实际文件为准（引用前已逐处 grep/实读复核，未照抄 HANDOFF）。本文不给出"已变快/已变慢"式结论；所有优化方向仅为建议，立项与优先级由 xavier 决定。

---

## 1. 范围与方法

1. 通读 `TextBatchEditForm.cs` 全文（2643 行）、`TextBatchEditAction.cs` 全文、`AddInLogger.cs` 写盘路径。
2. 对照 HANDOFF §4.4 的 P1-a/P1-b/P2-a/P2-b，逐条回到代码核实，不沿用文档结论。
3. 联网核对微软官方资料（见 §9 参考链接）：
   - Best Practices for Scaling the Windows Forms DataGridView Control；
   - `IDataGridViewEditingControl.EditingControlWantsInputKey` API 文档；
   - `DataGridViewEditMode` 枚举与 `DataGridView.EditMode` 默认值（`EditOnKeystrokeOrF2`）；
   - .NET WinForms 官方开源仓库 `DataGridViewTextBoxEditingControl.cs`（release/8.0 标签，用于确认键位判定逻辑；与 4.7.2 的逐位一致性见 §8 [UNCERTAIN]）。
4. 范围外：不评估 EPLAN API 调用在 C++/CLI 层的真实耗时（静态不可知，全部进真机埋点清单）；不重开 T1–T8（HANDOFF §8.1-A 已列，本文只引用不复制）。

---

## 2. 结论摘要（一页）

1. **P2-b（数据安全）成立，机制可实锤到"自动保存对【处于编辑态的当前格】立即 `EndEdit()`（不看它是否脏、是否刚进入）"这一步**：`SaveDirty` 开头（`TextBatchEditForm.cs:2272`）为 `if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();`——只判编辑态、不区分该格是否脏、是否刚进入；600ms 防抖只被文本变更/提交事件重置，**进入新格编辑不重置计时器**，Tick 内也不检查当前格是否在编辑。后一格被强制退出编辑回到选中态后，后续按键按 DataGridView 默认 `EditOnKeystrokeOrF2`（官方文档确认的默认值）以"选中格键入即覆盖"处理。`_saving` 只防重入，防不了这个窗口。详见 §4。
2. **P1-b（闪烁/掉帧）机制清晰**：① `EditGrid` 自身**未开双缓冲**（全仓仅底部 `BufferedPanel:2564-2567` 开了），列宽/行高实时拖拽必然闪；② 排序走"全表 `Rows.Clear()` + 逐行重建 + `Refresh()`"（`ApplySort:1586-1699`），整表销毁重建；③ 开窗装载本身就执行了**两遍**行重建（`LoadRows` 后立刻 `ApplyDefaultOrder`）。
3. **P1-a（卡顿）是多个反模式叠加**，且不需要"特别大批量"即可感知：unbound 逐格赋值导致行不可共享（命中官方 scaling 反模式原文）、每格每次重绘做字符串/字典比较的 `CellFormatting`、每次当前格移动都全表 `Invalidate()`+全量脏扫、`AutoSizeToAllHeaders` 行首模式、Debug 日志逐条同步刷盘（`MinLevel=0`）、EPLAN 互操作在 UI 线程同步进行。没有单一银弹，也不必一上来 VirtualMode。详见 §3。
4. **P2-a（Home/End）实锤**：本仓 `LineBreakTextBox.EditingControlWantsInputKey`（`:2577-2584`）只特判 Ctrl+Enter，其余交基类；.NET 基类对 Home/End 的规则是"**仅当文本未被全选时编辑框消费；全文选中时让位给 grid**"（官方源码 174-181 行），而本仓双击进编辑显式 `BeginEdit(true)`（`:476-479`）＝全文全选，于是 Home/End 落到 grid 行导航。修复方向是一行级 override，工作量 S。
5. **静态新发现（数据安全次要点）**：窗口 X 关闭和"取消"都不检查未保存修改（无 `FormClosing` 处理，`Cancel` 直接 `Close()`，`:1188-1189`）。自动保存关闭时，未点"应用"的改动会静默丢失。
6. 建议处理顺序（建议性质）：**P2-b 竞态 ＞ 关窗未保存保护 ＞ P1-b 双缓冲 ＞ P1-a 装载/绘制组合优化 ＞ P2-a 键位 ＞ 一般 UX 打磨**。见 §6。

---

## 3. A. 性能静态审计

> 官方反模式编号对应 §9 链接 [1]（Best Practices for Scaling）。所有"为什么卡/闪"的机制描述为代码+框架语义推断，实际耗时占比一律以真机 Profiler/埋点为准。

### 3.1 审计总表

| # | 现状（位置） | 命中的官方反模式 / 机制 | 为什么会慢或闪 | 优化方向（利弊，不定方案） | 工作量 |
|---|---|---|---|---|---|
| A1 | 页选择路径全量同步枚举：`CollectTexts` 对 `pages` × `page.AllPlacements` 双层 foreach（`TextBatchEditAction.cs:102-112`，`AllPlacements` 取值在 `:107`、内层遍历 `:109-111` 仅做 `is TextBase` 过滤），在 Action Execute（UI 线程，`:189`）内完成 | 全量同步物化；未用 `DMObjectsFinder` 等服务端筛选（AGENT.md 记载 EPL-Scripts 有性能优化笔记，未核实 API 细节 `[UNCERTAIN]`） | `AllPlacements` 物化整页全部对象（元件、连接、符号…），成本随**页内对象总数**而非文本数；大页/多页时 EPLAN 互操作往返多，窗还没出现 EPLAN 已像卡死 | ① 调研 `DMObjectsFinder` 按类型只取 Text/PathText（需查离线 API 文档，M）；② 收集移后台线程（EPLAN API 对象的跨线程可用性未证实 `[UNCERTAIN]`，风险高，先不选）；③ 至少加等待光标/计时日志（S） | M（含调研） |
| A2 | `LoadRows` 逐行 `_grid.Rows.Add()`（`:1505`）后立刻给该行约 20+ 个单元格逐个 `.Value=`（`:1507-1547`）；unbound 模式 | 官方原文："**A row cannot be shared in an unbound DataGridView control if any of its cells contain values**"；且官方建议避免逐格访问（访问 cell 使行 unshared） | Add() 无参重载本可共享行，但随后赋值使每行立即实例化完整 `DataGridViewRow`+约（17+2×语言数）个 Cell 对象；每行还伴随布局/样式失效。N=1000、23 列时约 2.3 万个 cell 对象 + 2 万次以上逐格写 | ① 装载期挂起重绘（`WM_SETREDRAW`）再批量 Add（S，省的是布局/绘制而非对象分配）；② 数据先组装成数组/`AddRange` 一次提交（unbound 下含值行仍不共享，收益有限，S-M）；③ 根治用 VirtualMode（L，见 A10） | S / M / L |
| A3 | 开窗即装载两遍：构造函数 `LoadRows()`（`:184`）→ `ApplyDefaultOrder()`（`:186`）→ `ApplySort(...,0)` 内 `Rows.Clear()`+逐行重建（`:1672-1685`） | 重复全量构建 | 所有行在窗出现前被构建两次（含每行 EPLAN 读、赋格、快照）；首开耗时近乎翻倍 | `LoadRows` 直接按 `_meta` 默认序组装后一次性入表（数据本来就有 `BuildMeta` 的 rank），开窗不再跑 `ApplySort`；或至少排序改为"只重排数据+逐格搬值不 Clear"。风险：排序/序号盖章逻辑要回归（M） | M |
| A4 | `RowHeadersWidthSizeMode = AutoSizeToAllHeaders`（`:342`）；`ApplySort` 结束还手动 `AutoResizeRowHeadersWidth(...,AutoSizeToAllHeaders)`（`:1693`）；列头 `ColumnHeadersHeightSizeMode=AutoSize`（`:350`） | 官方："Avoid using automatic sizing on a large set of rows … For row headers use AutoSizeToDisplayedHeaders/AutoSizeToFirstHeader；maximum scalability: turn off + programmatic resizing" | AllHeaders 模式在增删行/排序时对**全部行首**测量，且该模式下用户拖拽行首宽会被自动值覆盖（注释已知）；排序后再来一次全量测量 | 装载/排序期临时切 `DisableResizing`，结束用固定宽度（行号位数可由总行数算出）程序化设一次；或 `AutoSizeToFirstHeader`。注意 xavier 偏好"行高拖拽用内置"，不要动行高内置路径，只改行首宽策略 | S |
| A5 | `AutoFitColumns()`（`:877-921`）：对每个可见列 × **全部行**的每个非空格做 `TextRenderer.MeasureText`（GDI）；在 `Shown`（`:196`→`UpdateColumnVisibility:847`）、每次任意显示开关（`:735-744`）、排序后链路中执行 | 官方自动测量反模式（全量而非 displayed） | 可见列约 8–10 × N 次 GDI 文本测量 + 每行 `Split('\n')` 分配；开关一次"显示原值"列数翻倍再测一遍；拖拽列宽不触发它（手动 Width），但开关/开窗/重载必触发 | ① 装载期用 `WM_SETREDRAW` 包住；② 只测显示行（DisplayedRows）+ 抽样，列宽上限 420 已有限制（`:879`），抽样风险是长文本在滚动后被截（Tooltip 已开 `:347` 可兜底）；③ 结果按（数据指纹,列集合）缓存，切开关不全表重测 | M |
| A6 | `CellFormatting`（`:1005-1043`）每格每帧重算状态：`CurrentCellValue`（`:1128` 每次索引 `_grid[col,row].Value`）、`StateValue`（`:1120-1126`，含 LINQ `First`）、逐字符串 Ordinal 比较 ×2（脏/已保存）；行首自绘 `RowModificationColor`（`:1147-1157`）对每行遍历所有列再各做两次比较；`DrawSortArrow` 每次绘制 `new Font`（`:1100`） | 官方："avoid work in CellFormatting / use shared style instances"（同页 cell styles 节）；每帧分配 | 任意滚动/失效都对所有可见格跑字典查找+字符串比较+LINQ；GDI Font 每帧分配。单次不重，但被 A7 的全表 Invalidate 放大成"移动一下当前格就整表重算重绘" | ① 行级脏/保存状态做缓存（变更/保存时维护一个行状态枚举），`CellFormatting` 只查枚举、直接换用**共享的 DataGridViewCellStyle 实例**（官方建议，避免每格改 `e.CellStyle.BackColor` 触发样式对象克隆）；② 箭头 Font 提为静态字段 | M |
| A7 | `UpdateApplyEnabled()`（`:1749-1755`）：`DirtyRows()` 对每行 `SnapshotRow()`（`:1711-1724`，每行 new RowState+new Dictionary+逐语言填值）→ 然后 `_grid.Invalidate()` **全表重绘**；它挂在 `CellValueChanged`（`:431`）与 **`CurrentCellChanged`（`:495`）**；开"单元格聚焦"时 `CurrentCellChanged` 还先额外 `Invalidate()` 一次（`:494`） | 全量 O(N) 扫描 + 全表重绘被高频事件触发 | 键盘每移动一格：N 次字典分配 + 整张表 CellFormatting 重算（联动 A6）。这是"不算特别大批量也卡"的强候选：成本按 N 计费却由最高频的焦点移动触发 | ① 脏计数增量维护（提交时 ±1），移除周期性全扫；② 失效只针对受影响行（`InvalidateRow`/`Invalidate(cell.RowIndex)`），聚焦行列高亮可只失效旧/新两条行列；③ 去掉重复 Invalidate | M |
| A8 | `EditGrid` 未开双缓冲：grid 构造块（`:332-369`）无 `DoubleBuffered/SetStyle`；全仓 grep 仅 `BufferedPanel`（`:2564-2567`，底部栏）开启 | DataGridView 默认双缓冲关闭（protected `DoubleBuffered`，子类可直接 set） | 列宽拖拽（实时跟手，每像素一次全表 layout+paint）、行高拖拽、排序重建（A9）时逐元素擦除重画 → 可见闪烁/掉帧。**外层 Panel 双缓冲救不了子控件自绘** | 在 `EditGrid` 构造里 `DoubleBuffered = true`（同类内可访问 protected，S、风险极低）；若仍有残余闪烁再叠加 `WS_EX_COMPOSITED` 评估（M，需真机比对远程桌面下的副作用 `[UNCERTAIN]`） | S |
| A9 | 排序：把全表 N×列数 的 Value 装箱到每行一个 `object?[]`（`SortView.Values`，类定义 `:1574-1581`，填充 `:1594-1609`）→ `Rows.Clear()` → 逐行 Add+逐格赋值重建（`:1672-1685`，循环内还重设行高与 `SetRowEditable`）→ `AutoResizeRowHeadersWidth(AllHeaders)`（`:1693`）→ `Invalidate(DisplayRectangle)`+`Refresh()`（`:1694-1695`） | 全量重建 + 同步 Refresh；无任何重绘挂起 | 排序瞬间整表销毁重建并强制同步重绘，叠加非双缓冲（A8）＝肉眼可见白闪/掉帧；大表排序期间 UI 线程长阻塞 | ① 排序期 `WM_SETREDRAW OFF`→重建→ON 一次重绘（S）；② 中期：排序只重排并行数据列表并逐格搬值（unbound 仍要写 N×列，但省掉行/格对象销毁重建）（M）；③ VirtualMode 后排序只换索引（L） | S→M |
| A10 | 数据模式：纯 unbound、无 `VirtualMode`、无分页（grep 全文无 VirtualMode/SuspendBinding） | 官方：大数据量应实现 virtual mode 自行管理数据 | 天花板问题：A2/A6/A9 的根治都指向它；但本插件数据（`_texts`+`RowState`+`_meta`）本就存在网格之外，具备 virtual 化基础，改造面是编辑提交、复选框、暂存、排序、行高/着色全套交互，回归风险大 | 先做 A2/A6/A7/A8/A9 的低成本组合，真机量到 2000+ 行仍不达标再上 VirtualMode（官方 walkthrough 含行级提交/回滚示例） | L |
| A11 | 日志：`MinLevel=0` 全开 Debug（`AddInLogger.cs:17`），每条 `File.AppendAllText`（`:301`，开-写-关同步刷盘，全局锁）；加载热路径每行至少 1 条 Debug（`LoadRows:1475` 一条含 `string.Join`+`Preview`+多次属性读取），`BuildMeta` 另有 Debug（`:1315-1317` 等），保存每格/每行多条 | 热路径同步 IO + 日志字符串预先拼接（即使未来升级别，`:1475` 的拼接也已发生） | 每行若干次同步文件 IO，且发生在 UI 线程最敏感的装载循环里；网络盘/杀软扫描目录时更明显。占比未知，但"零成本关掉一半日志"确定 | ① 分发版 MinLevel=Info（AGENT.md:111 本就计划）；② Debug 调用改惰性（先判级别再拼字符串）或加采样；③ 保留加载关键里程碑各一条（带 Stopwatch 耗时，见 §3.3） | S |
| A12 | 保存路径 UI 线程同步：单 `UndoStep`+单 `Transaction`（`:2290-2292`，**事务粒度正确，一个撤销点符合已验证设计**），但脏行循环内 setter `t.Contents=`（`:2340`）、之后对**每个脏行**回读：`t.Contents` 再逐语言 `GetString`（`:2369-2404`），单语言分支还连调 `GetStringToDisplay`/`GetString(L___)`/`InternalString`（`:2395-2398`） | UI 线程同步互操作；无保存中反馈 | 自动保存/应用期间窗口冻结；冻结时长＝写回+回读互操作总时长。它既是"保存后卡一下"的来源，也是 P2-b 覆盖窗口的放大器（§4）。回读校验是防"UI 成功模型未落库"的安全措施，不能砍 | ① 埋点先拆"写回/回读"耗时（§3.3），用数据决定是否只对改动语言回读（现状 translated 分支对全部项目语言比对，`:2381-2391`）；② 保存期间等待光标+状态栏"保存中…"；③ 是否后台化受 EPLAN 事务线程模型限制，列 `[UNCERTAIN]` 不建议先做 | S（反馈）/ M（精简回读） |
| A13 | `ZoomGrid`（`:76-105`）：foreach 全部列设 Width、foreach 全部行设 Height、再换 `Font`，每步独立触发布局，无重绘挂起 | 高频布局失效 | Ctrl+滚轮一档在大表上触发 N 行×列次布局风暴，掉帧 | 三套用 `WM_SETREDRAW` 包成一次布局；保留现有缩放语义 | S |
| A14 | 装载/刷新无任何等待反馈：构造函数在 `form.Show()`（`TextBatchEditAction.cs:232`）之前同步跑完 `LoadRows+ApplySort+`（Shwon 前的列宽测量）；`ReloadSelection`（`:1385-1432`）同样同步 | —（UX/性能交叉） | Action 触发后到窗口出现之间 EPLAN 主界面无响应、无等待光标，用户感知为"点了没反应/卡死" | 开窗即显示带"正在加载 N 个文本…"的空表 + `UseWaitCursor`，装载移到 Shown 后（若再配合 A3/A2 收益明显）；成本是要把构造函数瘦身 | M |

**不命中项澄清（核查后排除）**：

- `AutoSizeColumnsMode`/行自动尺寸**没有开**（`:337` 为 None、`:346-348` 注释明确不用自动换行/自动行高）——列宽成本全部来自自写的 `AutoFitColumns`（A5），不是框架自动测量。
- `Rows.Add` 期间用 `_syncing=true`（`:1446-1559`）抑制了 `CellValueChanged` 联动，未挂着业务事件做重复计算；但同步标志不阻止框架自身布局/行不可共享。
- `CellPainting`（`:1050-1092`）只画表头/行首，数据格没有自绘重活；数据格背景色走 `CellFormatting`（A6）。

### 3.2 VS Profiler 目标函数与埋点建议（供后续 worker 实施、xavier 真机量测）

**VS Profiler（附加 `W3u.exe`/`Eplan.exe`，混合模式建议勾选 native debugging）**：

- CPU Usage（采样先跑）热点候选：`LoadRows`、`BuildMeta`、`ApplySort`、`AutoFitColumns`、`GridOnCellFormatting`、`RowModificationColor`、`DirtyRows`/`SnapshotRow`、`SaveDirty`；若 EPLAN 互操作占主导，采样会落到 native（`Eplan.EplApi.*`/CLI 封送），此时以埋点墙钟时间为准。
- Instrumentation（ instrumentation 模式）核对调用次数：重点确认 `CellFormatting`/`Invalidate`/`SnapshotRow` 在一次焦点移动、一次排序中的调用计数。

**Stopwatch 埋点位置（临时 Debug 日志，验收前清理或降级）**：

| 埋点 | 起 | 止 | 附加计数 |
|---|---|---|---|
| 收集 | `TextBatchEditAction.cs:189` 调 `CollectTexts` 前后 | — | 文本数、页数、`AllPlacements` 总数 |
| rank 构建 | `TextBatchEditForm.cs:1445` `BuildRankMaps()` 前后 | — | 三段节点数 |
| 装载-EPLAN 读 | `:1450` 循环内每对象读 Contents/meta 段累计 | — | 每 100 行打点一次，观察是否线性 |
| 装载-入表 | `:1505 Rows.Add` 与赋格段累计 | — | 行数、列数 |
| 装载总计 | `:1434`–`:1561` | — | — |
| 默认序重建 | `ApplySort:1586`–`:1699`（验证 A3 第二遍成本） | — | n |
| 自动列宽 | `AutoFitColumns:877`–`:921` | — | MeasureText 次数（列×非空格） |
| 一次焦点移动 | `CurrentCellChanged`（`:492`）handler 整体 | — | `DirtyRows()` 耗时、`CellFormatting` 自增计数 |
| 排序交互 | header 双击到 `ApplySort` 返回 + 首次绘制完成 | — | 帧数/耗时 |
| 保存-提交当前格 | `SaveDirty:2272` `EndEdit()` 单行耗时 | — | 是否当前格在编辑（P2-b 观测点） |
| 保存-写回 | `:2294`–`:2354` | — | 脏行数 |
| 保存-回读 | `:2362`–`:2406` | — | 比对语言次数 |

另加两个计数器：`CellFormatting` 每秒调用次数、`_grid.Invalidate` 调用来源计数（验证 A7）。

**建议测试数据量级**：文本对象 100 / 500 / 2000 / 10000 四档；项目语言数 1（单语言项目）/ 3 / 6；两种来源各测——图形编辑器直接框选文本（N≈文本数），与页导航器选大页（单页放置对象 2000+ 但其中文本少量，专测 A1 的枚举放大效应）。每档记录开窗耗时、排序耗时、按住列宽分隔线拖 2 秒的主观帧率、自动保存一轮耗时。

---

## 4. B. P2-b 竞态静态走查（数据安全，重点）

### 4.1 参与方与时序

- 防抖定时器：`ScheduleAutoSave`（`:1794-1820`），`System.Windows.Forms.Timer`，`Interval=600`（`:1800`，UI 线程 Tick）；每次 `Stop()`+`Start()` 重启（`:1818-1819`）。
- 谁会重置它：`CellValueChanged`（`:432`，格**提交后**）与编辑框 `TextChanged`（`EditingTextChanged:1827-1831`，编辑中每个字符）。
- Tick（`:1801-1816`）：停表 → 检查 `_saving`/开关 → **`DirtyRows().Count == 0` 就返回**（`:1805`，只看有没有脏行，不看当前格状态）→ `_saving=true` → 同步 `SaveDirty(quietSuccess:true)` → finally `_saving=false`。
- `SaveDirty` 第一步（`:2272`）：`if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();` —— **只要当前格在编辑态就结束其编辑（不看该格是否脏/是否刚进入）**。
- 网格 `EditMode` 未显式设置（grep 无赋值）→ 默认 `EditOnKeystrokeOrF2`（官方属性文档原文："The default is EditOnKeystrokeOrF2"，见 §9-[4]）：选中格上敲任意字母数字键即开始编辑，且该键替换单元格全部内容。
- `_saving`（`:55`）：只在两处被读——`ScheduleAutoSave` 开头（`:1796`）和 Tick 内（`:1804`）；作用是阻止重入和保存期间再次排队，**不阻止"保存开始时当前格处于编辑态"**。

### 4.2 复现型时序（与 xavier 真机描述对齐）

1. 用户在格 A 编辑并输入 → 每次 `TextChanged` 重启 600ms 计时。
2. 用户快速切到格 B 并进入 B 的文本编辑（双击：`CellDoubleClick` handler `:468-480`，文本格分支 `:476-479` 调 `BeginEdit(true)`（`:478`），编辑框创建、文本**全选**；或 F2，同样全选）。A 在切换瞬间被 grid 提交（`CellValueChanged` 触发，`:432` 又把计时器重启一次 600ms）。
3. **进入 B 编辑（`EditingControlShowing`/BeginEdit）没有任何代码重置或暂停计时器**（`:501-511` 只挂 TextChanged 与右键菜单；而用户若尚未敲键，连 TextChanged 都没有）。
4. 距 A 最后一次事件 600ms 到点（用户操作足够快时，此时 B 刚进入编辑、0 个或 1 个字符）：Tick 发现 `DirtyRows()` 含 A → `_saving=true` → `SaveDirty` → **`:2272` 对 B 调 `EndEdit()`**。
5. 【代码实锤】在 B 正处于编辑态这一前提下，`EndEdit()` 提交并结束 B 的编辑（不再区分 B 是否脏/是否刚进入）：编辑控件被回收，B 回到"单元格选中（非编辑）"状态。此时分两种：
   - B 尚未改动：值不变（不触发 CellValueChanged），但编辑态确实被强制结束；
   - B 已有半截输入：半截值被提交（`EndEdit` 语义），B 还可能因此进入脏集合，被本轮一起写进 EPLAN（`:2274` 的 `DirtyRows()` 在 EndEdit **之后**才算）——即"半截输入落库"的次生污染。
6. 【静态推断，机制确定】`SaveDirty` 全程在 UI 线程同步执行（写回+回读多轮 EPLAN 互操作，见 A12），期间用户继续敲键：键消息在 Win32 队列排队，保存返回后按"非编辑态选中格"规则处理。默认 `EditOnKeystrokeOrF2` 下，首字母键＝进入编辑并**替换整格内容**，用户"正在编辑的文本"被该字符覆盖 → 输入丢失。`[UNCERTAIN]` 仅在于：EndEdit 后焦点是否 100% 留在 grid 的 B 格（代码未改 CurrentCell、未点别处，按框架语义应留在 B；需真机用焦点采样脚本确认，见 §7-R11）。

### 4.3 为什么现有两道防线都没挡住

- **600ms 防抖**：合并的是"连续编辑"，但"保存 A"与"开始编辑 B"是两个格；防抖锚点在 A 的事件流，切格后 B 的编辑开始不重置锚点，Tick 时 B 的编辑年龄可以是 0–600ms 任意值。打字+切格快于 600ms 是正常操作速度，不是极端手速。
- **`_saving` 标志**：只管 Tick 重入；保存动作本身正是它放行的，`SaveDirty` 内没有任何"当前格在编辑就跳过/延迟"的判断。
- 附带放大因素：自动保存默认开启（`:746`）、保存同步阻塞且耗时不可控（A12），使"被打回选中态"的暴露窗口从一瞬间拉长到整轮写回+回读。

### 4.4 防护方向（只列方向与风险，不定方案）

1. **Tick/SaveDirty 前置守卫**：当前格在编辑（`IsCurrentCellInEditMode` 且 EditingControl 聚焦）时本轮不保存，停止本次 Tick 并短延迟后重试（或等下一次提交事件自然再调度）。风险最低；代价是用户停在同一格持续不离开时保存被推迟——但此时本来也不该打断，且关窗/确定路径仍可走显式 EndEdit 保存。**注意**：守卫只应作用于"自动保存"，手动"应用/确定"（`:1186,1192`）仍需 EndEdit 提交，不能改坏。
2. **自动保存只保存"已提交的脏行"**：`SaveDirty` 增加可选参数，自动保存路径跳过 `:2272` 的强制 EndEdit，脏行集合在 Tick 时已不含未提交格（A 已提交、B 未提交，则只写 A）。风险：要确认"未提交但已脏"的当前格在连续不切换时如何兜底（可在 CellEndEdit 后立即再调度一轮，天然闭合）。
3. **进入新格编辑时重置防抖**：在 `EditingControlShowing` 里 Stop 计时器并等首次提交后重新计时。单独使用只能缩短窗口、不能根治（保存耗时窗口仍在），适合与 1/2 叠加。
4. **保存后恢复编辑现场**：保存前记录 CurrentCell 地址/光标位置，保存后若焦点仍在本窗则 `BeginEdit` 并恢复 SelectionStart。复杂且在半截值语义上容易出错（与方向 5 冲突时更乱），不建议作为首选。
5. **保存中输入保护**：保存期间禁用 grid（Enabled=false 会丢焦点且视觉跳动，不推荐）或吞掉首轮字母键——体验怪异，不推荐。

回归约束（任何方向都必须不破坏）：HANDOFF §4.4 已验证的保存语义——单 UndoStep/单 Transaction 合并一个撤销点（`:2290-2359`）、回读校验与不一致行不更新基线（`:2362-2421`）、多语言/单语言（L___）写回分支（`:2312-2345`）、多语言暂存（`:391-414`）、复选框即时提交（`:374-381`）、最后一格未离开也能保存（`:2272` 存在的初衷）。

---

## 5. C. P2-a 键位静态走查

### 5.1 现状（全部实锤/官方锚定）

- 全文 grep：无 `Keys.Home`、无 `Keys.End`、无 `ProcessDialogKey` 覆写；唯一的键位协商点是 `LineBreakTextBox.EditingControlWantsInputKey`（`:2577-2584`），其中仅对 **Ctrl+Enter** 特判返回 true，其余一律 `base.EditingControlWantsInputKey(...)`。
- .NET 基类 `DataGridViewTextBoxEditingControl.EditingControlWantsInputKey` 的官方开源实现（§9-[5]，release/8.0，174-181 行）：

  ```csharp
  case Keys.Home:
  case Keys.End:
      if (SelectionLength != Text.Length) return true;   // 未全选 → 编辑框自己处理
      break;
  ...
  return !dataGridViewWantsInputKey;                    // 全选 → grid 想要就让给 grid
  ```

  即：**编辑文本被全选时，Home/End 让位给 DataGridView**（grid 用它们做行首/行尾单元格导航）；非全选时编辑框消费。
- 进入编辑时是否全选：
  - 双击文本格：本仓 `CellDoubleClick` handler（`:468-480`）文本格分支显式 `_grid.BeginEdit(true)`（`:478`，selectAll=true）→ 全文全选；
  - F2：默认 `EditOnKeystrokeOrF2` 下 F2 进入也是全选（官方 `PrepareEditingControlForEdit(true)→SelectAll()`，同文件 209-224 行）；
  - 直接敲字母进入：selectAll=false，光标置尾、非全选——此路径下 Home/End **按基类逻辑应正常作用于文本**。
- 结论：【代码实锤】**双击/F2 进入编辑（最常见路径）→ 全选 → 首次 Home/End 落到 grid 导航**，当前编辑被提交并跳到行首/行尾格——与 xavier 真机感受一致。`[UNCERTAIN]` 点：① net472 的基类实现与 release/8.0 源码逐位一致（该逻辑自 .NET 2.0 起未见变更记录，但本次未翻 4.7.2 Reference Source 逐行比对）；② 直接键入进入编辑后 Home/End 行为正常这一推断待真机确认（见 §7-R14 落点矩阵）。

### 5.2 标准修复方向与影响面

- 方向（官方做法，§9-[3] 文档示例即如此）：在 `LineBreakTextBox.EditingControlWantsInputKey` 中对 `(keyData & Keys.KeyCode)` 为 `Home/End` 恒返回 `true`；Shift+Home/End 的 KeyCode 仍是 Home/End，文本选择行为由 TextBox 原生处理，无需额外代码。工作量 S。
- 顺带核对（xavier 要求）：
  - 左右方向键基类有"光标到边界就让位给 grid 换格"的刻意设计（同文件 130-151 行）。建议**保留**：截留后键盘将无法用方向键移出单元格（只能 Tab/Enter），与表格快速导航习惯冲突；真机确认边界行为可接受即可，不建议顺手改。
  - Shift+方向键文本选择、Shift+Home/End：随 Home/End 截留自然正确；但"全选+按右"仍按现有边界规则走，保持一致。
- 影响面：仅编辑态键位协商，不影响非编辑态 grid 的 Home/End 行导航（不编辑时该 override 不参与）、不影响 Ctrl+Enter/¶、Ctrl+J、剪贴板过滤器三条既有键路。回归点：中文 IME 下 Home/End（键码不经 IME 改写，理论上同路径，真机验）。

---

## 6. D. UX 启发式走查（Nielsen 10 + 插件特有交互）

> 分两组：**与本次四问题相关**、**一般发现（不扩张成重构）**。xavier 偏好已作为评价基线：极简紧凑、提示放底部状态栏而非弹窗、框架层 ReadOnly 防复选框误触、右键纯功能名+右列快捷键、行高拖拽用内置。

### 6.1 与本次四问题相关

| 启发式 | 发现 | 证据 | 建议方向 |
|---|---|---|---|
| 错误预防（最高） | P2-b：自动保存静默打断后一格编辑，无任何提示即可致输入丢失 | §4 全链 | 按 §4.4 方向 1/2 修复；修复前可在状态栏于"保存打断编辑"时留痕（至少可观测） |
| 一致性与标准 | P2-a：文本框内 Home/End 不符合所有文本编辑的标准模型 | §5 | 一行 override |
| 系统状态可见性 | 自动保存开/关、未保存计数、复选框操作提示均在底部状态栏，符合偏好 | `:1758-1783, 746-757` | 保持；建议补"保存中…"瞬时态（保存窗口冻结期目前零反馈，A12） |
| 美观与极简 / 响应速度 | P1-b：列宽行高拖拽、排序闪/掉帧，与"极简紧凑表格"体感直接冲突 | §3 A8/A9 | 双缓冲先行 |
| 用户控制与自由 | 自动保存默认开且编辑结束即落库，但"取消"按钮不回滚已自动保存内容——用户对"取消"的模型通常是放弃全部修改 | `:1188-1189` Cancel 直接 Close；自动保存已写 EPLAN | 二选一（轻量为先）：按钮文案改"关闭"并在说明页写清自动保存即落库；或保留"取消"语义但需引入会话级撤销，成本高不推荐 |

### 6.2 一般发现

| # | 启发式 | 发现 | 证据 | 建议 |
|---|---|---|---|---|
| G1 | 错误预防（数据安全） | **X 关窗无未保存拦截**：无 `OnFormClosing/FormClosing` 处理；自动保存**关闭**时，未点"应用"的脏改动随关窗静默丢失（"确定"才会先 SaveDirty，X/"取消"不会） | grep 无 FormClosing；`:1186-1189` | FormClosing 中 `DirtyRows()>0` 时给"保存/不保存/取消"三选（复用现有 MessageBox 模式，频率低、不违背状态栏偏好）；工作量 S |
| G2 | 灵活高效使用 | 复选框必须双击翻转（框架 ReadOnly 防误触，符合既定偏好，状态栏也有提示）；但右键菜单无勾选项，新用户可发现性低 | `:438-451, 1775-1780` | 可在右键菜单按当前列条件追加"勾选/取消勾选"（仅复选框列选中时）；非必须 |
| G3 | 帮助与文档 | "说明"页只有颜色含义与日志路径，未覆盖：¶ 与 Ctrl+Enter、多语言暂存（关掉多语言时译文去哪了）、两种"还原"区别、自动保存即落库、Home/End 等键位 | `:773-781` | 补一页纯操作指南（HANDOFF T6 同向，去技术细节） |
| G4 | 系统真实状态 | 多语言暂存期间新值译文被清空、保存后只落单语言值（设计正确），但格内无"此译文被暂存"视觉提示，用户可能以为译文丢失 | `:389-414` | 暂存格加占位样式或状态栏说明（轻量）；至少进说明页（G3） |
| G5 | 错误提示位置（既有偏好） | 回读不一致/写回失败/转到图形失败/未选中行等仍用模态 `MessageBox`（`:962, 978, 2411, 2439`） | — | 严重写回失败保留弹窗合理（罕见且需用户知晓）；"请先选中一行"这类提示改状态栏，减少打断 |
| G6 | 加载/空状态 | 无选择有 MessageBox（Action 侧 `:191-195`，合理）；但大数量装载无加载态/等待光标（A14） | — | 随 A14 一并解决 |
| G7 | 可访问性/美观 | 状态仅靠黄/绿/灰三色区分，色弱用户无第二通道；行号列跟随着色略有助益 | `:33-38, 1005-1043` | 低优先：可加图案/字重或保持现状（内部工具，xavier 定） |
| G8 | 灵活高效使用 | 双击列头三级排序无可见提示（箭头只在排序后出现），新用户难发现；双击文本格排序与双击编辑在不同区域（列头 vs 格）不冲突 | `:1564-1572` | 可在列头 tooltip 补"双击排序"，低优先 |
| G9 | 用户控制 | Ctrl+滚轮缩放无档位/重置提示；右键"调整列宽"可间接复位，但字体缩放与列宽联动关系不透明 | `:76-105, 448-449` | 说明页补一句即可 |
| G10 | 一致性 `[UNCERTAIN]` | 单击格仅选中、需双击/F2/键入才编辑；对"点进去马上按 Home"的用户心智有影响（也与 P2-b 覆盖语义相关）。改 `EditOnEnter` 会改变键盘导航（每次方向移动都进编辑、首键覆盖语义变化） | `:330-369` 未设 EditMode | 不在本次动；仅作为 P2 修复后真机观察项，若误触覆盖仍有报告再评估 |

---

## 7. 真机实测清单（交 xavier）

> 前置：HANDOFF §8.1-A 的 T1–T8 继续有效，此处不重复；以下为性能/UX 专项。所有项记录插件版本号（API 模块对话框"装配名称"列 Version 或 OnInit 日志全名）+ 自动保存开关状态 + 文本数量/语言数/来源（框选 or 页导航器）。

### 7.1 性能（P1-a / P1-b）

- [ ] **R1 开窗耗时**：分别用 100 / 500 / 2000 文本框选集执行 Action；记录触发到窗口可交互的墙钟秒表时间（埋点版看日志分段：收集/装载/默认序重建/自动列宽）。主观卡顿分级：1 顺滑 / 2 可感可接受 / 3 明显迟滞 / 4 像卡死 / 5 超时无响应。
- [ ] **R2 大页枚举放大**：页导航器选一个放置对象 2000+、文本少量的页开窗，对照 R1 同级文本数，验证 A1。
- [ ] **R3 排序帧率**：500 / 2000 行下双击列头排序三次，录屏观察：有无白闪、重建耗时、排序后滚动是否顺畅。
- [ ] **R4 拖拽闪烁**：按住列宽分隔线左右拖动 2 秒、行高分隔线拖动 2 秒（500 行、显示原值开/关各一次），记录有无闪烁/撕裂与主观分级；开"单元格聚焦"与关闭各试一次（验证 A7）。
- [ ] **R4b 焦点移动成本**：2000 行下按住方向键在行间连续移动，观察掉帧；埋点版记录每次 CurrentCellChanged 的脏扫耗时与每秒 CellFormatting 次数。
- [ ] **R4c 双缓冲对照**（修复后）：同 R4 操作，确认闪烁消失且无新增副作用（远程桌面/双屏下文字发虚等 `[UNCERTAIN]`）。
- [ ] **R5 保存耗时与冻结**：制造 1、20、200 个脏行后触发自动保存，记录写回/回读分段耗时与窗口冻结主观分级；多语言项目（3、6 语言）各一次。
- [ ] **R6 开关与缩放**：连续切换"显示原值/显示源语言/显示结构/显示坐标"各 3 次、Ctrl+滚轮缩放 5 档，记录卡顿分级。
- [ ] **R7 日志影响**：同一 2000 行场景，在日志目录位于本地盘时开窗一次；如条件允许，把 `$(MD_SCRIPTS)\.log` 指向网络盘/杀软受控目录再试一次（A11 放大效应），仅对比不做结论。

### 7.2 P2-b 竞态（最高优先）

- [ ] **R8 标准复现**：自动保存开；格 A 输入文本 → 600ms 内**双击**格 B 进入编辑（不敲键）→ 等待自动保存（日志出现"自动保存触发"`TextBatchEditForm.cs:1809`）→ 观察 B 是否被打回选中态 → 敲字母，记录 B 内容是否被覆盖及最终落库值（关窗后重开或图面核对）。
- [ ] **R9 节拍矩阵**：进入 B 后分别在立即敲 1 键、敲 3 键、只按方向键三种情况下于 ~600ms 窗口等待 Tick，记录每种序列 B 的最终值与是否半截落库（§4.2 次生污染）。
- [ ] **R10 进入方式矩阵**：B 的进入方式＝双击 / F2 / 单击后键入 / Tab / 方向键移入后键入，各跑一遍 R8。
- [ ] **R11 焦点实证**：Tick 保存刚结束瞬间用 `scripts/monitor-focus.ps1` 或 `check-kbd-focus.ps1` 采样真实焦点窗口/控件（配合 AGENT.md 焦点三件套，先切 ENG IME），确认焦点是否留在 grid 当前格（验证 §4.2 唯一推断点）。
- [ ] **R12 防线边界**：自动保存关时同序列操作，确认不发生打断（阴性对照）；手动"应用/确定"在格 B 编辑中触发时仍能正确提交 B 与 A（守卫修复后的必测回归）。
- [ ] **R13 多语言回归**：守卫修复后，改 A 的译文→快速进 B→自动保存，验证只落 A、B 不丢、撤销栈只有一个"文本批量编辑"撤销点、Ctrl+Z 一次回退全部（HANDOFF §5 已验证语义不回退）。

### 7.3 P2-a 键位

- [ ] **R14 落点矩阵**：分别用双击、F2、直接键入三种方式进入文本编辑，按 Home / End / Shift+Home / Shift+End / 左（光标在最左）/ 右（光标在最右），记录每键作用对象（文本光标 or 单元格导航）；修复后复测，期望编辑态全部落在文本框，非编辑态保留 grid 导航。
- [ ] **R15 中文 IME**：0804 中文输入态下重复 R14 编辑态部分；含 ¶ 的多行单元格内 Home/End 行为记录。
- [ ] **R16 键路回归**：Ctrl+Enter（插 ¶）、Enter（提交下移）、Ctrl+C/X/V 与框选复制、Ctrl+J（转到图形）在键位修复后各验一次。

### 7.4 其他

- [ ] **R17 关窗保护（G1）**：自动保存关，改若干格不点应用：点 X、点"取消"，记录改动是否丢失（现状预期：丢失）；修复后出现三选对话框并逐按钮验证。
- [ ] **R18 自动保存+取消语义（§6.1）**：自动保存开，改 A 等其落库后点"取消"，重开核对 A 是否已变更——记录实际行为供文案决策。

---

## 8. 优先级建议（建议性质，最终 xavier 定）

按"数据安全 > 正确性 > 高频体验 > 一般打磨"：

| 序 | 事项 | 等级 | 工作量 | 理由 |
|---|---|---|---|---|
| 1 | **P2-b 自动保存竞态防护**（§4.4 方向 1+3 或 2+3），含 R8–R13 真机回归 | P0 / 数据安全 | S–M | 静默丢用户输入，无任何提示；自动保存默认开，命中面大 |
| 2 | **G1 关窗未保存拦截** | P0 梯队 / 数据安全 | S | 一行 FormClosing 级别改动，堵第二个静默丢失口 |
| 3 | **P1-b 双缓冲**（EditGrid.DoubleBuffered）+ 排序/缩放重绘挂起（A8/A9/A13） | P1 / 高频体验 | S | 成本最低、主观收益最直接，先拿确定性收益 |
| 4 | **P1-a 装载与绘制组合优化**：消第二遍装载（A3）、装载挂起重绘（A2）、行首宽策略（A4）、CellFormatting 脏状态缓存+失效收窄（A6/A7）、日志降级（A11）、加载态（A14） | P1–P2 / 高频体验 | M（拆小步，每步真机埋点对照） | 先用 §3.2 埋点定位真实占比再排序各小项；VirtualMode（A10）押后 |
| 5 | **P2-a Home/End 截留** | P2 / 正确性 | S | 改动小、行为标准化；排在性能后仅因影响面小于数据安全 |
| 6 | 枚举路径 A1 与保存回读 A12 的深度优化 | P2 / 性能 | M | 依赖埋点数据与 API 调研，不急着动 |
| 7 | §6.2 一般 UX（G2–G10） | P3 / 打磨 | S 为主 | 说明页补充（G3/G4/G9）可与下一次文案更新顺带做 |

立项纪律建议：第 1、2 项属数据安全，修复后建议先合一个最小 fix（hotfix 路径按仓库编排约定走），不与性能改造混在同一分支；性能各项拆成可独立埋点验证的小步，避免一次性重写网格层。

---

## 9. [UNCERTAIN] 与静态局限

1. 所有"慢/闪"的**实际耗时与占比未测**：本文给出的是机制级必然性（如非双缓冲实时拖拽必闪、全表重建必产生一次性开销），量级必须由 §3.2 埋点/Profiler 与 §7 真机清单确定。
2. EPLAN API（`AllPlacements`、`Contents` setter/getter、`DMObjectsFinder`、事务）的真实耗时与**跨线程可用性**未证实；后台线程化方案在核实前视为不可行。
3. `DataGridViewTextBoxEditingControl` 键位逻辑引用的是 .NET 开源仓库 release/8.0 源码；与 net472 框架实现的逐位一致性未用 Reference Source 逐行比对（该逻辑长期稳定，P2-a 仍以真机 R14 为验收依据）。
4. P2-b 中"`EndEdit()` 后焦点留在 B 格、排队按键按选中格规则覆盖"的最后一环为框架+Win32 消息泵语义推断，需 R11 焦点采样实证。
5. 远程桌面/双屏/DPI 缩放在开启双缓冲、`WS_EX_COMPOSITED` 后的表现未知（R4c）。
6. `EditOnEnter` 等交互模式变更的利弊纯静态讨论，未实测（G10）。
7. 本轮未启动 EPLAN、未构建、未运行插件；未评估 HANDOFF T1–T8 中需真机才能判定的条目。

## 10. 参考链接（均为微软官方 / .NET 官方开源，2026-09-18 可访问）

1. Best Practices for Scaling the Windows Forms DataGridView Control — <https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/best-practices-for-scaling-the-windows-forms-datagridview-control>（cell styles / automatic resizing / selected cells / shared rows / unshared rows 各节，页面更新 2025-05-07）
2. Performance Tuning in the Windows Forms DataGridView Control（节点页，含 Virtual Mode 与 JIT data loading 入口）— <https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/performance-tuning-in-the-windows-forms-datagridview-control>
3. `IDataGridViewEditingControl.EditingControlWantsInputKey` API 文档（含官方 Home/End/方向键 return true 示例）— <https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.idatagridvieweditingcontrol.editingcontrolwantsinputkey?view=windowsdesktop-9.0>
4. `DataGridView.EditMode` 属性（默认值 `EditOnKeystrokeOrF2` 原文）— <https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.datagridview.editmode?view=windowsdesktop-9.0>；枚举语义：<https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.datagridvieweditmode?view=windowsdesktop-9.0>
5. .NET WinForms 官方源码 `DataGridViewTextBoxEditingControl.cs`（release/8.0，EditingControlWantsInputKey 的 Home/End 全选让位逻辑 126-209 行；PrepareEditingControlForEdit 209-224 行）— <https://github.com/dotnet/winforms/blob/release/8.0/src/System.Windows.Forms/src/System/Windows/Forms/DataGridViewTextBoxEditingControl.cs>
6. Virtual Mode 文档（A10 远期方向）— <https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/virtual-mode-in-the-windows-forms-datagridview-control>
