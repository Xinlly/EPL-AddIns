# P1 详细任务书：分块加载 + 进度 + 可取消（方案 B，bound 网格）

- 状态：**待实施（仅任务书）**；基线 develop = `e6ce98f`（已含 P0 `b8412fd`、250ms 固定节拍自动保存、排序原地重排、动态版本号修复）。
- 上位设计：`docs/design/text-batch-edit/large-load-virtualmode.md` §5（分块/进度/取消）、§1 TL;DR、§10 回归、§11 线程探针。
- 本期边界：**不做 VirtualMode（P2）、不做 live resize（P3）、不改写回 EPLAN 逻辑、不改键路径**。bound（普通绑定）网格保留，只把"一次性同步装载"改为"UI 主线程分块、块间让出消息泵、可取消、带遮罩进度"。
- 线程纪律（§3 决定性结论）：EPLAN API 不支持主线程外调用。本期全部 EPLAN 读取仍在 UI（=EPLAN 主线程）线程，**仅在块间让出消息泵**，不引入后台 API 读取。方案 A（后台线程裸读）能否采用以 §11 真机探针 A/B 结论为准，**不在本期实现**。

---

## 0. 目标与成功标准

大选择集（5000 / 10000 文本）开窗时：

1. 点击菜单后**窗口立即出现并显示遮罩进度**（已处理 n/总数、百分比、取消按钮），点击到遮罩出现的延迟只含"冻结名单 + 语言读取"，不含逐对象 Contents。
2. 加载期间 **EPLAN 主窗口可拖动、可切页、不白屏、不出现"应用程序无响应"**；本窗遮罩在、取消可用。
3. 可中途**取消**：点取消即关窗，无半写对象、无残留锁、无残留自动保存定时器；取消后再次触发动作能正常开窗。
4. 加载完成后的终态与现状（P0 后）**逐字段一致**：默认序（结构管理顺序 → X↑ → Y↓ → 开窗序号）、序号盖章 1..n、行号、复选框/stash、baseline/initial、可编辑性、列可见性/列宽、行首宽、排序状态（`_sortCol=-1/_sortDir=0`）、选区清空。
5. `ReloadSelection`（窗常驻时刷新选择集）走同一分块管线，体验一致；未变化早退、脏行三态询问语义不变。
6. Debug + Release 均 0 警告 0 错误；R1/R6/R7/R8 真机回归通过。

**非目标**：不保证总耗时大幅下降（EPLAN 读取总量不变，方案 B 只消除"冻住"、把不可中断长任务切成可泵消息的小块）；真正的总量优化属 P2/后续。

---

## 1. 现状锚点（基线 e6ce98f，file:行 已逐一实读）

### 1.1 Action 侧：名单已在 Show 前冻结（本期基本零改）

`TextBatchEditAction.cs`：
- `CollectTexts(out project, out sourceDesc)`：`:59-139`，在 `Execute` 内、窗体 `Show` 之前同步执行，返回 `List<TextBase>`。
  - 图形路径：遍历 `new SelectionSet().Selection`（`:65-76`），只收集 **TextBase 引用**，不读 Contents。
  - 页路径：遍历所选页 `page.AllPlacements`（`:108-125`）过滤 `is TextBase`，同样只收集引用（一次原生数组封送 + 遍历，不读每对象 Contents/语言串）。
- `Execute`：`:194-262`。读源语言 `:218`、项目语言 `:219-230`（两次 project settings 读取，廉价）→ 已开窗则 `ReloadSelection`（`:239`）→ 否则 `new TextBatchEditForm(...)`（`:244`）→ `form.Show(owner)`（`:251`）。
- 单例字段 `_openForm`：`:20`，`FormClosed` 里 Dispose 并置 null（`:246-250`）。

> 结论（回应上位设计 §5.5）：**名单冻结在现状里已经成立**——昂贵的逐对象读取发生在窗内 `LoadRows`，不在 Action 收集。P1 不需要把 SelectionSet 构造推迟到"窗先 Show 之后首块"，只需保证 `new Form(...)` 构造函数不再同步跑 `LoadRows`（见 §2），点击到窗出现之间仅剩 CollectTexts + 语言 settings，符合 §5.5"通常便宜一个数量级"的判断。
> [UNCERTAIN] 整页 2000+ 放置对象时 `AllPlacements` 遍历本身的耗时未实测；若真机发现这一步也可感，降级为点击后立即弹一个轻量"正在准备…"无按钮提示（§5.5 备选），本期默认不做。

