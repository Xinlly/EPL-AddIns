# TextBatchEdit 大选择集装载根治设计：内存模型 + 分块采集 + DataGridView 虚拟模式

- 状态：设计（只读研究产出，未改任何产品代码；配套真机线程探针见 `scripts/probe/`）
- 基线：`97a8a0d`，分支 `feat/tbe-large-selection-load`
- 适用：EPLAN 2.9（net472 / x64 Add-in），EplApi 13 个 DLL 已在 `references/EplApi/`
- 现象：选中 5000~10000 个文本后首次开窗/刷新选择集，EPLAN 整体冻住数秒~数十秒（UI 线程独占，无进度、不可取消）

> 本文所有行号均指向基线 `97a8a0d` 的 `EA.EplAddIn.TextBatchEdit/TextBatchEditForm.cs`（下简称 Form）/`TextBatchEditAction.cs`（下简称 Action）。

---

## 1. 结论速览（TL;DR）

1. **冻住的根因是 UI 线程（= EPLAN 主线程）上三段同步 O(n)/O(n×语言数) 工作叠加，且实际"装两遍"**：
   ① 逐对象 EPLAN API 只读（Contents/语言串/页结构/坐标）；
   ② 逐行逐格创建约 24 万个 DataGridView Cell 对象并布局；
   ③ `LoadRows()` 后立刻 `ApplyDefaultOrder()` 把全部单元格读出、排序、再逐格写回 + 全量重绘；
   另有 Shown 后 `AutoFitColumns()` 对"可见列×全部行"做 GDI 测量，以及装载期间每行多条同步文件日志。
2. **线程模型（决定性）**：EPLAN 官方明确"在 EPLAN 主线程之外执行 API 代码不推荐、未测试、不在支持范围"，并专门提供 `EplanMainThreadDispatcher` 供后台线程跳回主线程（官方文档原文 + DLL 反射实证 + 社区旁证，见 §3）。
   **因此根治基线 = 方案 B：UI 主线程分块只读采集，每块让出消息泵（不白屏、可取消）+ VirtualMode 网格。**
   方案 A（Task 后台线程裸读 API）是否在本机 2.9 可行，**必须由 `scripts/probe/` 真机探针判定**；探针通过仅表示"技术上能跑"，仍属 out-of-support，是否采用由 xavier 决策。
3. **无论 A/B，VirtualMode 都是必须的**：它消除 ②（不再为 10000 行创建 24 万 Cell 对象），把 CellFormatting/dirty/排序/自动保存/列宽从"每行每格实体"改为"内存模型 + 只服务可见行"。
4. **live resize（列宽/行高拖拽实时预览）只依赖 VirtualMode 只重绘可见行这一性质，与 A/B 无关**，在 B 退化形态下同样成立（§8）。

---

## 2. 阻塞构成：5000/10000 行时 UI 线程热点清单

全部热点都在 EPLAN 主线程（Add-in Action 在 EPLAN GUI 线程执行，窗体构造也在该线程；Form 构造 `:265-280`）。

### 2.1 类别一：EPLAN API 读取（托管→原生 C++ 对象，进程内原生边界，非托管开销不可忽略）

| 热点（每行） | 证据 | 量级 |
|---|---|---|
| `t.Contents`（getter，可能触发延迟加载/复制 MultiLangString） | Form `LoadRows()` `:1578` 起；反射 `TextBase.get_Contents` RVA=0x1845e4（sealed 原生包装类，含原生 Finalize） | O(n) |
| `MultiLangString.GetLanguageList()` + 逐语言 `GetString(lang)` | Form `:1578` 起的逐行循环（多语言文本按 `_projectLangs` 逐个取串） | O(n×L) |
| `BuildMeta(t)`：`t.Page`、`page.Name`、`page.Properties.DESIGNATION_FULLPLANT/FULLPLACEOFINSTALLATION/FULLLOCATION`、`t.Location`（X/Y） | Form `:1443-1483`（`1448/1453/1456-1458/1471-1473`）；每行调用 | O(n)，每次属性读取都是原生边界 |
| 选择集对象收集本身（`SelectionSet.Selection`、选中页走 `AllPlacements` 枚举） | Action（开窗前，同样在 UI 线程同步完成；标题计数 `texts.Count` 直接来自它） | 与选中量线性；本设计不改它，但它属于"开窗前冻结"的一部分，进度窗无法覆盖，需在分期中说明 |
| `TextBase.IsAutomaticallyTranslated`、`DatabaseIdentifier`、`IsValid` 等 | SaveDirty `:2597/2559`、Reload 比对 `:1540` | Reload/保存路径，非首次装载主成本 |

### 2.2 类别二：DataGridView 建行 / 布局 / 测量（纯 WinForms 托管，但量极大）

| 热点 | 证据 | 量级 |
|---|---|---|
| 逐行 `Rows.Add` + 约 24 列单元格对象创建与赋值（复选框/文本 Cell），n=10000 时约 24 万个 Cell | Form `LoadRows()` `:1578-1728`；列结构 `BuildColumns()` `:741-817` | O(n×列)，**实体行模型的核心成本** |
| **装两遍**：`ApplyDefaultOrder()` 把每行全部列值读进 `SortView.Values`（`:1765`），排序后再逐格写回复用行（`:1842-1853`），随后 `Invalidate(DisplayRectangle)+Refresh()`（`:1868-1869`）强制整表同步重绘 | 构造 `:268 LoadRows()`→`:270 ApplyDefaultOrder()`；Reload `:1570-1571`；ApplySort `:1753-1879` | 额外 O(n×列) 读+写+全表 CellFormatting |
| Shown→`UpdateColumnVisibility()`→`AutoFitColumns()`：对每个可见列遍历**所有行**、按换行 split 后逐串 `TextRenderer.MeasureText` | Form `:280`→`:977`→`:1007-1063`（全量遍历在 `:1038-1047`） | O(可见列×n×行内换行数) GDI 调用 |
| `DirtyRows()` 全表扫描：每行 `SnapshotRow()` 从单元格现取标志+逐语言串（`:1891-1904`），逐格字符串比较 | `:1919-1927`；调用点：`UpdateApplyEnabled` `:1932`、自动保存 Tick `:2012`、SaveDirty `:2515`、关窗 `:288`、Reload `:1546` | O(n×L)，且 `UpdateApplyEnabled` 末尾**整表 `Invalidate()`**（`:1939`），一次编辑后重绘全表 |
| CellFormatting 每格经 `CellIsDirty/CellSavedChanged` 现算（再读单元格值+字典比较） | `:1147-1186`、`:1280-1284`；行头上色再调 `RowModificationColor` 遍历所有可编辑列 `:1290-1300` | O(可见格×可编辑列)/帧 |
| 行首宽 | `EnsureRowHeadersWidth()` `:133-141` 已是 O(1)（只测一次位数串） | 已优化，保持 |

