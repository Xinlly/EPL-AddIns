# TextBatchEdit（文本批量编辑）交接文档 HANDOFF

> 状态：**交接草稿，未经 xavier 宣布交接完成，不得据以派工/开发/发布。**（注：插件功能本身已于 2026-09-17 获 xavier 总体验收，见 §4.4；"功能验收"不等于"交接流程完成"。）
> 本文档是未来 planner / worker / reviewer 接手 TextBatchEdit 工作线的基准。只记录本插件项目事实；通用多 agent 编排规则见 skill `paseo-orchestrator` 与 AGENT.md「多 Agent 编排」节，不在此复制。

---

## 0. 元信息

| 项 | 内容 |
|---|---|
| 文档日期 | 2026-09-17（v1 初版；v2 同日补 xavier 验收结论与实测问题，见文末「文档修订记录」） |
| 编制 | planner（常驻 feat/text-batch-edit 工作线，协调者 ca4dcf0 派出） |
| 工作区 | `wks_1c82cd87b9e43af7`（worktree 隔离，标题 feat/text-batch-edit） |
| worktree 物理目录 | `/var/lib/hermes/.paseo/worktrees/3635jb1e/feat-text-batch-edit` |
| 分支 / base | `feat/text-batch-edit`，base = 本地 develop 尖端 `e4a80f2`，无 upstream |
| 历史权威来源（顾问） | 旧普通会话 agent `cc090eab-c109-43b6-9cd3-4191e911912b`（名「本地」，驻旧工作区 `wks_140f20f3da41008d`，cwd = 仓库主 checkout `/mnt/d/Users/Admin0/source/repos/EPL-AddIns`） |
| 验收权威来源 | xavier 本人结论，经协调者转达（2026-09-17，见 §4.4） |
| 信息来源优先级 | xavier 本人结论 > 顾问答复 > git 历史 / AGENT.md / 代码实读 > planner 推断。推断一律标 `[UNCERTAIN]`；来源间冲突时两边并记、列存疑清单，不擅自取一。 |
| 本轮约束 | 只读盘点 + 访谈 + 仅落盘本文档；不写产品代码、不构建、不发布、不 git add/commit/push。 |

### 来源标注约定

- `[代码实读]` = 本轮在本 worktree 直接读源码确认，结论带 `文件:行`。
- `[git 证据]` = 本轮实际运行只读 git 命令确认，命令与结果见 §4。
- `[AGENT.md]` = 仓库 AGENT.md（develop @ e4a80f2）记载。
- `[顾问答复 N]` = cc090ea 访谈答复（N = 附录问题编号，带日期）。顾问为计费会话，问题分批合并提出。
- `[xavier 验收 2026-09-17]` = xavier 本人结论，经协调者转达（原话见 §4.4），权威度高于顾问"凭记忆从严"。
- `[UNCERTAIN]` = 尚无一手证据，或缺哪类证据已注明。

---

## 1. 插件功能与入口

### 1.1 功能（一句话）

在 EPLAN Electric P8 2.9（net472 / x64）中，对**图形编辑器选中的文本对象**或**页导航器/查找结果中选中的页**，批量编辑其中英文（多语言）文本及翻译标志，提供原值对照、Excel 式块复制粘贴、排序、转到图形，整批写回合并为**一个撤销点**。

覆盖对象类型：`Eplan.EplApi.DataModel.Graphics.TextBase`（自由文本 `Text` 与路径文本 `PathText` 的共同基类），见 `TextBatchEditAction.cs:66`、`:111`。`[代码实读]`

### 1.2 入口与注册

| 入口 | 挂接点 | 代码 |
|---|---|---|
| 主菜单（工具/实用工具菜单末尾） | `new Menu().AddMenuItem("文本批量编辑", "TextBatchEditAction")` | `TextBatchEditAddIn.cs:46-47` |
| 图形编辑器（图纸/GED）右键 | `ContextMenuLocation{ DialogName="Editor", ContextMenuName="Ged" }` | `TextBatchEditAddIn.cs:58-60` |
| 页导航器（页树）右键 | `DialogName="PmPageObjectTreeDialog", ContextMenuName="1007"` | `TextBatchEditAddIn.cs:71-73` |
| 查找结果选项卡右键 | `DialogName="XSeSearchResultsTab1", ContextMenuName="1002"` | `TextBatchEditAddIn.cs:85-87` |

三处右键与主菜单共用同一 Action 名常量 `TextBatchEditAction.ActionName = "TextBatchEditAction"`（`TextBatchEditAction.cs:17`），Action 以 `[DeclareAction]` 标注（`TextBatchEditAction.cs:182`）。`[代码实读]`

生命周期（`TextBatchEditAddIn : IEplAddIn`）：`OnRegister` 设 `bLoadOnStart=true`（`:99-105`）→ `OnInit` 记日志/挂全局异常（`:24-34`）→ `OnInitGui` 注册菜单并安装右键显隐钩子（`:43-97`）→ `OnExit`/`OnUnregister` 卸载钩子（`:36-41, 107-112`）。`[代码实读]`

窗口为**非模态常驻单例**：`form.Show(主窗属主)`，重复触发 Action 时用最新选择集 `ReloadSelection` 刷新或前置（`TextBatchEditAction.cs:23-36, 217-233`）；`ShowWithoutActivation => true` 使其不抢键盘焦点（`TextBatchEditForm.cs:214`）。`[代码实读]`

### 1.3 文本收集口径（CollectTexts）

`TextBatchEditAction.cs:54-127`，严格区分上下文：

1. 选择含 `TextBase` → 仅这些文本（`:64-77`）；
2. 选择为空（图面只是打开某页、未选任何对象）→ 返回空，**不回退枚举当前页**（`:80-84`）；
3. 显式页选择（选择含 `Page`，或非空且不含图面 `Placement` 且 `GetSelectedPages()` 展开非空——页树选结构节点）→ 枚举所选页 `AllPlacements` 中的全部 TextBase（`:86-127`，判定 `IsExplicitPageSelection` `:133-138`）。**该分支只看"选择内容是否含页/结构节点"，不区分来源表面**：已验证入口是页导航器；ab1e288 后查找结果列表若选中页行且进了 SelectionSet，理论上走同一分支枚举整页——**此入口未真机验证**（`[顾问复核 R4, 2026-09-17]`）；
4. 其余（图面选中元件等非文本 Placement）→ 返回空（`:94-99`）。

无可编辑文本时弹信息框提示三种选取方式（`:191-196`）。`[代码实读]`

---

## 2. 代码结构（逐文件）

目录：`EA.EplAddIn.TextBatchEdit/`（6 个文件）。`[代码实读]`

### 2.1 `TextBatchEditAddIn.cs`（323 行）— IEplAddIn 生命周期 + 菜单注册 + 右键按需删除钩子

- 生命周期与异常订阅：`OnInit` 记录程序集全名与实际日志目录，订阅 `Application.ThreadException` 与 `AppDomain.UnhandledException`（`:24-34`）。
- 四处菜单注册（见 §1.2）。
- **右键项按需显隐核心机制**：`OnInitGui` 用 P/Invoke 在本 UI（MFC）线程装 `WH_CALLWNDPROC(=4)` **线程局部**钩子（`:114-135`），拦 `WM_INITMENU(0x0116)`/`WM_INITMENUPOPUP(0x0117)`（`:148-168`），在 BCG 自绘菜单处理前，若本次不应启用则把本插件项从临时 HMENU 中 `DeleteMenu`（`:170-220`）。
  - 区分主菜单栏下拉 vs 右键 TrackPopupMenu：只认 HMENU 是否挂在主框架 `GetMenu()` 菜单树（`IsMainFramePopup`/`MenuTreeContains` `:257-285`），**不能用 hwnd 判等**（GED 右键的消息 owner 被 MFC 路由到主框架 `AfxMDIFrame140u`，注释 `:251-256`）。
  - 区分图面 vs 页树/查找结果：`CursorHitChain()` 取右键瞬间光标处 `WindowFromPoint` 沿 `GetParent` 到顶层的类名链（`:227-243`）；链含 `AfxFrameOrView` 判为图面（严格 `SelectionHasText()`），否则判为列表（`SelectionHasText() || SelectionHasPage()`）（`:198-205`）。
  - 委托存实例字段防 GC（`:22, 119`）；回调全程 try/catch 并 `CallNextHookEx` 透传（`:150-167`）；退出/注销 `UnhookWindowsHookEx`（`:137-146`）。
- 内嵌 `NativeMethods`（`:287-322`）：user32/kernel32 P/Invoke 声明 + `CWPSTRUCT`/`POINT` 结构。

### 2.2 `TextBatchEditAction.cs`（398 行）— Action、选择集收集、项目语言解析

- `IEplAction` 三成员：`Execute`（`:182-243`）、`GetActionProperties`（`:395`）、`OnRegister(ref Name, ref Ordinal)`（`:397`）。
- 单例窗口管理、主窗属主包装 `WindowOwner`（`:19-42, 217-233`）。
- `CollectTexts` / `IsExplicitPageSelection`（§1.3）；对外静态判定 `SelectionHasText()`（`:142-154`）、`SelectionHasPage()`（`:158-170`）供钩子调用。
- 项目语言解析：
  - 源语言 `ReadSourceLanguage`（`:250-296`）：优先项目设置 `TRANSLATEGUI.SOURCE_LANGUAGE`（键**不带** `PROJECT.` 前缀）；处理「##_##（对话语言）」占位（编号 119，非枚举成员，`:298-310`），改从用户设置 `SYSTEM.GUI.LANGUAGE` / `Languages.GuiLanguage` 解析（`ReadDialogLanguage` `:316-356`）；再回退只读属性 `PROJ_SOURCELANGUAGE`；最终兜底 `L_zh_CN`。
  - 翻译语言集合 `ReadProjectLanguages`（`:362-393`）：`TRANSLATEGUI.TRANSLATE_LANGUAGES` 是**单个分号串**（如 `"en_US;zh_CN;"`），按 `;` 拆分，非按索引多值。
  - 语言短码/枚举名/数字解析委托 `LangHelper.TryParse`。