### 1.2 Form 侧：P0 两遍装载结构（本期改造对象）

`TextBatchEditForm.cs`（2989 行）：
- 构造函数：`:249-279`。字段赋值 → `BuildGrid()`（:265）→ `BuildTabs()`（:266）→ `BuildBottomBar()`（:267）→ **`LoadRows()`（:268，当前同步阻塞）** → `UpdateApplyEnabled()`（:269）→ 注册三个消息过滤器（:271-276）→ `Shown += UpdateColumnVisibility`（:278）。
- `LoadRows()`：`:1588-1790`，P0 后为两遍：
  - 准备：清五个并排列表 `_baseline/_initial/_stash/_origIndex/_meta`（:1601-1605）→ `BuildRankMaps()`（:1607）。
  - **第一遍（昂贵、EPLAN 读）**：`:1616-1731`，逐 `TextBase` 读 `Contents`/语言列表/各语言串（:1628-1669）、`BuildMeta(t)`（:1676，含结构/坐标，另见 :1444-1461），组装 `object?[] vals` 与 baseline/initial，产出 `List<SortView> records`（:1721-1730）。
  - 默认序排序：`DefaultOrdered(records).ToList()`（:1735）。
  - **第二遍（建行）**：`:1739-1767`，`_syncing=true` → `_grid.Rows.Clear()`（:1740）→ 逐行 `Rows.Add()` + 逐格赋值 + `SetRowEditable`（:1748-1753），并按同一顺序写五个并排列表（:1756-1761）→ finally `_syncing=false`。
  - 收尾：`ClearSelection()`/`CurrentCell=null`（:1771-1772）、`_sortCol=-1/_sortDir=0`（:1776-1777）、`EnsureRowHeadersWidth(n)`（:1780）、PERF 汇总日志（:1782-1789）。
- 不变式（多处依赖，务必保持）：**显示行 i 恒对应 `_texts[i]/_baseline[i]/_initial[i]/_stash[i]/_origIndex[i]/_meta[i]`**（注释 :1738；SaveDirty/排序/自动保存/转到图形均按此下标）。
- `SortView`：`:1803-1813`（Values/Height/T/Base/Init/Stash/Orig/Meta）。`DefaultOrdered`：比较器定义（PlantRank→PlaceRank→LocationRank→X↑→Y↓→Orig）。
- `BuildRankMaps()`：`:1374` 起（三段结构树，量与 n 无关，装载前一次性完成）。
- 行首宽 `EnsureRowHeadersWidth(n)`：`:133` 起（O(1) 按位数设固定宽），WM_SETREDRAW helper `:116` 附近。
- 列可见性/列宽：`UpdateColumnVisibility()`：`:947-976`（末尾调 `AutoFitColumns()` :975）；`AutoFitColumns()`：`:1005` 起，P0 已改 300 行采样（`maxSampleRows=300` :1014）。
- 网格配置：`BuildGrid()`：`:436-476`（bound、非 VirtualMode；行首宽 `DisableResizing` :449；复选框即时提交 :481-488；`CellValueChanged` 联动 :489 起，`_syncing` 守卫 :491）。

### 1.3 接缝方法

- **250ms 固定节拍自动保存**：`AutoSaveCadenceMs=250`（:117）；`ScheduleAutoSave()`：`:2051-2110`（节拍常驻，输入只上档；Tick 内取 `DirtyRows()`，跳过编辑中行，`SaveDirty(...,background:true)` :2087，失败停表）；`DisposeAutoSaveTimer()`：`:2112-2115`；开关切换 :2038 附近。
- **关窗三态**：`OnFormClosing`：`:281-303`（仅 UserClosing 拦截；脏行+未提交编辑计数；是=SaveDirty 失败则 `e.Cancel=true` 留窗）；`OnFormClosed`：`:305-314`（Dispose 自动保存定时器、摘三个消息过滤器、释放缩放字体）。
- **ReloadSelection**：`:1540-1586`（空选择早退 :1543；DBID 序列比对未变化早退 :1549-1553；脏行三态 :1558-1566；`langChanged` 重建列 :1577-1580；`DisposeAutoSaveTimer()` :1576 → `LoadRows()` :1581 → `UpdateColumnVisibility()` :1582 → `UpdateApplyEnabled()` :1583）。
- **复选框 / stash**：`CellValueChanged` 多语言联动 :489-540（SetRowEditable、stash 清空/写回，引用 `_stash[e.RowIndex]` :498-500）；`SetRowEditable` :1047 起（旧锚，grep 定位）。
- **转到图形**：`GoToGraphic`：`:1104` 起（按 `SelectedCells` 行号映射 `_texts[row]`，注释 :1102 已声明"显示行 i 恒对应 _texts[i]"）。
- 排序：`ApplySort`：`:1815` 起（原地重排 + WM_SETREDRAW；本期不改排序逻辑）。