### 2.3 类别三：装载期同步文件 IO（本轮新发现，证据确凿，属白捡的 P0 项）

- `AddInLogger.MinLevel = 0`（Debug 常开，`AddInLogger.cs:17`）；`Debug()` 无条件先格式化再在 `lock` 内 `File.AppendAllText`（`AddInLogger.cs:298-301`）。
- `BuildMeta` 每行至少写 1 条 Debug（成功路径 `:1459-1461`，两段 catch 也写 `:1466/:1477`）；10000 行 = 上万次"加锁+打开文件+追加+关闭"，全在 UI 线程。
- LoadRows/ApplySort 的 PERF 汇总日志在 `PerfTrace=false` 时不写，但业务 Debug 日志不受开关控制。

### 2.4 哪些在"EPLAN 主线程"

- Add-in Action 的 `Execute`、所有 WinForms 事件、250ms WinForms Timer Tick 都在 EPLAN GUI/UI 线程（MFC 消息循环所在线程）。
- 类别一（API 读）与类别二（网格）在同一线程串行执行，**两者互相阻塞且阻塞 EPLAN 自身消息泵**——这就是"冻住 EPLAN"而非仅"卡自己窗口"的原因。

---

## 3. 线程模型（决定性问题）

### 3.1 官方文档证据（已抓取原文，2.9 API Help）

`Eplan.EplApi.Base.Internal.EplanMainThreadDispatcher`（官方页面 Remarks 原文）：

> "EplanMainThreadDispatcher can execute some work in the main thread of EPLAN.
> **Executing API code purely of[ff] main Eplan thread is not recommended, i.e. such scenarios were not tested and are out of support.** So the class enables executing API code on a main thread from another thread (i.e. like in case of non-modal dialogs or a background worker)."

公开方法（官方成员表）：
- `CanAccessMainThread()` —— "Allows the user to access the main thread both synchronously and asynchronously."
- `ExecuteInMainThreadSync(...)` / `ExecuteInMainThreadAsync(...)`（多重重载）
- `AddProgressBackgroundWork(progress, work)` —— 官方示例给的就是"后台干活+进度条，但 API 调用包回主线程"的形态。

含义：
1. EPLAN 知道有人在后台线程用 API，但立场是**未测试、不支持**；
2. 官方支持的并行范式是"后台线程做非 API 工作，需要碰 DataModel 时用 dispatcher 封送回主线程"——这对"把逐行 API 读取整体挪到后台"**没有帮助**（逐行封送只会更慢）。

### 3.2 DLL 反射实证（dnfile 只读元数据，未加载混合模式程序集）

- `Eplan.EplApi.Baseu.dll` 存在类型 `Eplan.EplApi.Base.Internal.EplanMainThreadDispatcher`，方法表与官方文档一致：`CanAccessMainThread`、`ExecuteInMainThreadSync`、`ExecuteInMainThreadAsync`×3、`AddProgressBackgroundWork`、`GetMainThreadDispatcher/SetMainThreadDispatcher`；并存在 4 个委托类型 `ExecuteInEplanMainThreadDelegate(1/2/3)`。
- `Eplan.EplApi.DataModelu.dll`：
  - `Eplan.EplApi.DataModel.Graphics.TextBase`（sealed，继承 Placement/StorableObject 链），含原生终结器 `~TextBase/!TextBase`、`get_Contents/set_Contents`、`get_Location/get_Page`（继承）、`IsAutomaticallyTranslated`、`GetDisplayString` 等——典型 **C++ 原生对象的托管包装**。
  - `Eplan.EplApi.DataModel.LockingStep`（仅构造/Dispose/IsSurfaceFilled/Properties），与线程封送无关。
- `Eplan.EplApi.Baseu.dll`：`MultiLangString`（GetString/GetLanguageList/AddString/InternalString/Dispose）、`LanguageList`。
- `Eplan.EplApi.HEServicesu.dll`：`SelectionSet.Selection`、`LockProjectByDefault`、`LockSelectionByDefault`（反射签名与官方文档一致）。
- **[UNCERTAIN]** 未反汇编出 `get_Contents` 内部是否"检测到非主线程就自动封送/抛异常"的直接 IL 证据（dncil 版本 API 不兼容，字节级扫描只确认了 dispatcher 类型存在，未逐方法确认调用面）。这正是探针的核心观测项。

### 3.3 官方 Locking 语义：锁解决的是多用户编辑，不是跨线程通行证

官方《Locking》页（2.9）：
- "locking an object means to set an object reference to a state where it can be edited by the current user/process, whereas no other user/process can edit it"；
- **"the user can always get an object in an un-locked/read-only way, even if it is locked by another user"**；
- `SelectionSet.LockProjectByDefault`/`LockSelectionByDefault` 默认 true，是编辑锁，不是线程亲和性设置。

结论：**只读采集理论上不需要新建 LockingStep**（Action 已经过 SelectionSet 拿到对象引用）；但"只读不需要锁"与"可以在别的线程读"是两回事——后者仍受 §3.1 约束。后台只读不需要、也不应该自行加项目锁（会影响 EPLAN 主会话）。

### 3.4 社区旁证

Suplanus《BackgroundWorker in der EPLAN API》(2018-06)：
- 开篇明确："…geht offiziell nicht. Steht auch so in der Dokumentation."（官方不支持，文档如此写明）；
- 作者在 BackgroundWorker 里直接调 API 遇到"DLL 找不到"等怪异行为，归因于"EPLAN 线程不知道该在哪找"；
- 解法是 `new EplanMainThreadDispatcher().ExecuteInMainThreadSync(o => { ...API...; return null; }, null);` 跳回主线程。

### 3.5 两种根治形态与取舍

| | 方案 A：后台线程只读采集 + VirtualMode | 方案 B（**推荐基线**）：UI 线程分块采集 + 让泵 + VirtualMode |
|---|---|---|
| EPLAN API 调用线程 | Task/线程池 | EPLAN 主线程（分块，每块间 `await Task.Yield()` 让消息泵） |
| 官方立场 | **out-of-support，未测试**（§3.1） | 完全在支持范围内（本质就是非模态窗+让出消息循环，dispatcher 文档明确点名该场景） |
| 冻结/白屏 | 采集期间 UI 完全流畅（纯 API 读不碰 UI） | 不冻结：每块（建议 200 行/块，≈数十 ms）让一次泵，鼠标/重绘/取消可响应；总耗时不变或略增 |
| 风险 | 原生层线程亲和、延迟加载、内部缓存/句柄表竞争，可能抛异常/错值/崩溃，且不同 2.9.x 小版本行为可能不同；出问题 EPLAN 支持不受理 | 无新增线程模型风险；风险仅在分块期间选择集/对象被外部改动（用 `IsValid` 校验，见 §5.4） |
| 写回/锁 | 写回仍必须回主线程 | 天然在主线程，现有 UndoStep+Transaction 保存路径（`:2549-2622`）不变 |
| 适用判据 | **仅当探针在目标机连续多轮全绿**（见 §11）且 xavier 书面接受 out-of-support 风险 | 默认 |