### 2.3 `TextBatchEditForm.cs`（2643 行）— WinForms 多语言表格 UI 与写回事务

> 本插件绝大部分复杂度在此。按区块归纳，行号供追溯。

- **列结构**（`BuildColumns` `:615-691`，列索引常量 `:123-147`）：序号/对象类型；只读信息列（高层代号=、安装地点++、位置代号+、X、Y、页，默认隐结构/坐标、页常显）；原值侧（只读：多语言勾选、不自动翻译勾选、源语言文本、各语言文本，默认隐藏）；新值侧（可编辑：多语言勾选、不自动翻译勾选、源语言文本列与各语言列）。源语言文本列与源语言语言列**双向同值同步**（`SyncPair` `:710-715`，联动 `:423-430`）。
- **多语言/单语言（语言无关串）模型**：开窗 `LoadRows`（`:1434-1561`）读 `TextBase.Contents`（MultiLangString），用 `LanguageList` + C++/CLI 访问器 `get_Language(i)`；语言列表空或含 `L___` 判为未翻译（语言无关串），干净值经 `GetStringToDisplay`/`GetString(L___)`/`InternalString` 多读法 `PickClean`（`:2469-2484`）。`IsAutomaticallyTranslated` 反相映射「不自动翻译」勾选（语义反相：UI 勾选 = 属性 `false`，写回 `:2306, 2342-2345`）。
- **多语言切换暂存**：关闭行多语言时把非源语言译文存入 `_stash` 并清空，重开写回（仅会话内，不参与保存）（`:29-30, 389-415`）。
- **写回与撤销**：`SaveDirty`（`:2269-2448`）——增量只写脏行；`new MultiLangString()` 逐语言 `AddString` 后**经 setter 整体赋回** `t.Contents`（`:2338-2341`，就地改 getter 不落库）；`UndoManager().CreateUndoStep()` + `TransactionManager().CreateTransaction()`，`txn.Commit()` 后 `undo.CloseOpenUndo()`，整批一个撤销点（`:2290-2359`）；**提交后回读校验**逐语言比对，不一致记 ERROR 并在结果框列问题行、不把该行标为已保存（`:2361-2421`）；异常路径 `txn.Abort()`（`:2438`）。无脏行不建撤销点（`:2275-2279`）。
- **还原**：右键「还原选中的行」回到开窗原值 `_initial`（已保存也能还原，`:1998-2025`）；「还原当前值」按格映射回新值列、先复选框后文本（`:2031-2074`，`ApplyStateToRow` `:2091-2111`）。
- **复制/剪切/粘贴**：自建 TSV，复选框列输出 TRUE/FALSE，含 ¶/Tab/引号按 Excel 规则引用（`CopySelection` `:1931-1961`、`QuoteTsv` `:1964-1968`、`PasteClipboard` `:2113-2194`、`ParseTsv` `:2213-2262`）；单格锚点整块铺、多格选区整除重复平铺两种 Excel 语义；勾选 token 解析 `ParseCheckToken`（`:1919-1924`）。
- **快捷键三层接管**（日志 layer 区分捕获层）：
  - Ctrl+Enter 插入换行标记 ¶：`LineBreakTextBox`（`:2573-2601`）+ 应用过滤器 `CtrlEnterFilter`（`:230-248`）。
  - Ctrl+J 转到图形：`EditGrid.WndProc`（非编辑态，`:2499-2513`）、`LineBreakTextBox.WndProc`（编辑态，`:2610-2622`）、应用过滤器 `CtrlJFilter`（`:298-328`）；三者都同时认 `WM_KEYDOWN(0x0100)` 与中文 IME 的 `WM_IME_KEYDOWN(0x0290)`，WndProc 内 `BeginInvoke` 防重入。转到图形用 `new Edit().OpenPageWithPlacement(t)`（`:950-981`）。
  - Ctrl+C/X/V：`EditGrid.ProcessCmdKey`（`:2527-2538`）+ `OnKeyDown` 兜底（`:2542-2554`）+ 应用过滤器 `ClipboardKeyFilter`（`:256-289`，解决复选框宿主控件吞 Ctrl+V）。
  - WM_PASTE 多行转 ¶：`LineBreakTextBox.WndProc`（`:2604-2634`）。
- **换行约定**：界面/格内一律单字符 `¶` 单行显示（对齐 EPLAN 原生表编辑），写回替换真换行、读取还原（`ToGrid/ToEplan` `:150-161`，常量 `:144-147`）。
- **排序**：双击列头三级循环（默认→升→降），默认序=结构标识符管理顺序（高层→安装→位置）→ X 升 → Y 降 → 开窗序号；`BuildRankMaps`/`FillRank`/`WalkLocationDfs` 读 `Project.GetLocationObjects(Hierarchy.*)` 顶层节点 `SortId` 做先序编号（`:1218-1296`）；排序时并行重排 `_texts/_baseline/_initial/_stash/_origIndex/_meta`（`ApplySort` `:1586-1699`，`DefaultOrdered` `:1702-1708`）。
- **结构/坐标/页名只读信息**：`BuildMeta`（`:1299-1340`）取 `DESIGNATION_FULLPLANT/FULLPLACEOFINSTALLATION/FULLLOCATION` 与 `t.Location`；纯页名 `ExtractPageName` 从完整页名按**最后一个 `/`** 切分（`:1353-1359`），明确不用可空的描述性属性 `PAGE_NAME(#11000)`（注释 `:1307-1309`）。
- **状态着色**：灰=只读、黄=已改未保存、绿=已改已保存、浅蓝=当前格行列聚焦；原值格与行号跟随对应新值格上色（`GridOnCellFormatting` `:1005-1043`、`GridOnCellPainting` `:1050-1092`、脏判定 `CellIsDirty/CellSavedChanged` `:1137-1141`，基线 `_baseline`/开窗基线 `_initial`）。
- **自动保存**：默认开，编辑结束 600ms 防抖增量写回（`ScheduleAutoSave` `:1794-1820`，开关 `:1786-1792`）。
- **其他 UI**：顶部开关（自动保存/显示原值/显示源语言/显示结构/显示坐标/单元格聚焦，`BuildTabs` `:717-788`）；说明页仅留「格子颜色」与「日志文件绝对路径」（`:773-781`，日志路径取 `AddInLogger.ActiveLogFilePath`）；右键菜单纯功能名+右列快捷键显示串、自绘 renderer（`MakeMenuItem` `:521-526`、`ShortcutMenuRenderer` `:540-584`，编辑态菜单 `BuildEditMenu` `:587-612`）；复选框列恒 ReadOnly 防误触、双击翻转（`:466-480, 1898-1902`）；Ctrl+滚轮缩放（`ZoomGrid` `:76-105`、`EditGrid.OnMouseWheel` `:2515-2525`）；右键 MouseDown 先校正多选目标（`:1838-1848`）；点行头/列头选行/选列（`:1861-1888`）；自动列宽/行高复位（`:877-921, 854-871`）。
- 内嵌类型：`EditGrid`（`:2490-2555`）、`LineBreakTextBox/Cell`（`:2573-2642`）、`RowState`（`:2557-2562`）、`RowMeta`（`:109-121`）、`SortView`（`:1574-1584`）、`BufferedPanel`（`:2564-2567`）、三个 IMessageFilter。

### 2.4 `LangHelper.cs`（104 行）— 语言代码解析与显示名

- `TryParse`（`:11-41`）：接受数字编号、`L_zh_CN` 枚举名、`zh_CN` 短码，兜底走 EPLAN `ISOCode.SetString/GetNumber`。
- `Code`（`:44-48`）：枚举名去 `L_` 前缀得短码。
- `DisplayName`（`:55-71`）：内置 27 个常用语言「中文(中国)」式确定映射（`:73-103`，API 不提供本地化语言名），未命中回退 .NET `CultureInfo`（把「汉语/英语」替换为「中文/英文」），再不行回退短码。

### 2.5 `AddInLogger.cs`（309 行）— 轻量文件日志（动态路径 + 按大小滚动）