---

## 2. 设计：分块装载状态机（方案 B）

### 2.1 构造函数瘦身，装载后移到 Shown

- 构造函数 `:268` 的同步 `LoadRows()` 移除；改为：
  1. `BuildGrid/BuildTabs/BuildBottomBar` 照常（建立空网格与列；不 Add 数据行）。
  2. 建好遮罩 UI（默认隐藏）。
  3. 注册消息过滤器保持。
  4. `Shown` 事件里：先 `UpdateColumnVisibility()`（空表上跑，P0 采样列宽对 0 行安全；列可见性先就位），再 `await LoadRowsChunkedAsync(initial:true)`（见 §2.4）。
- 关键：`form.Show(owner)`（Action.cs :251）后窗立即绘制空网格 + 遮罩，随后 Shown 启动分块循环；点击到窗出现不再被 10000 行装载阻塞。
- **不把构造函数改成 async**（WinForms 设计器/事件订阅简单起见）；用 `async void OnShown` 或 `Shown += async (_,_) => {...}`（本项目本就用 lambda 订阅 Shown :278）。`async void` 仅用于事件处理器，内部 try/catch 兜底，异常进错误态（§5），不外泄到 EPLAN。

### 2.2 遮罩进度 UI（窗内自绘覆盖层）

- 一个覆盖 grid 客户区的 `Panel`（Dock=Fill 或置于 grid 之上，`BringToFront()`），含：
  - 标题文字（"正在装载文本…"）、`ProgressBar`（`Style=Blocks`，设 Maximum=总数 n）、标签 `已处理 k / n（p%）`、`取消`按钮。
  - 遮罩可见期间禁用底层 grid 交互（遮罩本身拦截鼠标即可；不必改 grid.Enabled 以免样式闪烁，二选一由实现定）。
- 进度数字**只在块边界更新**（每块一次），不在每行更新（避免高频 UI 刷新抵消收益）。
- 取消：`_loadCancelRequested = true`（由取消按钮在 UI 线程置位）；不在块中途强中断 EPLAN 调用，在**下一块边界**检查并退出（§3）。
- 遮罩在加载完成后隐藏并可 `Dispose()`；取消时随窗关闭一起销毁。

### 2.3 分块与 33ms 预算自适应

- 两个可配常量（放类内 const，便于真机调）：
  - `LoadChunkTargetMs = 33`（单块目标预算，约两帧，保证块间泵一次消息后主窗不白屏）。
  - `LoadChunkMinRows = 50`、`LoadChunkMaxRows = 800`（自适应夹取上下限）；初始块 200（对齐上位设计 §9 P1 "200 行/块"）。
- 自适应：用 `Stopwatch` 计每个"读块"耗时；下一块行数 = 夹取(当前行数 × 33 / 上一块实际ms)。EPLAN 读取段通常比建行段贵，读段用更小块也可接受——以实测单块墙钟不超 ~50ms（含一次泵）为准。
- 读段与建行段都按块推进（见 §2.4），每段块末都让一次泵。

### 2.4 核心循环（保持并排列表不变式）

把 P0 的两遍拆成"分块读 + 一次性排序 + 分块建行"三段：

1. **准备（同步，一次）**：清五个并排列表（同 :1601-1605）→ `BuildRankMaps()`（:1607）→ 置遮罩 Maximum=n、显示遮罩。
2. **分块读（EPLAN 段）**：`for i in 0..n step chunkRead`：对本块各 `t` 执行现第一遍体（:1616-1731 的逐行体，**原样搬入一个 `ReadRecord(i,t)` 辅助方法，不改读取/兜底语义**），追加到 `records`；块末更新进度（读到的计数）、检查取消、`await YieldToMessageLoop()`。
   - 注意：进度在"读"阶段以已处理对象数推进；总行数已知（`_texts.Count`），百分比可用。