**A/B 判据（探针输出决定）**：
- 后台线程读 N=500/5000/全量 均无异常、值与主线程一致、`CanAccessMainThread=False`、连续 3 轮后主线程复读不崩、EPLAN 操作正常 → A "技术可行（out-of-support）"；
- 任一项失败或行为怪异 → 确定走 B。
- 即使 A 可行，也建议先落地 B（无风险收益），A 作为后续可插拔的"采集策略"可选项（loader 接口预留，见 §4.3）。

---

## 4. 目标架构（A/B 共用）

### 4.1 单一内存行模型 `RowModel`

用一个按"开窗原始序"排列的 `List<RowModel>` 取代当前 6 个并排列表：
`_texts`(`:16`)、`_baseline`(`:28`)、`_initial`(`:30`)、`_stash`(`:32`)、`_origIndex`(`:102`)、`_meta`(`:103`)。

```
RowModel
  int OrigIndex                 // 开窗原始下标（默认序 tiebreaker，同现 Orig）
  TextBase T                    // EPLAN 对象引用（只在 UI 线程触碰；后台形态仅 loader 内短暂使用）
  RowMeta Meta                  // 页名/三段结构/X/Y/三个 rank（迁移现 RowMeta :193-205）
  RowState Initial              // 开窗值（绿/还原用，迁移 :2836-2841）
  RowState Baseline             // 已保存基线（黄/dirty 用）
  RowState Current              // 编辑工作值：Multilang/NoAuto/V[lang]（¶ 显示格式，语义同现网格值）
  Dictionary<lang,string> Stash // 多语言暂存（迁移 :32）
  bool Dirty                    // Current != Baseline（增量维护，不再逐格扫）
  double Height = RowTemplate.Height  // 行高（live resize 持久化/排序携带）
```

- 排序不再搬模型，只维护 `int[] _order`（显示行 i → 模型下标），数据层排序，见 §7.5。
- 现 `SortView`（`:1741-1751`）承担的"搬行容器"删除；排序比较器直接读 `RowModel.Meta/Current`。

### 4.2 网格配置变化（BuildGrid `:438-478`）

- `VirtualMode = true`；`RowCount` 唯一数据源（装载中=已采集数，完成=n）。
- 不再 `Rows.Add`；列结构 `BuildColumns()` 与列常量（`:207-226`）保持不变。
- 行号不再写单元格/HeaderCell 实体：`RowPostPaint`（或 CellPainting 的行首分支，现 `:1193` 已有自绘挂点）按 `e.RowIndex+1` 自绘，颜色逻辑沿用 `RowModificationColor` 但改读模型。
- 逐格 ReadOnly：虚拟模式不能逐格设置实体 Cell.ReadOnly；改为 `CellBeginEdit` 中按 `RowModel.Current.Multilang` 与列判定 `e.Cancel=true`（替换 `SetRowEditable()` `:1069-1079`）。
- 复选框列保持列级 ReadOnly（禁单击/空格翻转，`:795/:797`），双击翻转路径不变但写模型（§7.3）。

### 4.3 Loader 接口（预留 A/B 切换）

```
interface IRowLoader {
  Task<LoadResult> LoadAsync(IProgress<LoadProgress> p, CancellationToken ct);
}
```
- `ChunkedUiThreadLoader`（方案 B，默认）：UI 线程每块读 200 个对象的全部 API 字段 → 产出纯 POCO（字符串/double/bool，不含 EplApi 对象逃逸出块语义），`await Task.Yield()` 让泵。
  注意 `TextBase` 引用仍存在模型里（保存/转到图形要用），但跨块只在 UI 线程解引用。
- `BackgroundApiLoader`（方案 A，探针通过后可选）：`Task.Run` 内只读，结果 POCO 回 UI 线程组装模型；`TextBase` 句柄在 UI 线程重新校验后持有。
- 两种 loader 的产物、进度、取消、VirtualMode 绑定完全相同。

---

## 5. 分块、进度、取消（方案 B 细节）

### 5.1 开窗流程（替换现构造函数内 `:265-280` 的同步三连）

1. 构造：BuildGrid/BuildColumns/BuildTabs/BuildBottomBar + 消息过滤器（保持现顺序），grid `VirtualMode=true, RowCount=0`。
2. 立即 `Show()`：窗体 <100ms 出现，编辑页显示加载遮罩（面板盖在 grid 上）：`正在读取选中文本 1234/10000 …` + ProgressBar + 【取消】按钮；底部栏/开关可见但保存类按钮禁用。
3. Shown 后启动 loader（n 在开窗前已知，标题在首块后可定，先显示"选中 N 个文本"）。
4. 每块完成：追加模型 → 若当前排序状态为默认则直接追加到 `_order` 尾部（采集即默认序，见 §6）→ `RowCount = _models.Count`（可见区即时增长，用户可立刻滚/看已加载行；列宽在首批后做一次采样测量，见 §7.6）。
5. 完成：撤遮罩、`RowCount=n`、焦点落首行、开启自动保存节拍（默认开，`:872`）、状态"已全部保存"。
6. 失败/对象失效：该 meta 行允许字段留空（沿用 BuildMeta 两段 try-catch 的容错语义 `:1446-1478`），在状态栏汇总"x 个对象读取异常已留空"。

### 5.2 分块大小与节流

- 建议块大小 **200 行/块**（语言数多、页结构属性慢时按"块预算 33ms"自适应：块内 Stopwatch 超 25ms 即收尾本块）。
- 每块后 `await Task.Yield()`（至少让一次 WM_PAINT/输入通过）；进度更新本身限频（≥100ms 或块边界）。
- 不用 `Application.DoEvents()`：可重入。分块用 `async/await`（net472 已由产品使用，无需加包）。

### 5.3 取消语义

- 【取消】→ `CancellationTokenSource.Cancel()`，块边界生效。
- **取消后直接关窗**（不做"空表可编辑"状态：空表没有可保存对象，且 Reload 语义复杂，价值为零）。关闭走现 `OnFormClosing` 路径（`:283`），但加载中的模型没有用户改动，不弹三态询问（新增 `_loading=true` 短路）。

### 5.4 ReloadSelection（刷新选择集，`:1529-1576`）

- 走同一 loader + 遮罩 + 取消；选择集未变化的早退（`:1538-1542`）保留；有脏行的三态询问（`:1547-1555`）保留且仍在启动加载前弹窗。
- 语言集合变化→重建列（`:1566-1569`）后再加载。
- 分块期间对象被外部删除：`TextBase.IsValid`（现保存路径已用 `:2559/:2628`）为 false 的行标记失效行（只读+灰+提示），不崩溃；失效行不进保存。
- 重新加载期间禁用自动保存 Tick，完成后按开关状态恢复。