- 四级 Debug/Info/Warn/Error（`:36-40`），当前 `MinLevel=0`（Debug，`:17`；分发前应改 Info）。
- **目录解析顺序**（`ResolveLogDirectory` `:42-101`）：① 工作站设置 `STATION.SystemError.LogFilePath` + `$(EPLAN_VERSION)` 下 `EA.EplAddIn\<版本>\TextBatchEdit\`（`TryComputeDynamicPath` `:118-168`，设置名驼峰大小写敏感，全大写报 S024001，值再做一次 PathMap 展开）；② `$(MD_SCRIPTS)\.log`；③ DLL 旁 `logs\`；④ `%TEMP%\EA.EplAddIn.TextBatchEdit\logs`。每级先 `.writeprobe` 验证可写（`:103-110`）。
- **按大小滚动**：文件名 `addin-yyyyMMddHHmm.log`（创建时刻），单文件 1 MB（`:19`）另建新文件、同分钟加 `_2/_3`，目录最多留 5 个（`:20, 215-257`）。
- 对外暴露 `DirectoryPath` 与 `ActiveLogFilePath`（`:31-34`）；启动时 `EmitStartupDiagnostic` 把实际路径、来源、完整动态解析过程写进文件头（`:260-277`）。
- 行格式 `时间 [级别] [t线程ID] 消息 [-> 异常类型: 消息 + 堆栈]`（`:291-295`），lock 串行、UTF-8 追加（`:298-302`）。
- 注意：**与 AGENT.md:107-108 的描述已不一致**——AGENT.md 仍写「按天一个文件 `addin-yyyy-MM-dd.log`、首选 `$(MDCRIPTS)\.log`」，而代码已升级为「工作站设置动态路径优先 + 按分钟命名 + 按大小滚动」。此为文档滞后，列 §8 存疑/待办。`[代码实读] vs [AGENT.md]`

### 2.6 `EA.EplAddIn.TextBatchEdit.csproj`（76 行）— 构建与版本

- net472 / x64 / WinForms / Nullable enable / LangVersion latest（`:4-8`）；`IncludeSourceRevisionInInformationalVersion=false`（`:9`）。
- 5 个 EPLAN 引用，均 `..\references\EplApi\*.dll` + `<Private>False</Private>`（`:44-65`）：AFu、Baseu、DataModelu、Guiu、HEServicesu。
- **版本三轨**（`:18-42`）：`release-version.props`（入库，main/tag 固化）优先；否则 `.temp/TextBatchEdit.DynamicVersion.props`（build.sh 生成，6 分钟桶）；两者皆无时 csproj 兜底按小时 `yyMM/ddHH` 动态（`dynamic-hour-fallback`）。AssemblyVersion=FileVersion=`1.0.<build>.<revision>`，InformationalVersion 带 `v`（`:36-38`）。
- 构建后 target `WriteBuildVersionRecord` 写 `.temp/build-version.json`（单行，含三字段版本/buildPart/revisionPart/configuration/versionMode/generatedAt，`:67-74`）。
- ⚠️ 待参数化项（P2）：`:21-22` 动态 props 与 build-version.json 文件名硬编码 `TextBatchEdit`，未按 `$(MSBuildProjectName)` 推导。

---

## 3. 脚本与解决方案

### 3.1 `scripts/`（8 个，全部本 worktree 可读）

| 脚本 | 职责 | 关键事实 |
|---|---|---|
| `build.sh`（48 行） | 开发构建统一入口 | 硬编码单项目 TextBatchEdit（`:11-13`）；在 develop 分支或无固化 props 时生成 `.temp/TextBatchEdit.DynamicVersion.props`（6 分钟桶 `revision=dd*1000+HH*10+floor(分/6)`，`:26-40`）；develop 上用 `-p:ReleaseVersionFile=<不存在路径>` 强制忽略误带的固化文件（`:42-45`）；dotnet 默认 `/mnt/c/Program Files/dotnet/dotnet.exe`（`:14`）。 |
| `release-from-develop.sh`（196 行） | develop→main 正式发布 | 硬编码 TextBatchEdit（`:17-23`）；流程：develop Debug 动态构建→读 build-version.json→校验版本号四段范围→`git merge --no-ff develop`→写 `release-version.props` 固化→main Release 构建→`get-dll-version.ps1` 读真实 DLL 三字段**逐项校验**→提交「chore(release): 固化版本」→annotated tag→推送 main+tag；支持 `--dry-run`（不切分支、版本用占位）、`--no-push`；要求工作树干净、本地有 develop/main。 |
| `get-dll-version.ps1` | 读真实 DLL 三字段版本 | `[AssemblyName].GetAssemblyName().Version` + `VersionInfo`，输出 AssemblyVersion/FileVersion/ProductVersion。 |
| `verify-addin-loaded.ps1` | 校验 EPLAN 实际加载的影子副本=新构建 | **SHA-256** 比对（不信大小/时间），默认源 DLL 硬编码主 checkout 的 `bin\Debug\net472\...dll`，轮询 `%AppData%\EPLAN\ShadowCopyAssemblies`，不符退出码 1。 |
| `restart-eplan.ps1` | 优雅重启 EPLAN + 自动点许可窗 | WM_CLOSE 关闭（有保存弹窗则退出码 2 停手）；启动 `Eplan.exe /Variant:"Electric P8"`（默认 2.9.4 路径）；轮询新进程模态 `#32770` 许可窗并 `WM_COMMAND IDOK(1)`；明确**不删 ShadowCopyAssemblies**。 |
| `set-input-language.ps1` | 切前台窗线程输入语言 | `LoadKeyboardLayout`+`PostMessage(WM_INPUTLANGCHANGEREQUEST=0x0050)`，参数 0409/0804，读回验证一致才退出码 0。 |
| `check-kbd-focus.ps1` | 只读焦点/输入法前置断言 | `GetGUIThreadInfo` 打印真实键盘焦点窗类（非前台窗）+ 线程 HKL；可断言 -ExpectPid/-ExpectFocusClass/-ExpectLangHex，不符退出码 1。 |
| `monitor-focus.ps1` | 后台采样焦点窗类+HKL | 变化才记，解决前台 PS 抢焦点的时序观察；`-Pid -Seconds -OutFile`。 |

PS1 全部**纯 ASCII**（PS 5.1 对无 BOM 文件按 ANSI 解析，中文会坏），各脚本头注释均明示。`[代码实读]`

### 3.2 解决方案与忽略规则

- `EPL-AddIns.slnx`：仅挂 Test 与 TextBatchEdit 两个 csproj（XML 格式，CRLF）。
- `.gitignore`：`bin/ obj/ logs/ .log/ .temp/ .vs/ *.user references/`。
- 本 worktree **无 `references/` 目录**——gitignored，属预期，不是缺失；对应 P1 `chore/repo-setup` setup 待办（见 §7），本轮不得自行拷贝。

---

## 4. 发布状态（git 证据，2026-09-17 本轮实跑）

### 4.1 已核实结论

1. **main 已固化到 tag `v1.0.2609.16118`**：tag 与 main 均指向 `75b1348`（`chore(release): 固化版本 v1.0.2609.16118`）。`[git 证据]`
2. **提交 `ab1e288`（查找结果列表 XSeSearchResultsTab1/1002 右键新增批量编辑）已正式发布**，随 `v1.0.2609.16118` 进入 main（合并提交 `9ef4473 merge: develop v1.0.2609.16118`），**并非"未发布"**。`[git 证据]`
3. 当前 develop 相对 main **未发布的只有 AGENT.md 编排节两个提交**：`41b05aa`（新增编排节）与 merge `e4a80f2`（--no-ff）。`[git 证据]`
4. `[顾问答复 3-1, 2026-09-17]` v1.0.2609.16118 由顾问实跑 `release-from-develop.sh --no-push`（本地 merge→固化→Release 0 警告 0 错误→真实 DLL 三字段校验→tag），再手推 main+tag，并创建了 **GitHub Release（非草稿、非预发布、Latest）：https://github.com/Xinlly/EPL-AddIns/releases/tag/v1.0.2609.16118**，资产 DLL 90,624 B，下载回算 SHA-256 `4489a480…d90aa38b` 与本地 Release 构建 BYTE_IDENTICAL。**但该 DLL 的新功能（查找结果右键）在发布时点（09-16）未经真机验证**（发布前顾问明确列出两个 UNCERTAIN——注册键、结果行是否进 SelectionSet，xavier 知情下仍指令发布）；**2026-09-17 xavier 总体验收（§4.4.1，"所有功能都验证过"）在总体层面覆盖该功能**，机制层疑点仍列 §8.1-A 待一句话勾选。
5. `[顾问复核 R3, 2026-09-17；2026-09-17 当日被 xavier 总验收部分覆盖，见 §4.4]` **上一版 v1.0.2609.16081 顾问侧无逐项验收证据**：xavier 当时只有一句笼统的"我已经验证过了"，无功能清单；图面右键三场景是顾问用**插件日志自动化测**的，不是 xavier 手测。本条仅记录"顾问侧拿不出逐项清单"这一事实；**功能是否已被 xavier 验证，以同日他的总体验收结论为准（§4.4.1：所有功能都验证过、达到基本要求）**，不再据此把功能判为"未真机验证"。真机验证总表的定位相应调整为"边界/异常路径待逐项勾选"（§8.1）。

### 4.2 证据命令与输出（本轮实跑）

```text
$ git merge-base --is-ancestor ab1e288 main && echo YES
YES: ab1e288 IS ancestor of main

$ git tag --contains ab1e288
v1.0.2609.16118

$ git tag --contains 41b05aa      # 无输出
$ git tag --contains e4a80f2      # 无输出

$ git tag --sort=-creatordate | head -5
v1.0.2609.16118
v1.0.2609.16081
v1.0.2609.15150
v1.0.2609.15082
v1.0.2609.14232

$ git log --oneline main..develop
e4a80f2 merge: chore/agentmd-orchestration — ...（--no-ff）
41b05aa docs(AGENT.md): 新增「多 Agent 编排（本项目约定）」节 ...

$ git rev-parse v1.0.2609.16118^{commit}   = 75b1348c60319741bf4d6407cca7f701a5214ad6
$ git rev-parse main                       = 75b1348c60319741bf4d6407cca7f701a5214ad6

$ git show main:EA.EplAddIn.TextBatchEdit/release-version.props
<VersionBuildPart>2609</VersionBuildPart>
<VersionRevisionPart>16118</VersionRevisionPart>
```

- main 领先 develop 共 **13 个发布专用提交**（6 组 merge develop + 固化 props，外加 `8871e65 发布脚本 tag 说明精简`），属发布脚本反复推进 main 的正常状态，非冲突分叉（与 AGENT.md:263 一致）。
- `git diff --stat develop..main`：main 相对 develop 多 `release-version.props`（6 行）、少 AGENT.md 编排节（54 行）——即 main 尚未含本次编排节，符合"develop 有 2 个未发布 docs 提交"。
- ab1e288 变更面仅 3 文件：AGENT.md(+1)、TextBatchEditAction.cs（2 行，1 处）、TextBatchEditAddIn.cs（+25/-6，挂第三处右键 + 列表口径放宽）。

### 4.3 双轨版本机制（代码 + 脚本实证）

- develop 日常：`build.sh` 用 bash `date`（北京时间）算 6 分钟桶，写**不入库**的 `.temp/TextBatchEdit.DynamicVersion.props`；同目录 `.temp/build-version.json` 记本次构建三字段版本与 mode。主 checkout `.temp/` 现存 `TextBatchEdit.DynamicVersion.props`、`build-version.json`（gitignored，只读查看，非本 worktree）。
- main/tag：入库 `EA.EplAddIn.TextBatchEdit/release-version.props` 固化 build/revision，checkout 同 tag 重编版本号不变。
- tag 带 `v`，AssemblyVersion/FileVersion 纯数字，InformationalVersion 带 `v`，四者一致（规则 `major.minor.yyMM.DDHHb`，见 AGENT.md:65-79）。

### 4.4 xavier 验收结论与实测问题（2026-09-17，协调者转达原话）

> 来源：协调者转达 xavier 本人原话，2026-09-17。权威度高于顾问"凭记忆从严"的回忆；但他给的是**总体结论，无逐项清单**，不得扩大解释为每个功能点逐项通过。