3. **默认序排序（一次，同步）**：`records = DefaultOrdered(records).ToList()`（同 :1735；O(n log n) 纯内存，通常远小于读取，不切块；10000 行排序实测若也超预算，再考虑 [UNCERTAIN] 分块，本期不做）。
4. **分块建行（网格段）**：`_syncing=true` → `_grid.Rows.Clear()`（初始为空，Reload 时清旧）；`for i in 0..n step chunkGrid`：本块各行执行现第二遍体（:1743-1762：盖章、Rows.Add、逐格赋值、行头、SetRowEditable、按同序 Add 五个并排列表）；块末更新进度（可复用同一进度条，读+建行可分两段显示或用统一 k，实现定，须能反映"还在建行"）、`SetGridRedraw` 策略见下、检查取消、`await YieldToMessageLoop()`。
   - **重绘策略**：建行循环期间沿用排序已有的 `SetGridRedraw(false)`（WM_SETREDRAW 0，:116 helper）包住整个建行段，结束后 `SetGridRedraw(true)` + 一次 `Invalidate`（遮罩在最上层，本就盖住 grid；挂起重绘可进一步省布局）。务必 try/finally 保证取消/异常路径也恢复 REDRAW(1)（沿用 ApplySort :1900-1923 的范式）。
   - **不变式**：并排列表只在对应行真正 `Rows.Add` 成功后按同一 i 追加；任意块末（含取消点）`_grid.Rows.Count == _texts.Count == _baseline.Count == ... `（针对已加入的前缀），绝不让并排列表与网格行错位。
5. **收尾（仅在未取消且完整跑完时执行，同 :1769-1789）**：`ClearSelection/CurrentCell=null`、`_sortCol=-1/_sortDir=0`、`EnsureRowHeadersWidth(n)`、PERF 汇总、`UpdateApplyEnabled()`、列可见性已在 Shown 先行（Reload 路径在 :1582 已调用）→ 隐藏遮罩。
6. 进度达到 100% 后再隐藏遮罩，避免露出半成品一瞬。

### 2.5 块间如何让出消息泵（net472 可行写法）

- 采用 **`await Task.Yield()`（经 `ConfigureAwait(true)`，回到捕获的 UI 同步上下文）** 作为主要让出原语：
  - WinForms `SynchronizationContext.Current` 在首个 Shown 后可用，`await Task.Yield()` 把后续续体排到消息队列，主窗拖动/切页/重绘/取消点击消息得以插入处理。
  - 仅 await 一次通常只泵"一个续体"；为给 Win32 重绘/输入充分机会，封装 `YieldToMessageLoop()`：`await Task.Yield();`，必要时叠加 `await Task.Delay(1)`（让定时器/输入消息成批处理）。是否需要 Delay(1) 由真机"拖动是否跟手"决定，默认先只 Yield，[UNCERTAIN] 标注。
- **不使用 `Application.DoEvents()`**：它会导致重入（拖动/点击/再次触发 Action/消息过滤器在装载中途进入，造成行列状态重入），是已知反模式。若真机显示 Yield 不够，优先调小块，而非 DoEvents。
- 不引入 `BackgroundWorker`/`Task.Run` 跑 EPLAN API（§3 线程模型禁止；只可用 `Task.Run` 做**纯托管、不碰 EPLAN 对象**的工作，本期没有此类需求）。
- 取消令牌：自研 `volatile bool _loadCancelRequested` 即可（取消按钮 UI 线程置位、循环在 UI 线程读），不必非用 CancellationToken；若用 CancellationTokenSource 亦可，但注意不能把 token 传给 EPLAN 调用，只在块边界查 `IsCancellationRequested`。

### 2.6 取消语义（无半写、无锁残留）

- 本期装载全程**只读 EPLAN**，不调用任何写 API（写仍只在 SaveDirty 路径），故取消天然不产生"半写对象"。
- 取消在块边界生效后：
  1. 恢复 `_syncing=false`、`SetGridRedraw(true)`（try/finally）。
  2. 初始开窗路径：直接 `Close()`（窗内无任何已保存数据；关闭走 `OnFormClosed` 摘过滤器/Dispose 定时器）。注意此时不能触发"有未保存修改"三态——装载未完成不允许编辑（遮罩拦截），且未进入可编辑态，`DirtyRows()` 应为 0；实现上可用 `_loading=true` 标志让 `OnFormClosing` 在装载取消时直接放行（不弹三态）。
  3. Reload 路径：取消 = **保留旧表不动**（更安全、更符合"取消=保持现状"，与 Reload 三态的"取消"一致）：丢弃本次新 `records`/已 Add 的半成品行，恢复显示旧数据。实现要点：Reload 应在**内存里把新数据读完排好**之前不破坏旧网格；进入建行段前才 `Rows.Clear()`。若在建行段取消，需能回滚到旧行——
     - 推荐做法：Reload 时新行先建在**离屏/暂存**不现实（bound 网格行绑定控件）；更简单的策略是 **Reload 也在"读+排序"全部完成后才进入一次性建行**，建行段把 10000 行的 WM_SETREDRAW 挂起，取消按钮在建行段一旦进入则只在"块边界"——但 Clear 后取消会留半空表。
     - **决策（写进实现）**：取消只在**读段块边界**响应；进入建行段后置"正在提交，不可取消"（取消按钮禁用或显示"正在完成…"）。理由：纯内存建行段（10000 行 Add+赋值）在 P0 实测中远短于 EPLAN 读段，且一旦 Clear 必须走完，否则需要额外的旧表快照回滚，复杂且易错。这样 Reload 取消语义干净：读段取消=旧表完整保留；建行段不可取消、快速走完。
  4. 取消后复位 `_loading=false`、`_loadCancelRequested=false`，Action 侧 `_openForm` 初始终止时随 FormClosed 置 null（:249）；Reload 保留时窗继续可用。