### 5.5 选择集收集在开窗前的问题（Action 侧）

`SelectionSet.Selection`/整页 `AllPlacements` 枚举发生在 Action `Execute` 内、窗体 Show 之前，B 方案的窗内遮罩盖不住这段。处理：
- 该段通常是一次原生数组封送，比逐行 Contents 便宜一个量级，但 10000 行/整大页仍可能有可感延迟；
- P1 先把 Action 侧收集也搬到"窗先 Show、首块内取选择集"（SelectionSet 在 UI 线程构造，分块第一块完成），使遮罩从用户点菜单后即出现；
- 若探针显示 SelectionSet 自身不允许这种用法（预期允许，未实测），退化为点击后立即弹一个轻量"正在准备…"无按钮提示。**[UNCERTAIN] 以真机为准。**

---

## 6. 消除"装两遍"

现状：模型按选择集顺序建行，`ApplyDefaultOrder()` 再做一次"全列读出→排序→全列写回"（§2.2）。

改法（P0 即可独立于 VirtualMode 先做）：
- `BuildRankMaps()`（`:1362-1378`，三段结构树，量小且与 n 无关）在装载前完成。
- 采集每一行时 `BuildMeta` 已算出三个 rank（`:1480-1482`）；整块采集完，在**内存**里按现 `DefaultOrdered` 规则（`:1882-1888`：PlantRank→PlaceRank→LocationRank→X↑→Y↓→Orig）排序一次，再按该顺序生成 `_order`/绑定。
- 构造函数删除 `:270 ApplyDefaultOrder()`；Reload 删除 `:1571`。`ApplyDefaultOrder()` 保留为"双击表头第三档复位"的入口（`:1735-1737`），但实现改为"把 `_order` 重置为默认序的索引置换"（O(n log n) 纯数据，零网格搬运、零 CellFormatting 风暴）。
- 序号列（ColIndex）在默认序下就是 1..n（CellValueNeeded 返回 `_sortDir==0 ? row+1 : _models[_order[row]].OrigIndex+1` 的现语义，对齐 `:1849-1850`）。

收益：首次装载直接砍掉一整遍 n×列 的读、写和一次全表同步重绘。

---

## 7. VirtualMode 详细设计

### 7.1 CellValueNeeded（读，只发生在可见行 + 少量预取）

按列返回（列索引常量不变，`:207-226`）：
- 信息列：`Meta.PageName/Plant/Place/Location/FormatCoord(X/Y)`；对象类型列：`T.GetType().Name` 的缓存字符串（每类型缓存一次，别每行反射）。
- 原值侧：模型里需要保存一份原值快照 `Orig`（标志+各语言串，¶ 格式）——当前原值侧的值只存在于网格单元格，VM 后必须显式持有：在 `RowState Initial` 之外增加 `RowState Orig`（Initial 与 Orig 开窗时相同；保存后 Initial 不动——与现"绿=相对开窗值"语义一致，对照 `CellSavedChanged :1283-1284`）。
- 新值侧：`Current.Multilang/NoAuto/V[lang]`；文本列与源语言列同值（同步语义见 §7.4）。
- 序号/行号见 §4.2/§6。
- 处理程序只做字段读取和极短分支，**禁止在其中调用 EPLAN API、禁止日志、禁止 MeasureText**。

### 7.2 CellValuePushed（写，O(1) 增量）

- 把值写入 `Current`；与 Baseline 比较更新该格 dirty 标记与行 `Dirty`；维护：
  - `HashSet<int> _dirtyRows`（替代全表 `DirtyRows()`，自动保存/应用/关窗直接取它，O(脏行数)）；
  - 整数 `_dirtyCount`（Apply 按钮 Enable、状态栏"n 处未保存"直接用，删除 `UpdateApplyEnabled` 里的全表扫描 `:1932`）。
- 一次编辑只 `InvalidateCell()` 受影响的 3 处：本格、配对原值格、行头；不再整表 `Invalidate()`（删 `:1939`）。
- `_saving`/`_syncing` 抑制标志语义保留（程序化写回时不重入）。

### 7.3 复选框（多语言/不自动翻译）

- 列保持 ReadOnly（框架层禁单击翻转，`:795/:797` 注释的设计不动）。
- 双击翻转：`CellDoubleClick`/`CellMouseDoubleClick` 现有分支（`:577-589`）改为翻转 `Current.Multilang/NoAuto` 后 `InvalidateCell` + 触发同样的联动。
- `CurrentCellDirtyStateChanged` 立即 `CommitEdit`（`:483-490`）在虚拟模式下行为不变（虚拟复选框编辑仍走同一提交路径；P2 真机重点验证项，见 §10 P2 Gate）。
- 多语言切换联动（现 `CellValueChanged` `:491-526`：SetRowEditable + stash 清空/写回）迁移为 Pushed 内的模型操作：
  - 关闭：非源语言 Current.V 移入 Stash 并从 Current.V 移除（这些行 dirty 状态随之变化）；
  - 开启：Stash 写回 Current.V；
  - 非源语言格可编辑性由 CellBeginEdit 按 `Current.Multilang` 判定。
- `ApplyStateToRow()`（`:2320` 起，粘贴整行/形态重建）改为写模型并丢弃旧 stash（`:2338-2339` 语义保留）。

### 7.4 文本列↔源语言列双向同步

现 `SyncPair()`（`:836-841`）与 CellValueChanged 两支（`:532-538`）：VM 下两列在 Needed 时本就读同一字段（`Current.V[_sourceLang]`），无需互相写单元格；Pushed 任一列都写该同一字段。删除同步事件链，行为等价且少一轮事件。

### 7.5 排序（数据层，零网格搬运）

- 现 ApplySort（`:1753-1879`）的 SortView 抓取/回写两段（`:1765-1776`、`:1826-1853`）整体删除。
- 改为对 `_order`（`int[]`/`List<int>`）用现比较规则排序：默认序比较器读 `Meta.PlantRank/...`（`:1882-1888`）；列排序读 `Current`/Meta（`:1789-1821` 的 # /布尔/坐标/文本分支规则逐条保留）。
- 排序后只做：`_sortCol/_sortDir` 更新 + `_grid.Invalidate()`（VM 下重绘只让可见行重新 Needed，O(可见行)）+ `ClearSelection()`/`CurrentCell=null`（`:1856-1857` 语义保留）+ 列头箭头自绘（`:1238-1247` 不动）。
- 行高在排序后由 Needed 之外的布局保留：虚拟模式下行高需要在 `RowHeightInfoNeeded/Pushed` 事件中从 `RowModel.Height` 读写（这是 VM 专用事件，替代现"行高存在物理行上、随 SortView.Height 搬运" `:1769/:1851`）。
- 双击表头三级排序（`:1731-1739`）、单击列头/行头选择（`:564-572`、`:2090-2117`）逻辑保留；SelectColumn/SelectRow 的 `foreach (DataGridViewRow)` 改为按可见行索引区间操作（只选可见行，本来也只有可见行可点选，选中集合语义一致）。