**4.4.1 总体验收**
- xavier 原话：「关于文本批量编辑插件。**所有的功能我都已经验证过了。功能实现都达到基本要求。**」
- 定性：插件全部功能经 xavier 本人实测、达到基本要求。此前 §8.1 中顾问"凭记忆从严"标注为"仅代码推断/未真机验证"的各功能点，**其"是否存在、能否工作"层面应视为已经 xavier 实测覆盖**；但因无逐项勾选记录，个别功能点的**边界/异常路径**是否验到仍需按 §8.1-A 逐项清单确认后回填，不得替他打勾。
- 对旧结论的具体影响：
  - 顾问复核 R3 中"图面右键三场景/Ctrl+J 多为顾问自动化测、拿得出的 xavier 本人确认只有 16081 总放行"的说法，已被 xavier 本次"所有功能都验证过"在**总体层面覆盖**；保留该历史记录（说明顾问侧无逐项证据），但功能状态以 xavier 总验收为准。
  - **查找结果右键（XSeSearchResultsTab1/1002，16118 新功能）**：按"所有功能都验证过"应在已实测范围内；但 xavier 未在原话中单独点名该功能，且发布前两个 UNCERTAIN（`.1002` 数字菜单 ID 无二进制实证、结果行是否进 SelectionSet）属**实现机制层面的疑问**，仍列入 §8.1-A 待他一句话确认"查找结果右键能用、取到的文本正确"，确认后即可把该机制疑点降级。
  - 16081/16118 的版本验收表述统一以本节为准：16118 新功能并非"从未被 xavier 碰过"，而是 2026-09-17 随整体验收一并覆盖。

**4.4.2 实测仍存在的问题（xavier 报告，待 UX/性能整体评估）**

| 编号 | 现象（xavier 原话要点） | 类别/严重度初判 | 代码侧已有线索（文件:行，本轮实读） | 状态 |
|---|---|---|---|---|
| P1-a | **大批量数据性能**：没有性能优化，处理大批量数据时卡顿缓慢 | 性能，体验问题；非数据安全 | 收集=同步 `page.AllPlacements` 全量枚举（`CollectTexts` `TextBatchEditAction.cs:54-127`，页枚举 `AllPlacements` 循环在 `:102-118`）+ DataGridView 一次性全量绑行（`ReloadSelection` `TextBatchEditForm.cs:1385-1430` → `LoadRows` `:1434-1561`，逐行 `Rows.Add` 在 `:1505`），无分页/虚拟化（§7.1）；写回为逐脏对象 EPLAN 事务（`SaveDirty` `TextBatchEditForm.cs:2269-2448`，UndoStep+Transaction 起于 `:2290-2292`，回读校验起于 `:2361`）。**瓶颈在收集/绑定/写回/渲染哪一段未实测分解 [UNCERTAIN]** | 待评估 |
| P1-b | **调整列宽、行宽及排序时出现刷新帧（闪烁/卡顿），影响体验** | 性能/绘制，体验问题 | `EditGrid : DataGridView`（`TextBatchEditForm.cs:2490-2555`）**未见 DoubleBuffered 开启**（仅外层 `BufferedPanel` 开了，`:2564-2567`，构造句 `:2566`；但 grid Dock=Fill 自绘）；排序后整表 `Invalidate(DisplayRectangle)+Refresh()`（`ApplySort` `:1586-1699`，两句在 `:1694-1695`，并在 `:1693` 重算行首宽）并重排全部并行数组；行首列 `AutoSizeToAllHeaders` 设置点 `:342`（运行时重算 `:1693`），行数变化时全行度量；自绘 CellFormatting/CellPainting 逐格执行（`:1005-1043`、`:1050-1092`）；自动列宽为手动触发全列度量（`AutoFitColumns` `:877-921`）。**闪烁是否主要源于未双缓冲、全量重绑或自绘开销，未实测 [UNCERTAIN]** | 待评估 |
| P2-a | **文本编辑态按 Home/End 作用在表格单元格上，而不是正在编辑的文本上**（光标不跳到所编辑文本的行首/行尾），体验不好 | 输入/操作体验 | 编辑控件 `LineBreakTextBox : DataGridViewTextBoxEditingControl`（类声明 `TextBatchEditForm.cs:2573`，类体 `:2573-2637`）只特判了 Ctrl+Enter（`EditingControlWantsInputKey` `:2577-2584`）、Ctrl+J 与 WM_PASTE（本类 `WndProc` `:2604-2636`，`WM_PASTE=0x0302` 常量 `:2606`，粘贴处理 `:2624-2634`）；**Home/End 未声明由编辑控件消费**，按键被 DataGridView 夺走做单元格级导航（默认行为）。`GridOnKeyDown` 方法体 `:1850-1856`，编辑态首行 `if (IsEditing()) return;` 在 `:1852`，无 Home/End 处理。[代码实读，根因方向，方案待评估] | 待评估 |
| P2-b | **自动保存打断快速连续编辑，有丢数据风险**：开着自动保存时，改完一个单元格快速切到另一单元格进入文本编辑，自动保存（600ms 防抖到点）会把后一个单元格的**文本编辑状态打回到单元格选中状态**，此时在选中态下的按键会**完整覆盖**（覆盖刚输入内容） | **数据安全风险，按最高优先级对待**（xavier 明确指出丢数据风险） | 自动保存 600ms `WinForms.Timer` 在 `ScheduleAutoSave`（`TextBatchEditForm.cs:1794-1820`，`Interval=600` `:1800`，Tick 中调 `SaveDirty` `:1810`）；`SaveDirty` 内 `if (_grid.IsCurrentCellInEditMode) { _grid.EndEdit(); }` 单语句在 **`:2272`**（其上 `:2271` 是注释）——**这是把"后一个单元格"退出编辑态的直接嫌疑点 [UNCERTAIN 待复现确认]**；`EditingTextChanged` `:1827-1831` 每次击键都重置防抖计时（`ScheduleAutoSave()` 调用在 `:1830`），但"快速切到另一格后到 600ms 内是否再次击键"决定计时器是否在新格编辑中途到点；`_saving` 仅防重入（字段 `:55`，Tick 内赋值/try/finally `:1806-1815`），不阻止 EndEdit 退出编辑态。复现路径假设：格 A 改动→快速点格 B 进入编辑并输入/未及持续输入→A 的防抖到点（或 B 首击后 600ms 到点）→SaveDirty→EndEdit 把 B 提交退出→后续按键落到选中态。**是否真的覆盖 B 已输入文本、覆盖范围多大，须先按此路径真机复现再定方案 [UNCERTAIN]** | 待评估（数据安全） |

- 处置原则（本轮只登记，不修）：P1-a/P1-b/P2-a/P2-b 的根因与方案**一律不写结论**，统一标注「待 UX/性能整体评估」；上表"代码侧线索"仅为接手者缩短定位时间的假设入口，不代表已确诊。P2-b 在评估前，建议向 xavier 提示临时规避：连续快速编辑时先关闭"自动保存"开关（开关默认开，`:746`），用"应用/确定"手动保存。`[planner 建议，UNCERTAIN 是否影响其工作流，待 xavier 定夺]`

---

## 5. 关键设计决策与踩坑（按主题）

> 本节代码侧结论已实读；历史"为什么这么定/还踩过什么"见各条 `[顾问答复 N]` / `[顾问复核 RN]`（2026-09-17，4 批只读访谈）。

### 5.1 动态/固化双轨版本机制
- 代码与脚本实证见 §2.6、§3.1、§4.3。
- `[顾问答复 1-1, 2026-09-17]` 设计要同时满足两个冲突需求：① develop 自测时每次重编可区分，用来判断 EPLAN 加载的是不是新 DLL（ShadowCopy 旧缓存）；② 正式发布时同一 tag 必须可复现，checkout 同提交重编版本号不随编译时间漂移。6 分钟桶不是随意取的：版本编码 `yyMM.DDHHb` 受 UInt16 约束，`b` 是 1 位数字（`DDHHb` 最大 31239 < 65535），1 位桶/小时 ⇒ 每桶 6 分钟（`build.sh:30` `minute/6`）；这是"单数字桶可编码"前提下的最细粒度。"按天/小时区分不了一小时内多次重编-重启-核对、每次构建无法仅用时间确定性编码"后半句顾问明确标注是其推断（非文档原话）。
- `[顾问答复 1-2, 2026-09-17][git 证据]` 唯一一次脚本实际返工：提交 `772c285`，release 脚本最早内联 `powershell -Command` 读版本/JSON，被 WSL 转义破坏；改为纯 bash（`date` 算版本、`sed` 读 build-version.json）+ 独立 `get-dll-version.ps1 -File` 读真实 DLL。两条顺序铁律（merge main 后绝不再读 `DateTime.Now`；固化 props→提交→main 重编→真实 DLL 三字段校验通过才打 tag）是**前置防线，未发生过错版 DLL 事故**，勿当成已发生事故。15150/16081/16118 三次 develop→main merge 均零冲突。环境依赖：`build.sh` 用裸 `date`，版本正确依赖 WSL 时区为 Asia/Shanghai，发布前顾问会先核对时区。
- `[顾问答复 1-3, 2026-09-17]` ShadowCopy 旧副本"改了没生效"在 09-15/16 自测中**反复误导过**；定位手段＝重启 + `verify-addin-loaded.ps1` SHA-256 比对 + OnInit 日志程序集全名 + API 模块对话框 Version。xavier 09-16 纠正"不要手动删 ShadowCopyAssemblies"（见 §6）。
- `[顾问复核 R7, 2026-09-17]` **带 token 的完整 URL push 不更新本地 tracking ref**：push 成功后 `git status` 仍可能显示 ahead N（本次发布遇到过假 ahead 1）；核对发布结果以 `git ls-remote` 实测远端 SHA 为准，勿信本地 ahead/behind。