- 无锁：本期不取任何 EPLAN `LockingStep`/编辑锁（只读）；确认不新增锁。

### 2.7 加载期自动保存暂停 / 恢复

- 初始开窗：自动保存在用户首次编辑后才由 `ScheduleAutoSave` 上档；装载期遮罩禁止编辑，理论上不会启动。保险：装载开始前置 `_loading=true`，`ScheduleAutoSave()` 开头（:2053 守卫链）增加 `|| _loading` 直接 return；装载完成（非取消）置 false。
- Reload：现状已在重建前 `DisposeAutoSaveTimer()`（:1576）；分块版保持——进入 Reload 先停表，加载成功后**不自动重启**，等下一次 `CellEndEdit/CellValueChanged` 自然上档（与现节拍"无脏行休眠、输入再上档"一致，:2078 注释）。`_loading` 覆盖整个 Reload 分块过程。
- 取消路径同样复位 `_loading`；初始取消随窗关闭 Dispose 定时器；Reload 取消恢复到旧表后定时器保持进入前状态（进入前已 Dispose，则维持停止，等编辑再上档）。

---

## 3. 受影响方法与不改项清单（接缝逐点）

### 3.1 改

| 位置（e6ce98f） | 改动 |
|---|---|
| `TextBatchEditAction.cs :244-251` | **原则上不改**。`new Form(...)` 构造瘦身后，:251 `Show` 立即返回空窗+遮罩；逐对象读取已在窗内 Shown 异步分块。仅当真机证明 CollectTexts 名单本身也可感（整页 AllPlacements）时，再加"正在准备…"轻提示（§1.1 [UNCERTAIN]，本期默认不动）。 |
| 构造函数 `:249-279` | 移除同步 `LoadRows()`（:268）；建遮罩控件；`Shown` 改为先 `UpdateColumnVisibility()` 再启动异步分块装载（一次性，初始路径）。`UpdateApplyEnabled()`（:269）移到装载完成收尾。 |
| `LoadRows()` `:1588-1790` | 重构为 `LoadRowsChunkedAsync(bool initial)` + 三个私有步骤：`ReadRecord(...)`（抽 :1616-1731 逐行体）、默认序排序（保留 :1735 调用）、`CommitRowsChunked(...)`（:1739-1767 分块）。**逐行读取与赋值的字段语义、baseline/stash、¶ 转换、SetRowEditable 一律原样**。保留 PERF 汇总（另加"总墙钟/读段/建行段/块数"分块观测，走 TEMP-PERF，默认关）。 |
| 新增遮罩/状态字段 | `_loading`、`_loadCancelRequested`、遮罩 Panel/ProgressBar/Label/取消按钮、分块常量与自适应块长状态。集中放置、注释标明 P1。 |
| `ReloadSelection()` `:1540-1586` | `LoadRows()` 调用点（:1581）改为 `await LoadRowsChunkedAsync(initial:false)`（方法签名改 `async Task<bool>` 或保留 bool + 内部 await；Action.cs :239 调用处 fire-and-forget 需能容忍异步——见下"Action 接缝"）。三态询问、DBID 早退、langChanged 重建列、DisposeAutoSaveTimer 顺序不变。 |
| `ScheduleAutoSave()` `:2051-2110` | 守卫 :2053 增加 `_loading` 时不上档。Tick 体不改。 |
| `OnFormClosing()` `:281-303` | 装载中（`_loading`）取消关闭直接放行、不弹三态（取消按钮路径 + 用户 X：装载中点 X 也按"取消装载"处理，直接关）。 |
| `SetGridRedraw`/WM_SETREDRAW `:116` 附近 | 建行段复用；确保新增 finally 恢复。不改 helper 本体。 |