### 7.6 自动列宽（采样，替代全量 GDI）

- 替换 `AutoFitColumns()`（`:1007-1063`）：宽度测量样本 = 表头各行 + 数据行采样：首 50 行 + 均匀间隔 150 行（对 `_models` 直接取串，不经过网格、不触发 Needed）。
- 时机：首批加载完成后一次；语言列集合变化（Reload 重建列）后一次；手动"适应列宽"时一次。滚动不重算。
- min/max/pad/箭头预留（`:1011-1013/:1051-1053`）常量保留。
- 10000 行长文本超宽列：采样命中长文本概率不足时，maxW=420 封顶本身已限损；可接受（与现状上限一致）。

### 7.7 CellFormatting 状态着色（O(1)/格，A6）

- `CellIsDirty/CellSavedChanged`（`:1280-1284`）改为模型内标记比较（Current vs Baseline / Current vs Initial），不再读 `_grid[col,row].Value`。
- `RowModificationColor`（`:1290-1300`）改为读 `RowModel` 预聚合的行状态枚举（None/Dirty/Saved），行头/原值列 O(1) 取色；行状态在 Pushed 时增量维护，不在绘制时遍历列。
- 聚焦行/列浅蓝（`:1172-1185`）、列头/行头自绘与排序箭头（`:1193-1247`）逻辑不动。

### 7.8 Apply 按钮与状态栏（A7，增量）

- `UpdateApplyEnabled()`（`:1929-1941`）瘦身为：`_applyBtn.Enabled = _dirtyCount>0` + `UpdateStatus(_dirtyCount)`，无扫描、无整表 Invalidate。
- 状态栏文案/颜色（`:1943-1969`）、复选框列"双击修改"提示（`:1961-1966`）保持。

### 7.9 自动保存 / SaveDirty / DirtyRows / SnapshotRow

- 自动保存节拍（250ms 固定节拍，`:1989-2048`，含饿死修复与编辑行 skipRow 保护）**机制全部保留**。
- `DirtyRows()`（`:1919-1927`）改为返回 `_dirtyRows` 中排除 skipRow 的列表（不再 SnapshotRow 逐格读网格）。
- `SaveDirty()`（`:2503` 起）改造点：
  - 所有"从网格取期望值"的读取（`:2567-2569/:2578/:2629-2631/:2646`）改为从 `Current` 读；`ToEplan()` ¶ 转换（`:2601`）、UndoStep+Transaction 单事务（`:2552-2554/:2616-2621`）、仅脏行/仅变化字段（`:2595`）、回读校验（`:2624-2669`）、mismatch 行报告逻辑不变。
  - 回读通过后：`Baseline = Clone(Current)`、行 Dirty 清除、移出 `_dirtyRows`、`_dirtyCount` 递减（替换现在"保存后隐式靠下一次全表比较"的做法）。
  - 回读比对的期望值也从 Current 取（`:2646/:2659-2662`）。
  - 编辑行排除（skipRow，`:2006-2013/:2516`）：VM 下编辑中的 Current 可能正被写——维持"整行不写不校不刷"即可，天然安全。
- `SnapshotRow()`（`:1891-1904`）删除（基线已在模型）；`CloneRow()`（`:1302-1307`）保留用于保存成功后快照。

### 7.10 现有交互适配清单（哪些依赖实体行、怎么改）

| 交互 | 现位置 | 依赖实体行？ | VM 适配 |
|---|---|---|---|
| 编辑/¶ 换行（Ctrl+Enter、WM_PASTE 多行） | `:324-356`、LineBreakTextBox `:2852-2921` | 否（操作 EditingControl） | 不动；¶ 语义在 Pushed 落 Current |
| Ctrl+J 转到图形 | `:1083-1123`、EditGrid.WndProc `:2778-2792` | 取 `_texts[row]` `:1109` | 改取 `_models[_order[row]].T`；Edit/OpenPageWithPlacement 不变 |
| 复制/剪切 | CopySelection/CutSelection `:2160` 起、EditableSelectedCells `:2155`、IsCellUserWritable `:2144` | 遍历 SelectedCells 读 Value | 选格仍是可见格；值改由 Needed 同源 helper 取（`GetModelValue(row,col)`），TSV 拼装/¶ 输出规则不动 |
| 粘贴（单格整块/选区平铺/TSV） | PasteClipboard `:2340` 起、WritePasteCell `:2426-2436`、ParseTsv `:2442-2491` | `cell.Value=` 写实体格；`_grid.Rows.Count` 边界 `:2377` | 写 `SetModelValue(row,col,token)`（内含复选框解析/ToGrid/联动/dirty）；边界改 `_order.Length`；只读判定改模型+CellBeginEdit 同一谓词 |
| 清除（Delete/右键） | ClearSelection（GridOnKeyDown `:2084`） | 是 | 同上 SetModelValue 写空/默认标志 |
| 还原当前值 | `:2270-2303` | `_initial[r]` + `_grid[col,r].Value=` | 从 `Initial` 取、写 Current、走增量 dirty |
| 多语言暂存 | `:498-524`、`:1711`、`:1773/:1831`、`:2338` | stash 并列表+网格清空 | 收进 RowModel.Stash（§7.3） |
| 多选/右键校正选区 | `:2067-2077` | 否（SelectedCells） | 不动 |
| 右键菜单（换行/复制/粘贴/清除/还原/适应行高/转到图形） | BuildGrid 菜单 `:560` 附近；ResetRowHeights `:984-1001` | ResetRowHeights 遍历物理行 `:995/:999` | 改模型 Height（`:984-1001`），配合 RowHeightInfoNeeded |
| 列/行头单击选择、双击排序 | `:564-572`、`:1731` | SelectColumn/Row 遍历行 | §7.5 |
| Ctrl+滚轮缩放 | EditGrid.OnMouseWheel `:2794-2804`、ZoomGrid `:154` 起（遍历行字体 `:167`） | 遍历 `_grid.Rows` 设字体 | VM 下无实体行：缩放改为设 `DefaultCellStyle.Font`/行模板（一次），靠可见行 Needed/布局生效；删除逐行字体循环 |
| 消息过滤器 3 个 | `:338-436` | 否 | 不动 |
| 关窗三态询问/确定/取消 | `:283-316`、`:1330-1336` | DirtyRows() | 走增量 dirty 集；逻辑不变 |
| 保存后绿底/撤销粒度 | SaveDirty 全方法 | — | 单 UndoStep+单事务、仅脏行（`:2552-2621`）保持，撤销粒度不回退 |
| 多语言编辑器（如有独立窗体入口） | 本类内未见独立入口（多语言行为由复选框+语言列承担） | — | 以代码为准，无额外适配 |