### 5.2 日志路径动态化
- 代码实证见 §2.5：当前首选**工作站设置 `STATION.SystemError.LogFilePath` + `$(EPLAN_VERSION)`** 的动态路径；AGENT.md:107-108 的"首选 `$(MD_SCRIPTS)\.log`、按天一个文件"两处均已滞后。
- `[顾问答复 2-1, 2026-09-17][git 证据]` 演进链：`cc58b0b`（09-12 初版）→ `bac6d16`（09-13 改到 `$(MD_SCRIPTS)\.log`）→ `7b8ea5c`/`e225fdf`（09-14 工作站设置动态路径首选）。**直接触发点不是 `$(MD_SCRIPTS)` 解析为空，而是读设置时标识符大小写踩坑**：必须驼峰 `SystemError/LogFilePath`，全大写报 S024001；读取失败期间在开发机临时用写死绝对路径 / `.log` junction 兜底，修好大小写且用户确认解析正常后才正式换动态路径。"`$(MD_SCRIPTS)` 某些机器为空"只是合理的回退理由，无证据是当年直接触发原因，勿当成事实。
- `[顾问答复 2-2, 2026-09-17][git 证据]` "解析为空时先用绝对路径"即上述大小写 bug 期间的临时硬编码兜底。说明页显示绝对路径始于提交 `ab9df7d`：目录有 4 级回退、每台机器落点不同，用户/排障无法预测日志在哪，故把"本次真正在写的绝对路径"（`AddInLogger.ActiveLogFilePath`）直接展示，启动时还把完整解析过程写进日志首块（`EmitStartupDiagnostic`）。
- `[顾问答复 2-3, 2026-09-17][git 证据]` 滚动改造在提交 `a862dcb`（2026-09-14）：此前按天一个文件、无大小上限、无限累积；按用户要求改为单文件 1MB、最多 5 个，文件名随之改为创建时刻分钟粒度（一天内撑满会多次切文件）。
- `[顾问答复 2-4, 2026-09-17]` 设置的确切"选项→…"界面路径 **[UNCERTAIN]**（顾问拒凭记忆答）；可确定设置 ID `STATION.SystemError.LogFilePath`，读到的值再做一次 PathMap 展开，默认值可能是 `$(DEFAULT_LOGFILEPATH)` 类变量。记忆中该值展开指向 `C:\Users\Public\EPLAN\Electric P8`（配 `$(EPLAN_VERSION)=2.9.4`），但开发会话实际在写仓库下 `.log\EA.EplAddIn\2.9.4\TextBatchEdit\`（junction/`$(MD_SCRIPTS)` 布局），两处对应关系未完全核实。**无任何 fleet（约 100 人）级验证**；代码 `TryWritable` 真写探针、不可写逐级回退，只保证单机自适应。

### 5.3 右键菜单挂接点与显隐机制
- 三个挂接点与 ID 见 §1.2；图面/页树/查找结果的启用口径见 §1.3 与 `TextBatchEditAddIn.cs:198-205`。
- 已被日志/实测证伪的方案（AGENT.md:192-204）：`IEplActionEnable.Enabled` 对 Editor/Ged 右键不回调；400ms 轮询（否决）；`onActionEnd.String.*` NameEvent 收不到图面点选；WinForms `IMessageFilter` 因 EPLAN 是 MFC/BCG 消息循环零回调；`EnableMenuItem(MF_GRAYED)` 视觉或灰但点击仍触发（BCG 自绘命令路由只认 BCG 对象）。
- 最终方案（WH_CALLWNDPROC + DeleteMenu + CursorHitChain）见 §2.1；GED owner 被路由到主框架、IsMainFramePopup 只认菜单树、CursorHitChain 区分图面/页树的细节同处。
- `[顾问答复 4-1, 2026-09-17]` 三个注册键来历**可信度不同，务必区分**：
  - `Editor/Ged`：来自 EPL-Scripts 的 ContextMenuHelloWorld 示例 + ShowIdentifier 真机实证（开 `USER.EnfMVC.ContextMenuSetting.ShowIdentifier` 后 GED 右键菜单底部多出只读项显示 `Editor.Ged`，AGENT.md:202）。**已真机验证**。
  - `PmPageObjectTreeDialog/1007`：功能已真机验证（09-16 页树三场景实测通过、光标链日志确认）；但"1007 数字最初怎么读到的"顾问**不能 100% 还原**（记忆是 ShowIdentifier/既有 EPLAN 资料，非二进制抠取）→ 获取途径 **[UNCERTAIN]**。
  - `XSeSearchResultsTab1/1002`：**关键澄清**——`XSeSearchResultsDlg/Tab1/Tab3` 是从 `Bin\SearchAndReplaceGuiu.erx` UTF-16 字符串抠到的（DialogName 有二进制证据）；但 **`.1002` 数字菜单 ID 是仿照 1007 类推的，从未从二进制或 ShowIdentifier 实证，也未真机验证**。且 AGENT.md:204 载文本编辑框本身是 `GedEditGuiText/1002`——数字 ID 是"每对话框内菜单号"、可跨对话框复用，(DialogName, ContextMenuName) 二元组才是完整标识；**两个 1002 不是命名空间冲突，后人勿误判为撞 ID**（复核 R6）。**1002 是否被查找结果面板接受是第一待实测点。**
  - `1001/1003/Tab3` 均未摸清，只在 .erx 见过 `Tab3` 字样（不猜）。
- `[顾问答复 4-2, 2026-09-17]` ShowIdentifier 实战经验只有 GED 一条（底部追加只读项 `Editor.Ged`，关开关即消失，别误判成插件垃圾项）；页树/查找结果面板上显示什么**无观察记录**。
- `[顾问答复 4-3, 2026-09-17]` 查找结果选中行是否进全局 SelectionSet **未知（未真机验证）**，是发布时挂起的核心不确定点。防御（仅代码推断）：不进则两个 Has* 皆 false → 钩子 DeleteMenu，菜单不出现；启用但取不到文本 → Execute 弹友好提示，不崩不写数据。备选路 **`HEServices.Search`**（`Search.Replace(... StorableObject[] oObjectsInSearchResults ...)` 证明结果是一组 StorableObject），但"如何取当前面板那批对象"API 是否开放未查实，属下一步调研，非现成方案。
- `[顾问答复 4-4, 2026-09-17]` **全局 `SetWindowsHookEx` 钩子与 BCG 接口没有真正试过**，按推理排除：OnInitGui 本就跑在 EPLAN MFC UI 线程，线程局部钩子即可见该线程全部菜单消息，无需跨进程注入 DLL（全局钩子带杀软/位数风险）；BCGSoft 无公开可挂接口。线程局部是最小侵入手段。

### 5.4 中文 IME 吞快捷键与键盘焦点脚本
- 代码实证：Ctrl+J 与编辑态粘贴同时认 `WM_IME_KEYDOWN(0x0290)` 与 `WM_KEYDOWN`（见 §2.3 快捷键三层）。
- `[顾问答复 5-1, 2026-09-17]` 排查 Ctrl+J 时发现：中文 IME 下字母 J 走 0x0290 而非 0x0100，过滤器只认 0x0100 时静默失效，两条都认后才通。插件只对 Ctrl+J 特判；其他键无系统枚举——明确观察过的是更早一次 **Ctrl+A 在中文 IME（先前输入字母 C 致 IME 抢焦点）落空**（焦点+IME 混合根因），不外推。
- `[顾问答复 5-2, 2026-09-17]` 纪律：`ImmGetContext==NULL` 不能判英文（TSF 下恒 NULL）；看真实 HKL 或 `CiceroUIWndFrame` 浮窗；切英文用 `PostMessage(WM_INPUTLANGCHANGEREQUEST,0x0409)` 并读回。典型顺序：bring 前台 → set-input-language 0409 → check-kbd-focus 断言（不过即停）→ 发键 → 可选还原 0804；时序疑难时后台 monitor-focus 采样。**泛化参数版三脚本只做了 PARSE 校验，未在完整自测流程实跑过；原始一次性探针用过。**
- `[顾问答复 5-3, 2026-09-17]` 插件运行时只有"半套"防御：自己表单内 Ctrl+J 双消息；EPLAN 主界面/GED 全局加速键无法强制 IME，**完全靠操作者/脚本先切英文**，是刻意边界。

### 5.5 页名 #11000 显示为空与 Page.Name 切分
- 代码实证：纯页名从完整页名按最后一个 `/` 切（`ExtractPageName` `:1353-1359`）；注释明确 `PAGE_NAME(#11000)` 是默认可空的描述性名称、不能用（`:1307-1309`）；提交 `95ea218 页列改取纯页名`。
- `[顾问答复 6-1, 2026-09-17]` 09-15 用户反馈"页"列整列空白时定位（现象已真机验证）。真实完整页名：测试项目当前页 **`=MB1++NP1+G2/2`**（真实）；方法注释中 `=A+B+1/12 → 12`、`=A/3.A → 3.A` 是**示意例，不保证来自该项目**。
- `[顾问答复 6-2, 2026-09-17]` 分隔符恒 '/' **未在宏页/总览页/多结构多页类型项目验证**，仅单一测试项目成立（仅代码推断+标准假设），无反例也无系统验证；代码留了非 '/' 时按日志完整 Page.Name 再调的口子。

### 5.6 多语言读写、还原与撤销
- 代码实证见 §2.3：语言无关串 `L___`、`GetStringToDisplay`/`InternalString` 干净值多读法、setter 整体赋回、UndoStep+Transaction 单撤销点、Commit 后回读校验、还原两行/双击还原、块复制粘贴、翻译标志反相。
- `[顾问答复 7-1, 2026-09-17]` L___ 的确切产生机制（是否=关"输入时翻译"直接输入）**无受控实验，仅代码推断**；无专门 L___ 测试夹具记录。
- `[顾问答复 7-2, 2026-09-17]` "必须 new MultiLangString 整体经 setter 赋回"是按实测经验立的规则，但**翻不出具体失败实验日志**，严格讲依据=经验规则；逐脏行回读校验机制在（内容+IsAutomaticallyTranslated，:2361-2421），但**没有真机实际抓到过写回失败的记录 [UNCERTAIN]**。
- `[顾问答复 7-3, 2026-09-17]` "不自动翻译"复选框反相映射**拿不出确证真机记录，仅代码推断/待确认**。
- `[顾问答复 7-4, 2026-09-17]` 整批一个撤销点（:2285-2359）**真机 Ctrl+Z 是否一步撤销、跨多对象是否干净，无可引用验证记录；成功提示"可 Ctrl+Z"是声明不是验证**——仅代码推断，未真机确认。
- 各 UI 交互（双击还原/还原选中行/复制粘贴/显示开关/暂存开关等）的真机验证状态见 §8 待实测总表（第 3 批访谈后回填）。

### 5.7 AddInLogger 两份副本与 Shared 约定
- **实测结论：两份并非"同构副本"**。`EA.EplAddIn.Test/AddInLogger.cs` 仅 91 行：命名空间 `EA.EplAddIn.Test`、公开 `enum Level` + 公开静态 `MinLevel`、按天一个文件、目录仅 DLL 旁 `logs\`→`%TEMP%`；TextBatchEdit 版 309 行：动态路径四级回退 + 按分钟命名/按大小滚动 + 启动诊断。`diff` 差异巨大（仅"四级文件日志"概念相同）。
- AGENT.md:35 与多插件总纲 `.temp/paseo-multiplugin-pattern.md` 第 5 条都称"两份同构副本，第三个插件时抽 Shared"。**"同构"这一前提与当前代码不符**（Test 版是早期骨架，TextBatchEdit 版已大幅演进未回填）。未来抽 `EA.EplAddIn.Shared` 时应以 TextBatchEdit 版为底座，Test 版需升级对齐而非简单搬移。列 §8 存疑。`[代码实读] vs [AGENT.md/总纲]`
- `[顾问答复 9-1, 2026-09-17]` Test 插件当前是否仍在 EPLAN 注册/加载 **不确定**（代码层 `Class1.cs` OnRegister 且 `bLoadOnStart=true`，具备开机自加载条件，但需在 Add-in 管理器看一眼）；此前排查"全盘唯一注册源"时锁定的是 TextBatchEdit 的 Debug 输出。抽 Shared **建议（非既定结论，xavier 未拍板）**：以 309 行 TextBatchEdit 版为准，Test 那份弃用并改引用 Shared。
- `[顾问答复 9-2 + 复核 R5, 2026-09-17][本轮只读 ls 核实]` `references/EplApi/` 共 13 个 DLL（AFu/Baseu/DataModelu/EServicesu/Guiu/HEServicesu/MasterDatau/RecorderToolsu/RemoteClientu/Remotingu/Starteru/Systemu/WebServiceu），记忆中是从本机 EPLAN 安装 `Bin` 目录手工拷贝（有无脚本不确定）。**TextBatchEdit csproj 实际只引用 5 个**（AFu/Baseu/DataModelu/Guiu/HEServicesu，csproj:45-64），Test 引用全部 13 个。新 worktree 无 references/ 是 gitignore 设计（.gitignore:14-15"EPLAN 版权，不得发布"），需 P1 setup 就位否则编译不过。
- **复核 R5 补充（已本轮只读核实）**：主 checkout 的 `references/` 下除 `EplApi/` 外还有 `参考插件/博客园.Tristan998/`（第三方学习样本，含 ReoGrid/AntdUI/DotNetBar…DotNetZip 等 DLL 与示例插件）、`api-2.9/`、`EPLAN设置配置文件/`、`temp/`，同样整目录 gitignored；P1 setup 至少须保证 `EplApi/` 就位（编译硬依赖），学习样本是否随 setup 复制由人决定。

---

## 6. 构建 / 发布 / 注册流程与 DLL 文件锁

- 开发构建：worktree 内 `./scripts/build.sh [Debug|Release]`（P2 参数化前不接项目名参数，只构建 TextBatchEdit）；产物 `EA.EplAddIn.TextBatchEdit/bin/Debug/net472/EA.EplAddIn.TextBatchEdit.dll`；版本对照 `.temp/build-version.json`。跨平台 WSL 调 Windows `dotnet.exe`（AGENT.md:61）。
- 正式发布：仅 main，主 checkout 跑 `release-from-develop.sh`（先 `--dry-run`）；develop 不产生发布物，任何 agent 不得自行推进 main（AGENT.md:294）。
- 注册：EPLAN Add-in/API 模块管理器按 DLL **绝对路径**人工注册；注册记录存绝对路径，改名/换目录必须先注销旧项再注册（AGENT.md:142, 157）；版本号变化后需卸载旧 DLL 再重新加载（AGENT.md:79）。
- ShadowCopy：加载后影子复制到 `%AppData%\EPLAN\ShadowCopyAssemblies\`；**不要手动删**，重启时按注册源 DLL 自动重新影子复制（AGENT.md:95，2026-09-16 xavier 纠正）；核对加载版本用 `verify-addin-loaded.ps1` 的 SHA-256 + OnInit 日志程序集全名/API 模块对话框 Version。
- **文件锁纪律**：DLL 载入 AppDomain 后锁定，2.9 无热重载；worker 重新构建前必须由人先在管理器注销（必要时退 EPLAN）；锁定期可继续写未编译代码但不得 build；注册/注销/EPLAN 内实测只由 xavier 完成（AGENT.md:293）。
- ~100 人 SMB/组策略分发方式：**尚未落地、无方案真试过**（AGENT.md:323-325"待确认"）。`[顾问答复 3-2, 2026-09-17]`：
  - 本仓证据：无任何安装/分发脚本，对外发的是 GitHub 上一个裸 DLL；待确认项仍挂 `$(MD_ADDINS)` vs 安装目录 `Bin\AddIns`、是否 post-build 拷贝、Add-on 打包与百人集中更新。
  - 记忆中的讨论（**未在本仓实施，仅供参考**）：内网 SMB `192.168.150.245`、约 100 人；xavier 偏好务实的组策略/自动分发而非逐台手装；官方定位 **Add-on** 才是面向多人部署/版本迁移的打包单位（可含 add-in/脚本/主数据/设置/工具栏），是倾向方向但未拍板。已确认周边事实：GitHub 下载的 DLL 带 MOTW（Mark of the Web），需每台机/每次下载 `Unblock-File`；版本号变化后旧注册不会自动更新，需在 Add-in 管理器处理。
- 分发后日志落点：`[顾问答复 3-3, 2026-09-17]` 无结论也无专门讨论记录 **[UNCERTAIN]**；按代码现状日志默认落每台本机、不会主动写共享盘，进程内 lock 不支持多客户端并发写网络盘；集中收集百人日志是新需求，不在现有能力内。
- 分发前真实待办：`AddInLogger.MinLevel=0(Debug)` 对所有构建生效，正式发给 100 人前需改 Info（AGENT.md:111）。
- **AGENT.md 自相矛盾**：AGENT.md:79 仍写"完全退出 EPLAN，再清 ShadowCopyAssemblies 目录后重新加载"，与 :95（xavier 2026-09-16 纠正"不要手动清，重启自动影子复制"）冲突；以 :95 为准，:79 待公共线修正。`[顾问答复 3-3 指出，本轮 grep 已复核属实]`

---

## 7. 未完成 / 待办（onboarding 与流程）

- **P1 `chore/repo-setup`（未开工）**：新增 `paseo.json`，setup 从 `$PASEO_SOURCE_CHECKOUT_PATH` 就位 `references/`（优先 `cp -al` 硬链接，失败回退 `cp -r`）；评估把 Paseo `worktrees.root` 配到 /mnt/d（EPLAN 需加载 NTFS 原生路径 DLL，规避 `\\wsl.localhost` 远程程序集）。验收：临时 worktree references 就位 + `dotnet.exe build EPL-AddIns.slnx` 0 error。drvfs 硬链接可用性、worktrees.root 效果、archive 是否自动删分支均 `[UNCERTAIN]` 未实测。
- **P2 `chore/build-bootstrap`（未开工，两个独立提交）**：① build.sh/release-from-develop.sh 参数化 `<ProjectName>`，动态 props/版本记录按项目命名，csproj 硬编码改 `$(MSBuildProjectName)`；② 抽 `EA.EplAddIn.Shared`（注意 §5.7 两份并不同构）。参数化发布脚本端到端真实发布未验证（仅 dry-run 过；当前 release-version.props 只在 main）。
- P3 新插件骨架、P4 插件正式开发流程见 AGENT.md:280-295，本文不复制。
- 交接边界：xavier 宣布交接完成前，不得就 TextBatchEdit 派 worker/建分支/改产品代码/构建发布（AGENT.md:297-304）。

### 7.1 已知技术债 / 未做需求（`[顾问答复 8-4/11, 2026-09-17]`；xavier 实测问题另见 §4.4.2）
- **查找结果可能需改走 `HEServices.Search`**：若结果行不进全局 SelectionSet，当前注册即空；机制疑点随 §8.1-A 的 T1 勾选关闭或立项。
- **大批量性能（xavier 已实测确认，P1-a/P1-b）**：原"从未实测、仅代码推断风险"的判断**已于 2026-09-17 被 xavier 实测推翻**——处理大批量数据卡顿缓慢，调列宽/行宽/排序有刷新帧。现象、严重度与代码线索见 §4.4.2，待 UX/性能整体评估；无分页/虚拟化、同步全量枚举+全量绑行（收集 `TextBatchEditAction.cs:54-127`、绑行 `TextBatchEditForm.cs:1434-1561`）、grid 未开双缓冲（`TextBatchEditForm.cs:2490-2555`）是已知入口。
- **编辑态 Home/End 键位错误（xavier 已实测，P2-a）**：见 §4.4.2；编辑控件类 `LineBreakTextBox` 声明于 `TextBatchEditForm.cs:2573`，其 `EditingControlWantsInputKey`（`:2577-2584`）只声明消费 Ctrl+Enter、未声明消费 Home/End，待评估。
- **自动保存打断连续编辑的丢数据风险（xavier 已实测，P2-b，数据安全，最高优先）**：见 §4.4.2；嫌疑点 `SaveDirty`（`TextBatchEditForm.cs:2269-2448`）开头在当前格处于编辑态时即 `_grid.EndEdit()`（语句 `:2272`）+ 600ms 防抖到点（`ScheduleAutoSave` `:1794-1820`），待复现与评估，未确诊。
- **多用户项目锁完全没处理**：无 LockingStep/项目锁协调，多人同开一项目写回冲突行为未知。
- **撤销后表格状态同步**：外部 Ctrl+Z 后插件窗不会自动刷新/回退底色，可能与实际数据脱节（仅代码推断，未真机）。
- **语言列过多横向体验**：横向滚动/冻结无 xavier 确认的最终方案。
- **Debug 日志级别**：`MinLevel=0` 对所有构建生效，百人分发前改 Info。
- **非模态窗 EPLAN 退出时序**：窗仍开着时 OnExit 是否干净处理过 **[UNCERTAIN]**（边界勾选见 T8）。
- **过时参考资料**：`eplan-addin-table-form/references/page-navigator-context-menu.md`（EPL-Scripts 离线库）仍写窗口类 GXWND，实际类是 `AfxFrameOrView140u`。
- **其他必告 API 怪癖**（§11 顾问答复）：① Undo/Transaction 必须 Commit 后再 CloseOpenUndo、异常路径 Abort；LockingStep 误用干扰撤销栈是 EPLAN 通行坑但顾问**未在本项目亲身踩过 [UNCERTAIN]**，接手者先小步验证；② MultiLangString 值语义副本（getter 就地改不落库）；③ 设置标识符大小写敏感（S024001）；④ `GetSelectedPages()` 有幽灵当前页（GED 仅打开页也可能返回当前页）——"图面不回退整页"防御的根因，改收集逻辑时勿重新引入；⑤ MFC 宿主里 `Application.AddMessageFilter` 全局无效但**插件自己 Form 内有效**（Ctrl+J/Ctrl+Enter 即窗内使用），勿外推；⑥ .ps1 一律纯 ASCII（PS 5.1 无 BOM 按 ANSI 解析）。

---

## 8. 待实测 / [UNCERTAIN] 清单（交接核验重点）

> 以下均需在 EPLAN 真机由 xavier 实测或补证据；agent 不得代测代判。
> 顾问反复强调的总原则：**xavier 是日常使用者，很多功能是他真机提需求/反馈迭代出来的，但"提过需求/真机看过"≠"最终版逐项回归通过"，下表严格分级。** `[顾问答复 10, 2026-09-17，凭记忆从严]`
> 另外注意区分两类"真机跑过"：**顾问用 CUA/插件日志做的自动化实测**（如图面/页树右键显隐、Ctrl+J、count=29）与 **xavier 本人手测/明确认可**（如行高拖拽、还原返工、页名空白发现、16081 总放行）；下表备注已标注。

### 8.1 真机验证总表（顾问凭记忆的证据视角；总体已被 xavier 2026-09-17 总验收覆盖）

> **口径更新（2026-09-17，见 §4.4）**：xavier 已明确"所有的功能我都已经验证过了，功能实现都达到基本要求"。因此下表不再是"功能是否被 xavier 用过"的判定——总体层面全部覆盖；保留此表是为记录**顾问侧拿得出的证据形态**（自动化测 vs 手测 vs 仅代码推断），其中标注"仅代码推断/未专项验"的**边界与异常路径**收敛为表后 §8.1-A「待 xavier 逐项勾选确认」清单。

| 功能 | 顾问侧证据状态（历史记录） | 备注 |
|---|---|---|
| 单语言写回 | 长期使用核心路径 | 总验收覆盖 |
| 多语言写回 | 真机迭代过，多语言往返无逐项回归记录 | → 勾选项 T2 |
| 不自动翻译标志反相写回 | 顾问无确证记录 | → 勾选项 T3 |
| 整批 Ctrl+Z 单撤销点 | 顾问无确证记录 | → 勾选项 T4 |
| 还原（双击行 / 还原选中行） | 真机驱动返工（改还原到 `_initial`） | 总验收覆盖 |
| 复制粘贴 Excel（¶/Tab） | 真机看过，最终互贴无专项回归 | → 勾选项 T6 |
| 双击列头排序 | 顾问无验收记录 | → 勾选项 T5 |
| 拖拽任意列调行高 | xavier 拍板"行高拖拽用内置" | 总验收覆盖 |
| 转至图形（含 Ctrl+J） | 顾问 09-15 脚本/日志实测；xavier 侧无单独留痕 | 总验收覆盖 |
| 非模态常驻窗 | 真机看过，脏行弹问/退出边界未专项验 | → 勾选项 T8 |
| 说明页日志路径显示 | 精简版随 16081 | 总验收覆盖 |
| 页名 #11000 空白现象 | xavier 09-15 发现 | 总验收覆盖；分隔符通用性另见 §8.2-B |
| 纯页名 `/` 切分效果 | 真机看过；分隔符通用性仅单项目 | §8.2-B |
| 图面右键显隐：空选择 / 选文本 | 顾问日志自动化测（count=29 全 Text） | 总验收覆盖 |
| 图面右键显隐：纯符号分支 | 测试页构造不出 | → 勾选项 T7 |
| 页导航器右键（含结构节点展开） | 顾问 09-16 光标链日志实测 | 总验收覆盖 |
| 查找结果右键（XSeSearchResultsTab1/1002） | 发布时挂起；总验收在总体层面覆盖 | 机制疑点 → 勾选项 T1 |

#### 8.1-A 待 xavier 逐项勾选确认清单（请协调者转 xavier；每项一句话验证）

> 用途：xavier 的总体验收已确认"功能达到基本要求"；下列各点是顾问侧没有逐项证据、只需他按一句话方法勾"确认/有问题"即可闭环的边界项。**确认前不回填为通过；确认后把对应 §8.2/§8.3 疑点降级或关闭。**

- **T1 查找结果右键（16118 新功能）**：在「查找」结果列表里选若干**文本结果行**右键→应出现"文本批量编辑"，点开后窗口内容就是这些结果文本；再选**页结果行**右键验证整页枚举。（同时回答：菜单是否出现、取到的文本对不对——这决定 `.1002` 注册键与 SelectionSet 两个机制疑点能否关闭，见 §8.2-A。）
- **T2 多语言往返**：多语言项目改同一文本的 2 种以上语言译文并保存→关闭窗口重新打开→各语言新值都在；再切到单语言形态确认不丢值。
- **T3 不自动翻译标志**：勾某行"不自动翻译"保存后，在 EPLAN 修改源语言触发自动翻译，确认该行译文不被覆盖、未勾行会被翻译。
- **T4 整批一次撤销**：一次改多行（多语言格也改）→确定写回→在 EPLAN 里**按一次 Ctrl+Z**，确认这批改动整体撤销、内容还原（而不是只撤一行或撤销栈变灰）。
- **T5 双击列头排序**：双击"结构/X 坐标/Y 坐标"等列头，确认默认→升→降三态循环，结构列按高层/安装/位置管理序、坐标按数值，且各行复选框/新值/原值不串行。
- **T6 复制粘贴回归**：把含 ¶ 的多行格与多列区域复制进 Excel 再贴回（换行、Tab 分列正确）；复选框列复制粘贴输出/接受 TRUE、FALSE。
- **T7 图面纯符号右键**：在一张只选了符号（无任何文本）的真实图纸上右键，确认"文本批量编辑"菜单**不出现**。
- **T8 非模态边界**：有脏行未保存时再次触发右键菜单，确认弹问且三选一不丢内容；插件窗开着不关直接退出 EPLAN，确认无报错、改动按预期提示。

### 8.2 必须在 EPLAN 真机核验的高优先项

> 2026-09-17 更新：功能边界项已收敛为 §8.1-A 的 T1–T8 勾选清单（xavier 勾一句即可闭环）；以下保留技术细节。**另：xavier 当日实测报告的 4 个现存问题（性能 P1-a/P1-b、Home/End P2-a、自动保存丢数据风险 P2-b）登记在 §4.4.2，状态"待 UX/性能整体评估"，优先级上 P2-b 为数据安全、高于本节其余项。**

- **A. 查找结果列表右键（16118 新功能；→ T1）**：① 注册键 `XSeSearchResultsTab1/1002` 中 `.1002` 数字菜单 ID 是仿照 1007 类推的，**从未从二进制/ShowIdentifier 实证**，可能不被该面板接受（最坏菜单不出现，try/catch 兜底不崩不动数据）；② 结果行（文本结果/页结果）是否真进全局 SelectionSet 未知；③ 若不进，需改走 `HEServices.Search`，但"如何取当前结果面板那批 StorableObject"API 是否开放尚未查实。实测时请同时记录插件日志的 `右键菜单：来源=…` 行（实际在 `TextBatchEditAddIn.cs:204`，输出"图纸(AfxFrameOrView)"或"列表(页导航器/查找结果)"）。
- **B. 完整页名分隔符是否恒为 `/`**：仅单测试项目（`=MB1++NP1+G2/2`）成立；宏页/总览页/多结构多页类型项目未验证。非 '/' 时代码无兜底（只去掉可能尾部 ";##1"），需据日志完整 Page.Name 调整。
- **C. 写回/撤销三件套的真机回归**：多语言多语译文往返、"不自动翻译"标志实际效果、整批一个撤销点的 Ctrl+Z 实际表现——代码实现完整但缺确证真机记录。
- **D. 复制粘贴最终回归**：¶ 换行、Tab 分列、与 Excel 双向互贴、复选框列复制粘贴。
- **E. 图面纯符号（无文本）右键**：测试页无法构造，待真实图纸验证"菜单隐藏"。
- **F. 非模态窗边界**：脏行未保存时重复触发/最小化前置/EPLAN 退出时窗仍开着的 OnExit 时序。

### 8.3 环境 / 部署 / 资料类 [UNCERTAIN]

1. 工作站设置 `STATION.SystemError.LogFilePath` 的确切"选项→…"界面路径未知；默认值/百人机器指向与可写性无 fleet 验证（§5.2）。
2. 100 人 SMB（`192.168.150.245`）/组策略分发方式无定论（倾向 Add-on 未拍板，无任何方案真试过）；GitHub DLL 带 MOTW 需逐台 `Unblock-File`；日志无集中收集设计（§6）。
3. `EA.EplAddIn.Test` 当前是否仍注册/加载在 EPLAN 不确定，需在 Add-in 管理器确认。
4. P1 repo-setup 未开工：paseo.json setup、drvfs 硬链接、worktrees.root 迁 /mnt/d 均未验证；本 worktree 无 references/ 是 gitignored 预期，**本轮不拷贝不安装**。
5. 泛化参数版 check-kbd-focus / set-input-language / monitor-focus 只做了 PowerShell PARSE 校验，未在完整自测流程实跑（原始一次性探针用过）。
6. `PmPageObjectTreeDialog/1007` 的数字 1007 最初获取途径顾问不能 100% 还原（功能已真机验证，仅"来历"存疑）；1001/1003/Tab3 含义未知。
7. L___ 语言无关串的确切产生机制无受控实验；无专门测试夹具。
8. 回读校验机制（Form.cs:2361-2421）没有真机实际抓到过写回失败的记录。
9. 大页性能、多用户项目锁、撤销后表格同步均无实测（见 §7.1）。
10. **测试机 EPLAN 前台线程 HKL 状态 [UNCERTAIN]**：自测期顾问曾把 EPLAN 前台线程输入法强制切到英文 0409，恢复脚本待批后随 `.temp` 删除、**未执行恢复**；接手者首次键盘自测前先用 set-input-language/check-kbd-focus 读一眼当前 HKL，勿假定是中文 0804。

### 8.4 文档/代码不一致（走公共线修正，插件分支无权改）

- AGENT.md:79（"清 ShadowCopy 目录"）与 :95（"不要手动清"，xavier 09-16 纠正）冲突——以 :95 为准。
- AGENT.md:107-108 日志章节滞后：仍写"首选 `$(MD_SCRIPTS)\.log`、按天 `addin-yyyy-MM-dd.log`"；代码已是工作站设置动态路径首选 + 分钟命名/1MB 滚动/保留 5 个。
- AGENT.md:35 与多插件总纲称 AddInLogger"两份同构"——实际已不同构（§5.7）。
- EPL-Scripts 离线参考 `eplan-addin-table-form/references/page-navigator-context-menu.md` 仍写 GXWND，实际窗口类 `AfxFrameOrView140u`。
- `.gitignore` 已实际覆盖 AGENT.md:327 待确认所列项（bin/obj/.temp/references/.vs 等），该项可视为已满足，仅待人确认勾选。

---

## 9. 顾问问答附录（cc090ea，只读访谈）

> 访谈铁律：首条仅发 `/model custom:ark:ark-code-latest` 待确认；其后每条开头声明只读、禁止任何写操作/构建/git 写。问题分批合并。以下按批次×编号记录，答复标注日期 2026-09-17。
> 顾问：`cc090eab-c109-43b6-9cd3-4191e911912b`（名「本地」，驻主 checkout，完整经历开发）。共 3 个实质批次 + 1 个复核批。完整原话存于 planner 会话工具记录，本节为要点归档，正文各节已带 `[顾问答复 x-y]` 内联标注。

### 批次 1：版本机制 / 日志路径 / 发布分发（10 条：1-1~1-3、2-1~2-4、3-1~3-3）
- 6 分钟桶源于版本号 UInt16 编码约束（1 位 b，每小时 10 桶，§5.1）；动态/固化为兼顾"自测识别新 DLL"与"tag 可复现"。
- 脚本唯一返工：`772c285`（PowerShell 内联改纯 bash + get-dll-version.ps1 -File）；无错版 DLL 事故。
- ShadowCopy 旧副本在 09-15/16 反复误导；xavier 09-16 纠正不要手删。
- 日志演进链：cc58b0b→bac6d16→7b8ea5c/e225fdf；触发点是设置 ID 大小写（S024001），非 `$(MD_SCRIPTS)` 为空；滚动 a862dcb；说明页绝对路径 ab9df7d。工作站设置 GUI 路径与 fleet 可写性 [UNCERTAIN]。
- 16118 由顾问跑脚本发布、GitHub Release 已建，**新功能在发布时点（09-16）未经真机验证即按 xavier 指令发布**（次日 09-17 已被其总体验收在总体层面覆盖，见 §4.4）；100 人分发无方案真试过，倾向 Add-on；日志无集中收集。

### 批次 2：右键挂接 / IME / 页名 / 多语言（13 条：4-1~4-4、5-1~5-3、6-1~6-2、7-1~7-4）
- **最大发现**：`XSeSearchResultsTab1/1002` 中 DialogName 有 .erx 二进制证据，但 `.1002` 数字 ID 是仿照 1007 类推、从未实证；查找结果行是否进 SelectionSet 未知；备选 HEServices.Search 未查实。
- Editor/Ged 已真机验证；1007 功能真机验证、数字来历 [UNCERTAIN]；1001/1003/Tab3 未摸清。
- 全局钩子/BCG 接口没真试过，按推理排除，定线程局部 WH_CALLWNDPROC。
- Ctrl+J 双消息（0x0290/0x0100）；其他键无系统枚举（仅见过 Ctrl+A 焦点+IME 混合落空）；插件只在自有 Form 内防御，全局靠先切英文。
- #11000 是可空描述性页名（真机验证）；真实完整页名 `=MB1++NP1+G2/2`；'/' 通用性仅单项目。
- L___ 产生机制、MultiLangString  setter 铁律的原始失败日志、不自动翻译反相、整批撤销——均无确证真机记录，仅经验规则/代码推断。

### 批次 3：UI 真机状态 / 遗留 / Test 与 references / 真机验证总表 / API 坑（8-1~8-4、9-1~9-2、10、11）
- 真机验证总表见 §8.1；已真机验证：单语言写回、还原、行高拖拽、转至图形、说明页、页名空白现象、图面（空选择/选文本）显隐、页导航器右键。
- **"图面空选择绝不枚举整页"是刻意设计**（防 `GetSelectedPages()` 幽灵当前页）；整页枚举分支只看选择内容含页/结构节点、**不区分表面**——已验证入口=页导航器；查找结果选中页行走同一分支是 ab1e288 后的理论新入口，未真机验证（复核 R4 已纠正原"只能从页导航器进"的绝对表述）。
- 技术债：查找结果 HEServices.Search、大页性能、多用户锁、撤销后同步、横向体验、Debug 级别、OnExit 时序。
- Test 插件注册状态不确定；两份 Logger 不同构，抽 Shared 建议以 309 行版为准（未拍板）。references 13 个 DLL 手工拷贝，TextBatchEdit 只引 5 个；新 worktree 无 references 是 gitignored 预期。
- API 坑：Undo/Transaction 配对、LockingStep 通行坑（本项目未亲历）、MultiLangString 值语义、设置 ID 大小写、GetSelectedPages 幽灵当前页、MFC 宿主下窗内/全局 MessageFilter 差异、PS 脚本纯 ASCII。

### 批次 4：存疑清单复核（R1~R7，2026-09-17）
- **R1 无异议**（顾问记录 release 提交 75b1348 + annotated tag 对象 6720e55，merge SHA 未自记但与 9ef4473 无反证）；**R2 无异议**：16118 发布后 xavier 无任何实测反馈，"我已经验证过了"属于 16081。
- **R3 修正（重要）**：16081 只有一句笼统总放行、无逐项清单；不得写成"xavier 验收了 Ctrl+J/转到图形"。图面右键三场景是顾问日志自动化测。已据此改 §4.1-5 与 §8.1。
- **R4 修正**：整页枚举分支不区分表面，查找结果选中页行理论同路径（未验证）；"图面空选择绝不枚举整页"成立。已改 §1.3-3 与 §9 批次 3。
- **R5 补充**：references/ 还有 `参考插件/博客园.Tristan998`（ReoGrid/AntdUI/DotNetZip 等学习样本）、api-2.9、EPLAN设置配置文件、temp，均 gitignored（本轮只读 ls 核实）。
- **R6**：顾问无把握断言 AGENT.md 第四处失实；GXWND 过时文档在 skill 参考库（非 AGENT.md），已列 §8.4；两个 1002 非撞 ID（各 DialogName 命名空间内编号，可复用），已在 §5.3 注明。
- **R7 三条新教训**：① 带 token 完整 URL push 不更新本地 tracking ref，假 ahead 以 `git ls-remote` 为准（已记 §5.1）；② build.sh 裸 date 依赖 WSL 时区（已在 1-2）；③ 三个泛化键盘脚本仅 PARSE 校验 + 测试机 HKL 切 0409 后未恢复（已记 §5.4/§8.3）。

---

## 附：文档修订记录

| 版本 | 日期 | 变更 |
|---|---|---|
| v1 | 2026-09-17 | 初版：只读盘点 + cc090ea 顾问 4 批访谈（405 行） |
| v2 | 2026-09-17 | 协调者转达 xavier 验收结论后更新：① 新增 §4.4「xavier 验收结论与实测问题」（总体验收原话 + P1-a/P1-b/P2-a/P2-b 四个实测问题登记）；② §8.1 改为"顾问证据视角"并新增 §8.1-A 待 xavier 逐项勾选清单 T1–T8；③ §4.1-4/5、§7.1、§8.2 与 §9 相关表述按总验收口径修订；④ 修正 §9 批次 1/2 问答计数（10、13）；⑤ §0 增加验收权威来源与来源标注约定。未访问顾问，全部信息来自 xavier。 |
| v2.1 | 2026-09-18 | 协调者定稿校正：逐条 grep/实读复核 §4.4.2 与 §7.1 的全部 file:line 引用并改为实测行号（统一"起始-结束/起始"风格）。主要修正：LineBreakTextBox 类声明确认为 `TextBatchEditForm.cs:2573`（类体 2573-2637）；SaveDirty 内提交编辑格的语句为 `:2272`（`:2271` 是注释）、方法实为 2269-2448；600ms 防抖 `Interval=600` 在 `:1800`、Tick 调 SaveDirty 在 `:1810`；ApplySort 刷新两句在 `:1694-1695`；自绘两处理器为 `:1005-1043`/`:1050-1092`；收集 CollectTexts 为 `TextBatchEditAction.cs:54-127`；绑行方法实名 `LoadRows :1434-1561`（原误写不存在的 "LoadTexts"）。仅改引用，未动结论与严重度。 |