**Action 接缝（Reload 异步化）**：`ReloadSelection` 现为同步 `bool`，Action.cs :239 同步调用并立即 `BringToFront`。改异步后：
- 首选：`ReloadSelection` 内部 `async void` 启动分块（对外签名保持 `void`），立即返回、遮罩在已存在的窗内显示；`sameTexts` 早退与三态询问仍在**启动前同步完成**（这些必须即时给答案），只有"装载新数据"段异步。Action.cs :239 无需改（仍 void 调用）。三态中用户选"取消/保存失败"时不启动异步段。
- 这样避免把 async Task 串到 Action.Execute；BringToFront 照旧。

### 3.2 明确不改

- 写回/保存链路：`SaveDirty`（含单 Transaction/单 UndoStep、回读校验、回读不一致留窗）、250ms Tick 主体、`DirtyRows/IsRowDirty/SnapshotRow/CloneRow`。
- 键路径：消息过滤器（Ctrl+Enter/剪贴板/Ctrl+J）、Home/End 截留、GridOnKeyDown、WndProc、IME 处理。
- 排序 `ApplySort`（原地重排 A9 + O(1) 行首宽 A4，已合）逻辑不动；仅保证其与分块装载后的并排列表不变式仍成立。
- 复选框列 ReadOnly/双击翻转/CurrentCellDirtyStateChanged 即时提交；stash 清空/写回语义。
- `GoToGraphic`（按显示行号取 `_texts[row]`；不变式保持即正确）。
- 列宽 P0 采样（AutoFitColumns :1005）、缩放 ZoomGrid、CellFormatting 着色、双缓冲。
- VirtualMode、RowModel、CellValueNeeded/Pushed（全部留 P2）。
- 版本号/发布脚本/main。

---

## 4. 防御：分块期异常与外部变化

1. **单对象读取失败**：沿用 `ReadRecord` 内既有 try/catch（:1626-1674：Contents 失败记 Error、isTranslated/mirrorVal 兜底；BuildMeta 内 Page/坐标各有 try/catch :1441-1456）。**不新增吞异常**：记录失败行，仍产出一条 record（与现状一致，失败对象以空/占位值入表），不在分块层静默丢弃导致 n 与网格行数不符。
2. **对象在加载前被删（失效对象）**：现状读取已 try/catch；分块版保持"失败也占一行"。[UNCERTAIN] 是否要把失效对象整体跳过（改变行数与 R1 序号语义）——本期**不跳过**，与 P0 一致；真机 R10 数据准备含"加载前删一个"，验证其表现为占位行而非崩溃/错位。
3. **循环顶层异常兜底**：`async void Shown`/`Reload` 异步段最外层 try/catch：恢复 `_syncing/SetGridRedraw/_loading`，遮罩切为"装载失败：<ex.Message>，详见日志"+ 一个"关闭"（初始）或"保留旧表"（Reload）按钮；初始路径允许关窗，Reload 保留旧表。**绝不让异常穿过 async void 崩 EPLAN**。
4. **语言集合变化**：仅 Reload 可能。保持现状 `langChanged` 先 `BuildColumns()`（:1579）再装载；列结构在分块读之前就绪。语言 settings 读取在 Action 侧 Show 前完成（:218-230），初始开窗语言集合在装载中不会变。
5. **选择集在装载中被用户改动**：名单 `List<TextBase>` 是 Show 前冻结的引用快照（§1.1），分块读的是这批固定引用；用户后续改图形选择不影响本窗数据源（现状即如此）。**风险**：被冻结的 TextBase 引用若在加载中从图面删除，访问 Contents 抛异常 → 走第 2 点占位/错误路径，不崩。
6. **窗在加载中被属主/EPLAN 关闭**：`_loading` 且非 UserClosing 的关闭（ApplicationExitCall/WindowsShutDown/OwnerFormClosing）照现状 `:281` 一律放行；异步续体在窗 Dispose 后触发的风险——每个 await 续体恢复后先查 `IsDisposed`/`_loading`，Disposed 则直接 return，不再触控件。遮罩/进度控件的回调也要防 IsDisposed。
7. **重入**：加载中遮罩拦截 grid 鼠标键盘；再次触发动作走 `ReloadSelection`（单例 :236），其在 `_loading` 时应直接前置/忽略本次 reload（避免并发两套分块写同一网格）——加守卫：`if (_loading) { BringToFront(); return; }`（或记录"正在装载，请稍候"），不启动第二次。
8. **进度条/取消跨线程**：全部在 UI 线程（await 回 UI 上下文），不涉及 Invoke；不设 `Control.CheckForIllegalCrossThreadCalls` 例外。