### 7.11 受影响方法清单（汇总，评审用）

重写/大改：`LoadRows`、`ReloadSelection`、`ApplySort`、`ApplyDefaultOrder`、`SnapshotRow`(删)、`DirtyRows`、`IsRowDirty`(删并入模型)、`UpdateApplyEnabled`、`SaveDirty`、`AutoFitColumns`、`SetRowEditable`(→CellBeginEdit)、`BuildGrid`（VM 配置/事件挂载）、构造函数（加载流程）、`SelectColumn/SelectRow`、`ZoomGrid`、`ResetRowHeights`。
改读写目标（网格→模型）：`ToggleCheckBoxCell`、`ApplyStateToRow`、`RestoreCurrentValue`、`PasteClipboard/WritePasteCell`、`CopySelection/CutSelection`、ClearSelection 处理器、`GoToGraphic`、CellValueChanged 多语言联动（并入 Pushed）、`SyncPair`(删)、`CurrentCellValue/StateValue/CellIsDirty/CellSavedChanged/RowModificationColor`。
基本不动：`BuildColumns`（列定义）、三个 IMessageFilter、LineBreakTextBox/Cell、EditGrid.WndProc 的 Ctrl+J、`ParseTsv`、`BuildRankMaps/FillRank/WalkLocationDfs`、`BuildMeta`（输入从 t 读，原样可用）、`ToGrid/ToEplan/ExtractPageName/PageIdent/NormIdent/RankOf`、菜单/底栏/Tab 构建、日志。

---

## 8. 列宽/行高拖拽实时预览（live resize）

### 8.1 现状与目标

- 现状：`EditGrid`（`:2767-2834`）未接管尺寸拖拽；`BuildGrid` 中 `AllowUserToResizeRows=true`（`:452`），列宽默认可拖，行首宽 `DisableResizing`（`:451`，指最左行号列宽度，与行高拖拽互不冲突）。
  DataGridView 内置拖拽只画位置引导线、松手才重排（标准行为）。
- 目标：拖动列头右缘 / 行首下缘时**边拖边见效果**；10000 行不卡；Esc 取消恢复；双击分隔条自适应；不影响编辑态、复选框、排序、Ctrl+滚轮缩放。

### 8.2 为什么 VirtualMode 后可行（关键论证）

- 非虚拟模式下实时改 `column.Width` / `row.Height` 会触发布局对**全部物理行/物理单元格**的重算与重绘，n=10000 时每次鼠标移动都是一次全表布局——必然卡。
- VirtualMode 下不存在实体数据行：改列宽只让可见区（通常 20~40 行）重新布局并触发可见行的 CellValueNeeded；改行高（单行/选中行集）同样只影响可见区布局，行高数据经 RowHeightInfoNeeded 按需返回。**成本与总行数无关**。
- **该性质与采集线程形态无关**：即使探针否决 A、最终采用方案 B（主线程分块），live resize 依然成立——它只依赖 VirtualMode 的可见行语义。A/B 只决定数据怎么读，不决定拖拽怎么画。

### 8.3 交互设计

1. 鼠标在列头右缘 ±5px（或行首下缘 ±5px）按下 → 进入自定义拖拽（光标已是分栏光标）：记录目标列索引/行索引集合、起始宽/高、起始鼠标位。
2. 拖动中：实时把目标宽/高设为 起始值 + 像素增量（列宽取整、clamp 到最小/最大），可见区即时重排；显示一条与网格同色的位置线作为辅助（可选，内容本身已实时跟随，引导线仅增强定位）。
3. 行高多选：起拖时若该行首属于"选中的多个行头"，增量同时应用到全部选中行（对齐 DataGridView 内置多选行拖拽语义）。
4. Esc：恢复拖拽前的宽度/高度（键盘消息在鼠标捕获期间仍到本控件 WndProc，见 8.4）。
5. 松手：提交；列宽/行高仅作用于本次会话模型（行高写入 `RowModel.Height`，随排序经 RowHeightInfoNeeded 保持，与现"行高随排序携带"语义一致）。
6. 双击分隔条：自适应该列宽度——用 §7.6 的**采样测量**（不遍历全部行），即时生效；行首下缘双击恢复默认行高（等同现 ResetRowHeights 对该行的效果 `:984-1001`）。
7. 边界：
   - 最小列宽 40（沿用 AutoFit 的 minW `:1011`；给列统一设 `MinimumWidth`），最大 420 同现上限；
   - 最小行高 = 当前字体单行文本高 + 单元格内边距（不小于 RowTemplate.Height 的 80%），最大行高给 3 倍默认高，防误拖成巨行。
   - 拖拽中不触发排序（列头拖拽命中的是右缘，不是表头点击；现排序入口在 Click/DoubleClick `:564-572/:1731`，命中测试区分即可）。

### 8.4 实现要点（EditGrid 内，自包含，不改业务窗体）

- 用 `OnMouseDown` + `DataGridView.HitTest` 判定：
  - `HitTestType.ColumnHeader` 且 X 位于该列 `GetColumnDisplayRectangle` 右缘 ±5px → 列拖拽；
  - `HitTestType.RowHeader` 且 Y 位于该行 `GetRowDisplayRectangle` 下缘 ±5px → 行拖拽。
  - 命中即 `Capture = true` 并**不调用 base 的鼠标处理**（防止内置"引导线式"拖拽同时启动），由本类完全接管到 MouseUp。
- `OnMouseMove`（捕获中）：节流——距上次生效位移 **≥3px**（参数取 2~4px）才真正写 Width/Height；比 Timer 节流简单且天然对齐刷新率。不需要 WM_SETREDRAW 挂起（挂起反而造成闪烁/空白；VM 可见行布局本身够便宜）。
- `WndProc` 中在捕获态额外识别 `WM_KEYDOWN VK_ESCAPE`：恢复并结束（`OnMouseMove` 对 KeyDown 不可见，必须在 WndProc）；`WM_LBUTTONUP` 走 OnMouseUp 提交。
- 双击：`OnMouseDoubleClick` 同样先 HitTest 右缘/下缘；列自适应调用窗体提供的采样测量回调（`Func<int,int> AutoSizeColumnSampled`），行恢复默认高回调。
- 对外暴露两个回调属性（与现有 `GridClipboardCommand/ZoomGrid` 风格一致，`:2771-2774`）：
  `Action<int> AutoSizeColumnRequest`、`Action<int> ResetRowHeightRequest`；EditGrid 只管命中/拖拽/取消，不碰模型。