---

## 5. Gate / 验收

### 5.1 编译与静态 Gate
- `scripts/build.sh Debug/Release` 与直接 `dotnet build` 两路径（配合已合的动态版本修复 92e5075）均 **0 警告 0 错误**；NTFS 临时副本法，cmp 源码一致后删临时目录；读真实 DLL `scripts/get-dll-version.ps1` 取 FileVersion。
- fresh 只读 reviewer 必审：
  1. **并排列表不变式**：任意取消点/异常点，`Rows.Count` 与 `_texts/_baseline/_initial/_stash/_origIndex/_meta` 计数一致、不错位；保存/排序/转到图形按下标不串行（最高优先级，数据安全）。
  2. `SetGridRedraw(false)` 所有路径（正常/取消/异常）都在 finally 恢复 true；`_syncing` 同样复位。
  3. 块间让出确实回到 UI 上下文（无后台 EPLAN API 调用；无 Application.DoEvents 重入）。
  4. 取消语义：初始=关窗无三态、无残留定时器/过滤器；Reload 读段取消=旧表完整保留；建行段不允许半路取消（按钮禁用）。
  5. 装载中不能编辑/不能触发保存/不能并发 reload；`_loading` 对 ScheduleAutoSave/OnFormClosing/Reload 重入的守卫齐全。
  6. async void 仅事件处理器；所有续体查 IsDisposed；顶层异常兜底不崩 EPLAN。
  7. 终态与 P0 一致（默认序/盖章/行首宽/列宽/排序状态/选区/可编辑性/stash）。
  8. K3：读取/赋值逐行语义零改，只是位置搬到分块方法；未夹带 VirtualMode/排序/写回/键路径改动。

### 5.2 真机回归（xavier；对应上位设计 §10）
- **R1 开窗正确性**：默认序=结构管理顺序→X↑→Y↓；序号 1..n；页列纯页名；标题计数正确。完成态与 P0 版本目视一致。
- **R6 自动保存**：加载完成后 250ms 节拍正常；编辑行不落库不打断；快速连读不饿死；关窗三态在**加载完成后**行为不变；单事务单撤销点。
- **R7 大数据（本期核心）**：5000、10000 行两档：
  - 点菜单→窗+遮罩迅速出现（主观无明显等待）；
  - 加载全程**拖动 EPLAN 主窗跟手、可切页、本窗进度持续推进、无白屏/"无响应"灰化**；
  - 取消按钮：读段中途点取消→初始窗关闭（无报错、可再次正常开窗）；Reload 场景取消→旧表原样保留；
  - 加载完成后滚动/编辑/排序/勾选/自动保存/关窗正常。
- **R8 ReloadSelection**：未变化早退（不弹进度或瞬完）；脏行三态（保存并刷新/丢弃刷新/取消）语义不变；大选择集刷新与首次加载同体验（遮罩、可取消、不白屏）；语言变化重建列正确。
- 数据准备按 §10：单语言/多语言（≥3）各一；普通/多语言/不自动翻译/跨结构混选；含一个加载前删除的失效对象。

### 5.3 5000/10000 计时采集口径
- 开关：`TextBatchEditForm.PerfTrace=true` 与 `TextBatchEditAction.PerfTrace=true`（TEMP-PERF，验收后整块 grep 删除）。
- 指标（沿用现有 PERF 汇总行，新增分块观测）：
  1. **点击→遮罩出现**（Action `Execute:start` 日志 :197 到窗 Shown 首绘）：应只含 CollectTexts + 语言 settings + 空窗构建。
  2. **装载总墙钟**（Shown 启动到 100% 遮罩隐藏）、其中 **EPLAN 读段累计**、**建行段累计**、BuildRankMaps、默认序排序耗时。
  3. **块数 / 自适应最终块行数 / 单块最大墙钟**（验证 33ms 预算；单块明显超 50ms 记录）。
  4. 分档对比：记录 P0（同步版）总耗时与 P1 总耗时——**预期总墙钟可能接近（甚至因泵消息略增），但"最长不可中断连续阻塞"应从秒级降到 ≤约50ms/块**，这是本期核心收益口径，勿用"总耗时下降"验收。