- **编辑态保护**：拖拽开始时若 `IsCurrentCellInEditMode`，放弃接管、走内置行为（避免与编辑控件鼠标捕获冲突）；编辑只可能发生在数据区，正常情况下拖分隔条不会与编辑态同时出现，保留守卫即可。
- 与 Ctrl+滚轮缩放共存：缩放改的是字体（§7.10 改为默认单元格样式字体），列宽不动、行高按字体下限 clamp；缩放后双击分隔条仍可重新自适应。
- 与复选框/排序/多语言列显隐：列宽是列属性，列隐藏/重现（UpdateColumnVisibility `:949-978`）与排序不影响已拖宽度。
- 行首列宽（最左行号列）维持 `DisableResizing`，不纳入本次自定义拖拽（避免与现有 O(1) 行首宽逻辑 `:133-141` 冲突）。

### 8.5 回归清单（live resize 专项）

1. 5000、10000 行两档：拖动任意列右缘全程跟手、无明显掉帧（肉眼），任务管理器 EPLAN 无 CPU 打满长钉；
2. 拖行首下缘：单行实时变高/变矮；选中多个行头后拖一条边，多行等高联动；
3. Esc：列宽、行高均恢复拖拽前；松手后再 Esc 不撤销（符合 Windows 惯例）；
4. 双击列右缘：按采样宽度自适应（长文本列变宽、空列收到表头/40px 下限）；双击行下缘恢复默认高；
5. 最小/最大 clamp：拖到极窄=40，拖到极宽≤420；行高上下限有效；
6. 编辑态：正在某格编辑时拖分隔条不丢编辑内容、不夺焦（走内置或忽略，二者均可，不崩即可）；
7. 复选框双击翻转、多语言开关联动、右键复制/粘贴/清除/还原在拖拽改宽/改高后行为不变；
8. 排序后：行高跟随行（RowHeightInfoNeeded 读模型 Height），列宽不随排序变化；列头 ▲/▼ 箭头不被遮挡（箭头预留 18px 逻辑 `:1013/:1051` 仍在）；
9. Ctrl+滚轮缩放后再拖宽/拖高正常；缩放本身不重置用户拖拽结果；
10. 列显隐开关（显示原值/结构/坐标）切换后，曾拖宽的列宽度保持；
11. 自动保存 250ms 节拍在拖拽中到期：不打断拖拽、不弹任何窗、编辑行保护仍有效。

---

## 9. 分期实施计划（每期独立 Gate + 真机回归）

原则：先摘低风险果子（P0 不改交互模型），再解决"冻住"（P1），VirtualMode 作为 P2 主体，live resize 紧随 P2（P3）。每期 Debug/Release 均须 0 警告 0 错误、`git diff --check` 干净。

### P0 — 低风险减负（不引入 VirtualMode，先削一两个数量级的浪费）

范围：
1. 装载期日志降噪：`LoadRows/BuildMeta/ApplySort` 循环内 Debug 改"仅 PerfTrace 或汇总一条"（10000 行上万次 AppendAllText 消除，§2.3）；
2. `AutoFitColumns` 全量 MeasureText → §7.6 采样（bound 模式下对网格行做等间隔采样取值，不依赖 VM）；
3. 消除装两遍（§6）：采集→内存默认序→一次性 Rows.Add，删除构造/Reload 的二次 ApplySort；
4. dirty 增量：CellValueChanged 维护 dirty 集与计数，`UpdateApplyEnabled` 去全扫/去整表 Invalidate（bound 模式也能做：以行脏标记+InvalidateCell）。

Gate：Debug/Release 0/0；真机回归 R1~R5（见 §10）；5000/10000 开窗计时与 P- 基线对比（应可见数倍改善但不保证不冻——采集仍同步）。

### P1 — 分块加载 + 进度 + 可取消（方案 B，bound 网格暂留）

范围：窗先 Show + 遮罩进度；UI 线程 200 行/块（33ms 预算自适应）+ await Task.Yield；取消即关窗；ReloadSelection 走同路径；自动保存加载期暂停/恢复。
Gate：0/0；加载中 EPLAN 主窗可拖动、可切页、不白屏（"应用内无响应"不出现）；取消中途无残留窗/无锁异常；R1~R6 + 5000/10000 计时。
同步决策点：本期末跑 `scripts/probe/`，取得 A/B 真机证据并由 xavier 裁定 P2 的 loader 形态。

### P2 — VirtualMode 全面切换（根治主体）

范围：§4 RowModel、§7 全部 Needed/Pushed/排序置换/RowHeightInfoNeeded/CellBeginEdit 逐格可编辑/行号自绘/采样列宽/模型化 SaveDirty 与自动保存/复制粘贴清除还原暂存转到模型/缩放改造。
Gate：0/0；全量回归 §10（重点复选框虚拟模式提交、粘贴大块、自动保存编辑行保护、撤销粒度）；5000/10000 开窗可交互（滚动/编辑/排序/保存全程不冻）；内存对比（不再有 24 万 Cell 对象，任务管理器工作集显著下降）。
回滚策略：P2 为单期完整切换，不做 VM/bound 双模式开关（双模式=两套维护负担）；靠 git 回滚，发布前仅在测试版交付。

### P3 — live resize（§8）

范围：EditGrid 自定义列/行拖拽（节流、Esc、双击自适应、clamp、多选行），RowModel.Height 经 RowHeightInfoNeeded 落地。
Gate：0/0；§8.5 全部 11 项 + 5000/10000 拖拽跟手。

---

## 10. 真机回归清单（每期按涉及项执行；5000/10000 两档必测）

数据准备：单语言/多语言（≥3 语言）项目各一；普通页文本、多语言文本、不自动翻译文本、结构跨多个高层/位置的文本混选；另含少量失效对象（加载前在导航器删一个）。

- R1 开窗：标题源语言/计数正确；默认序=结构管理顺序→X↑→Y↓；序号 1..n；页列为纯页名。
- R2 编辑：文本列与源语言列双向同值；¶ 显示、Ctrl+Enter 换行、多行粘贴转 ¶、写回后真实换行（抽查 EPLAN 属性）。
- R3 复选框：双击翻转多语言→非源语言列编辑态联动；stash 关→开译文还原；不自动翻译保存后 `IsAutomaticallyTranslated` 正确（回读校验日志无 mismatch）。
- R4 排序：三级（升/降/默认）正确且稳定；排序后行高、选中清除、箭头、转到图形对象正确。
- R5 复制/剪切/粘贴/清除/还原：TSV 整块单格铺、选区平铺整除拦截、复选框 token 解析、只读列拒写、还原到开窗值（绿底消除）。
- R6 自动保存：250ms 节拍；编辑行不落库不打断；快速连读不饿死；失败停表不忙循环；关窗三态/确定/取消路径；单事务单撤销点（EPLAN 撤销列表只有一项"文本批量编辑"）。
- R7 大数据：5000、10000 行——开窗（P1 后全程不白屏、可取消）、滚动、排序、列开关、复制粘贴、勾选、自动保存、关窗；记录各段耗时（PerfTrace 汇总日志）。
- R8 刷新选择集：Reload 未变化早退；脏行三态询问；语言变化重建列；大选择集刷新同加载体验。
- R9 转到图形：Ctrl+J 三个捕获层（网格/编辑框/过滤器，`:1086` layer 日志）、右键菜单；排序后跳转对象正确。
- R10 视觉/交互：灰/黄/绿底色语义、聚焦行列浅蓝、缩放 0.7~1.8、列显隐、说明页日志路径。
- R11（P3）live resize §8.5 十一项。