- 同时任务管理器目检：加载期间 EPLAN 不长时间 CPU 打满/无"未响应"标记。

---

## 6. 小步拆分（每步独立可提交、可回滚）

> 每个小步一个 fix 分支 + fresh reviewer + 双 0/0；同改 Form.cs 严格串行，逐步合 develop。

- **Step 0｜spike（建议先做，最高风险点）**：在独立试验分支验证"UI 线程分块 + `await Task.Yield`（必要时 Task.Delay(1)）能否让 EPLAN 主窗在 10000 行装载时保持可拖动/不白屏"。
  - 最小试验：不动产品文件，临时在 LoadRows 读循环每 K 行插一次让出（或写一个独立探针窗），真机量"单块 33ms 时主窗跟手度"。
  - 产出：**确认让出原语（Yield vs Yield+Delay(1)）、可行块行数区间、EPLAN 主窗在消息泵间隙是否真的能处理拖动/重绘**。此为本方案唯一未静态证实的承重假设；若 spike 证明 Yield 不足以让 EPLAN 主窗泵消息（例如 EPLAN 主循环也被本插件同步占用），则方案 B 需重估（可能必须回到探针 A 判定后台线程，或减小预算/改用定时器分块），不应在未证实前铺开 §2 全量改造。
- **Step 1｜遮罩 + 构造瘦身（空路径）**：构造函数移除同步 LoadRows、Shown 先 UpdateColumnVisibility；新增遮罩/进度/取消 UI 与 `_loading/_loadCancelRequested`；Shown 启动一个"假分块"（不读数据，仅演示进度推进/取消关窗）。验证窗秒开、取消关窗清理干净。不含真实数据。
- **Step 2｜分块读 + 同步建行（先不切建行）**：抽 `ReadRecord`，读段分块 + 让出泵 + 进度 + 读段取消；读完一次性 `DefaultOrdered`，建行仍一次性（沿用 P0 第二遍，WM_SETREDRAW 包住）。此时"不白屏/可取消读段"主要收益已拿到；验证不变式与 R1。
- **Step 3｜建行分块 + 自适应块长 + Reload 异步**：建行段切块（块间泵）、33ms 自适应；ReloadSelection 异步化（async void、三态/早退仍同步、读段取消保留旧表、建行段禁取消）；`_loading` 接入自动保存/关窗/重入守卫。验证 R7/R8 全量。
- **Step 4｜硬化与观测**：顶层异常兜底/IsDisposed 续体守卫、TEMP-PERF 分块指标、取消/异常路径 REDRAW 与 _syncing 复位复核；R6 关窗三态在加载后回归。可与 Step 3 合并视改动量。

> 若 Step 0 spike 已在真机证实让出可行，Step 2/3 也可由同一 worker 连续实现后一次 review，但按"小步可回滚"原则建议至少在 Step 1（空路径窗秒开）与 Step 3（Reload/取消/守卫）两处各设一道独立 Gate。

---

## 7. 风险与未决（汇总）

1. **最高风险（spike 先行）**：`Task.Yield` 让出后，EPLAN 自己的主消息循环是否真的趁间隙处理拖动/重绘——静态无法保证，依赖 Win32 消息泵与 EPLAN 宿主实现。若不成立，方案 B"不白屏"目标落空，需凭 §11 探针 A/B 重新定 loader 形态（可能涉 out-of-support 后台线程，由 xavier 裁定）。
2. **Reload 取消的半空表**：通过"读段可取消、建行段禁取消（快速提交）"规避；前提是建行段实测足够短（P0 下纯 Add 段远短于 EPLAN 读段）。10000 行建行段若真机也长到可感，需另做旧表快照/双缓冲暂存（本期不做，回 P2 RowModel 后天然解决）。
3. **async void / 续体生命周期**：窗在加载中关闭导致续体触已 Dispose 控件——以每个续体 IsDisposed 检查 + try/finally 复位守卫；reviewer 重点审。
4. **排序排序耗时**：10000 行 `DefaultOrdered` LINQ 排序假定廉价未实测；若超预算，P1 可接受（纯托管、通常毫秒级），[UNCERTAIN] 留观测。
5. **名单 AllPlacements 遍历耗时**（整大页 2000+ 放置对象）未实测；可感时加"正在准备…"轻提示，本期默认不动 Action。
6. 本任务书所有 file:行 基于 develop `e6ce98f`；实施时以当前树符号为准（方法名锚定），行号允许漂移。