观测手段：`PerfTrace=true` 临时打开（`:66`），用 LoadRows/ApplySort/AutoFitColumns/SaveDirty 既有 PERF 汇总行；验收后随 TEMP-PERF 整块删除（`:61` 注释纪律）。

---

## 11. 真机线程探针（A/B 判据，交付物在 `scripts/probe/`）

为什么不能用 PowerShell 反射脚本：脱离 EPLAN 进程无法获得 `SelectionSet`/`TextBase`（HEServices/DataModel 必须在 EPLAN 进程内初始化）；EPLAN 2.9 脚本（.cs 加载型）按平台限制只能引用 Base/AFu/Gui 三个程序集，拿不到 DataModel/HEServices。因此探针是**独立最小 Add-in DLL**（与产品完全分离，不改产品 csproj，不并入解决方案）。

交付文件：
- `scripts/probe/TbeThreadProbeAddIn.cs` —— IEplAddIn 注册菜单"TBE线程探针"，IEplAction 执行；
- `scripts/probe/build-probe.ps1` —— 用 .NET Framework 自带 csc 编译，引用 EPLAN bin 的 EplApi DLL（或仓库 references/EplApi），输出单 DLL；
- 运行方式与结果判据见该两文件头部注释与本节。

探针动作（点击菜单后弹小窗，按钮开始，结果同时显示并写 `%TEMP%\TbeThreadProbe.log`）：
1. 打印 EPLAN 主线程 ManagedThreadId / ApartmentState / `new EplanMainThreadDispatcher().CanAccessMainThread()`；
2. 取当前选择集中的 TextBase（两个 SelectionSet 锁属性均设 false，避免探针改动锁语义）；预热 10 行后，在 UI 线程对 N=500、5000、全部（≤10000）顺序读：`Contents`、`GetLanguageList`、逐语言 `GetString`、`Page.Name`、3 个 FULL 属性、`Location.X/Y`，计时并校验非空率；
3. `Task.Run` 内打印后台线程 Id/ApartmentState/`CanAccessMainThread()`，做同样读取并计时：全程 try/catch 记录异常类型+消息+堆栈；值与第 2 步主线程抽样比对（首/中/尾 20 行）；
4. 后台线程内用 `ExecuteInMainThreadSync` 跳回主线程读 1 行并计时（量化封送成本）；
5. 连续 3 轮"后台读→主线程复读"，检查是否出现异常、错值、对象失效，结束后强制 GC.KeepAlive/GC.Collect 观察是否崩溃；
6. 后台读取期间窗内计时器持续刷新（证明 UI 泵存活），并提示用户此时手动切页/点菜单观察 EPLAN 是否响应。

判定：
- 全绿（无异常、值一致、3 轮稳定、宿主操作正常）→ A 技术可行但 out-of-support，报 xavier 决策；
- 任一异常/错值/卡死/版本差异提示 → 锁定 B。
- **[UNCERTAIN]** 探针只覆盖"读"；写（Contents setter/标志位）永远在主线程做，不做后台写测试（无设计需求且风险无意义）。

---

## 12. 风险与未决项

1. **[UNCERTAIN] 后台线程读可行性**：官方 out-of-support（§3.1），靠探针实证；B 方案不依赖该结论，可独立交付。
2. **[UNCERTAIN] 虚拟模式复选框提交细节**：列 ReadOnly + 双击翻转 + CurrentCellDirtyStateChanged/CommitEdit 在 VirtualMode 下的真机行为需在 P2 最早做一个尖刺验证（失败则退化为自绘复选框列，代价另估）。
3. **[UNCERTAIN] RowHeightInfoNeeded/Pushed 与手动行高、多选行拖拽的配合**需真机微调（机制确定，事件参数细节以 .NET Framework 文档+实测为准）。
4. **选择集收集前置**（§5.5）：Action 侧枚举在 Show 之前，P1 调整时确认 SelectionSet 可在窗 Shown 后构造。
5. MultiLangString 由 Contents getter 返回、产品代码未 Dispose（现状即如此）；VM 后每行只在采集时取一次字符串存入模型，对象持有量不增加；是否应 Dispose 返回的 MultiLangString 属独立技术债，本次不改变现状。
6. 分块期间用户在图形区改选择：不主动跟踪 SelectionSet 变更（Reload 是显式动作，现语义如此）；只对 `IsValid` 失效做防御。
7. 大选择集下"显示原值"展开列数翻倍（列数=约 13+2L，L=语言数）：VM 后仅可见格成本，列宽采样覆盖原/新两侧；横向滚动体验（冻结列等）不在本次范围。
8. 性能数字（5000/10000 各段毫秒数）本轮**未实测**（环境无 EPLAN、任务禁止启 EPLAN/构建）；P0/P1 Gate 必须以真机 PerfTrace 数据填写，本文不编造预算外的承诺值。

---

## 13. 证据来源

- 产品代码：`EA.EplAddIn.TextBatchEdit/TextBatchEditForm.cs`（2927 行，base 97a8a0d）、`TextBatchEditAction.cs`、`TextBatchEditAddIn.cs`、`AddInLogger.cs`。
- EplApi 反射：dnfile 只读解析 `references/EplApi/*.dll` 元数据（未加载/未执行混合模式程序集）。
- 官方文档（2.9 API Help，eplan.help）：
  - `Eplan.EplApi.Base.Internal.EplanMainThreadDispatcher` 类页（Remarks/成员/示例）；
  - 《Locking》（锁语义、SelectionSet 默认锁）；
  - 《EPLAN API offline applications / UsingEplanAssemblies》（DataModel 与 LockingStep、主线程退出要求）。
- 社区：Suplanus《BackgroundWorker in der EPLAN API》(2018)，旁证 out-of-support 与 dispatcher 用法。
- 关联评估：`docs/assess/text-batch-edit-uxperf-20260918.md`（A2/A3/A5/A6/A7/A10/VirtualMode/§3.2 埋点/§7 R 项，本设计为其根治方案）。
