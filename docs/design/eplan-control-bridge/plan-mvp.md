# EPLAN Control Bridge — 可行性评估与 MVP 规划

> 状态：Planner 产出，2026-09-19。仅规划，无产品代码。分支 `feat/eplan-control-bridge`，base `develop`。
> 适用：EPLAN 2.9 / .NET Framework 4.7.2。
> 本文所有"已验证-元数据"结论来自对 `references/EplApi/` 下 **13 个真实 DLL** 的只读 ECMA-335 元数据解析
> （Python `dnfile` 直读 TypeDef/MethodDef/Param/Field 表，**不加载、不执行任何 EPLAN 代码**，未改动 TextBatchEdit）。
> 本环境 WSL interop（PowerShell）在本会话失效（binfmt 在但 `WSL_INTEROP` 为空、override 既有 socket 均 EINVAL），
> 故未用 PowerShell 反射；改用 dnfile，结论等价（读到的是程序集真实签名）。原始类型清单留存 `.temp/reflect/`。

## 0. 证据等级约定

| 标记 | 含义 |
|---|---|
| **[V-元数据]** | 已用 dnfile 从真实 DLL 元数据核到类型/方法签名，确定存在 |
| **[V-仓库]** | 本仓库 TextBatchEdit / 线程探针已在真机验证过的用法（见 `AGENT.md`） |
| **[D-文档]** | 官方公开 API 文档/脚本机制支持，但本轮未跑代码 |
| **[?真机]** | 元数据能看到入口，但运行时行为/权限/稳定性必须在 EPLAN 2.9 真机回答 |

---

## 1. 可行性结论矩阵

### Q1 能否枚举"全部/内置 Action"的名字与参数？—— 部分可行（不能全量枚举）

- `Eplan.EplApi.ApplicationFramework.ActionManager` 公共面只有 **[V-元数据]**（`Eplan.EplApi.AFu.dll`）：
  - `FindAction(strNameOfAction)` / `FindAction(strNameOfAction, bSilent)`
  - `FindBaseAction(...)` / ctor
  - **没有任何 `GetActions`/枚举全部已注册 Action 的公共方法**（13 个 DLL 全量检索无此成员）。
- 拿到单个 `Action` 后可读 **[V-元数据]**：
  - `Action.Name`、`Action.ModuleName`、`Action.ActionProperties`
  - `ActionProperties.GetParameterProperties()` → 每个参数 `ActionParameterProperties.Name`（即"已知名字 → 可查参数"）。
- **结论**：运行时**不能列出全部 Action**；只能"按名 `FindAction` 探存在 + 读该 Action 的参数属性"。
  内置 Action 名目录需离线获得（官方 Action 文档 / 脚本录制 / CLI 帮助），由插件内置或随插件分发一份候选清单，再用 `FindAction` 去伪存真。
- 置信度：**高（存在性 [V-元数据]；"无全量枚举"是对公共面的穷尽检索结论）**。

### Q2 执行 Action 与传参？—— 可行（两种等价路径）

- **[V-元数据]** `Action.Execute(bool value, ActionCallingContext oCallingContext)`。
- **[V-元数据]** `ActionCallingContext`：`GetContextParameter()` / `SetContextParameter(pParams)` / `GetException()` / `SysMessages`。
- **[V-元数据] [V-仓库]** `CommandLineInterpreter`（AFu）：
  - `Execute(bool, strExpression)` 与 `Execute(bool, strExpression, oContext)`
  - `IsExecutable(bool, strExpression)`（可先判可执行性）
  - ctor `(bEnableExceptions, bCollectSysMessages)`
- TextBatchEdit 已在真机用 `CommandLineInterpreter.Execute` 调 `XPrjAction...` 并从参数块读返回（[V-仓库]）。
- **结论**：执行 + 传参可行。MVP 优先用 CLI 字符串表达式（`"XAction /param:value"`），最省事且仓库已实证；结构化结果取 `SysMessages`/异常。
- 置信度：**高**。

### Q3 动态执行脚本片段？—— 官方不支持"任意代码片段"，只支持预声明脚本文件 / 预注册 Action

- `Eplan.EplApi.Scripting` 命名空间只有声明式特性 **[V-元数据]**（AFu）：
  `DeclareAction`、`DeclareRegister`、`DeclareUnregister`、`DeclareEventHandler`、`DeclareMenu`、`Start`。
- 这是 EPLAN 经典的"**外部脚本文件 → 启动时编译并注册**"模型（[D-文档]），不是运行时 eval。
- 13 个 DLL 中**没有**任何公共动态编译/eval 入口（无 CodeDom/Roslyn 封装、无"执行传入 C# 字符串"的方法）。
- **结论**：
  - "灵活执行你想要的脚本"在官方支持面内 = ① 执行已注册 Action；② 放脚本文件让 EPLAN 加载（生命周期/热加载受限）。
  - **运行时下发任意 C# 片段并执行不受官方支持**；技术上可在 addin 内自备 `CSharpCodeProvider` 编译（net472 自带，无新重依赖），但这是**自担风险的代码执行沙箱**，安全与稳定性影响大，**不进 MVP**，列为后续专项且默认禁用。
- 置信度：**高（无公共 eval 入口 [V-元数据]）**；自备编译器路线仅 [?真机]。

### Q4 数据模型最小只读遍历？—— 可行，最小只读面齐全

全部 **[V-元数据]**（DataModelu / HEServicesu）：

- `ProjectManager`：`CurrentProject`、`OpenProjects`、`OpenProject(...)`（写，MVP 不用）。
- `Project`：`ProjectName`、`ProjectFullName`、`ProjectLinkFilePath`、`Pages`、`Properties`、`IsReadOnly`、`IsOpen`。
- `Page`：`Name`、`IdentifyingName`、`PageType`、`Functions`、`AllPlacements`、`AllGraphicalPlacements`、`Properties`。
- `StorableObject`：`Properties`、`TypeIdentifier`、`ObjectIdentifier`、`DatabaseIdentifier`、`IsValid`、`IsReadOnly`、`IsLocked`、`Project`。
- `HEServices.SelectionSet`：`GetCurrentProject(bUseSelDlg)`、`Selection`、`SelectionRecursive`、`GetSelectedPages()`、`SelectedProjects`、`OpenedPages`、`IsOnlyOneObjectSelected`。

- **结论**：`当前项目名/全路径/页数` + `选中对象类型计数` 这条最小只读链路所需 API 全部存在，且 TextBatchEdit 已在真机遍历过 `Project.Pages` 与属性（[V-仓库]）。
- 置信度：**高**。空项目/无选择时的返回形态需真机确认（[?真机]，见探针 P7）。

### Q5 枚举/触发菜单、Ribbon 命令？—— 自定义菜单/工具栏可枚举；内置 Ribbon 命令无公共枚举/触发 API

- `Eplan.EplApi.Gui.Menu` **[V-元数据]**（Guiu）：`AddMenuItem/AddMainMenu/AddStaticMenuItem/...`、`RemoveMenuItem`、
  `GetPersistentMenuId`、`GetCustomMenuId`、**`IsActionEnabled(strNameOfAction)`、`IsActionChecked(strNameOfAction)`**。
  → 面向"增删自定义菜单"，且能按 Action 名查启用/勾选态；**不能枚举内置主菜单/Ribbon 全部命令**。
- `Eplan.EplApi.Gui.Toolbar`：`CreateCustomToolbar`、`ExistsToolbar`、`GetCountOfButtons`、`GetButtonAction`、
  `GetButtonToolTip`、`AddButton`/`RemoveButton` —— 可对**具名/自定义工具栏**枚举按钮→Action 映射，但不是全量 Ribbon 命令枚举。
- `ContextMenu` + `ContextMenuLocation`：可对指定上下文位置 `GetMenuItemCount/GetMenuItem`。
- **结论**：**触发任何"命令"的正道是等价 Action（走 Q2），不是去点 Ribbon**。这恰好满足 xavier"用 API 替代视觉点按钮"的主目标——
  绝大多数 Ribbon 按钮背后都有一个 Action。枚举"按钮清单"本身无公共 API。
- 置信度：**高**。

### Q6 原生对话框内部控件能否程序化驱动？—— 存在半官方控件级 API（RecorderScript），但非通用、需逐框录制 ID —— 这是本轮最重要的新发现

`Eplan.EplApi.Recorder.RecorderScript` **[V-元数据]**（RecorderToolsu.dll，36 个方法）提供录制/无人值守（quiet）级的对话框操控：

- `CallAction(strNameAndParameter[, callback])`、`CallDialogAction(...)`
- `CallDialogCommand(strDialog, nDialogId, strCommand)`
- **`CallDialogControlCommand(strDialog, nControlID, strCommand, strData, strIndex, strDataType)`**
- **`SetField(strDialog, nControlID, strData, strIndex)` / `CheckField(...)`**
- **`ClickButton(strDialog, nControlID)` / `ClickSplitButton(strDialog, nControlID, strData)`**
- `ChangeSelection / ChangeSelectionByName / ChangeListSelectionByName(...)`
- `AddDialogCommand / GetDialogCommands / ResetDialogCommands`、`AddDecision/GetDecisions/ResetDecisions`、`Quiet` 开关。

**诚实结论（修正"预期多数不行"）**：

1. EPLAN **确实有官方的、按逻辑 ID（对话框名 + 数字 controlId）驱动内部控件的 API**，比 CUA/视觉可靠——不依赖像素/分辨率/语言位置。
2. **但**：
   - 它是"**录制脚本 / quiet-mode 批处理**"模型，`controlId` 是整数，通常要**靠录制或逐框探测**得到，**没有"运行时枚举某对话框所有控件及其 ID"的公共发现 API**；ID 非跨版本强契约。
   - 能否在**正常交互会话（非 quiet、对话框已被用户/AI 弹出）中即时调用**、模态期间线程如何配合、callback 语义，均未在文档面确认 → **[?真机]**。
3. 因此策略分两类：
   - **能映射到 Action 的操作 → 一律走 Action（Q2/Q5），稳定、可枚举参数、可白名单**。这是主力。
   - **Action 覆盖不到、必须填原生对话框字段/点框内按钮**的少数场景 → 后续专项用 `RecorderScript` 逐对话框建立 `(对话框, controlId, 含义)` 小目录，单独真机验证，不做通用 UI 自动化。
- 置信度：**类型/方法存在 [V-元数据] 高；交互可用性/稳定性 [?真机] 中低**。详见探针 P6。

### Q7 进程内常驻 loopback 服务的启停 / ShadowCopy / 共存？—— 可行；但 2.9 自带远程通道，构成架构分叉

**生命周期与加载 [V-元数据] [V-仓库]**：
- `IEplAddIn` 5 方法：`OnRegister(value, bLoadOnStart)`、`OnUnregister`、`OnInit`、`OnInitGui`、`OnExit`。
  → loopback 服务在 **`OnInitGui` 起、`OnExit` 停**是正确位置；TextBatchEdit 已实证 `OnInitGui` 可安全 new `ActionManager`/`Menu`/`SelectionSet`。
- **ShadowCopy 官方有感知接口**：`IEplAddInShadowCopy.OnBeforeInit(strOriginalAssemblyPath)`（AFu）——需要时可拿到原始程序集路径。
- 多 addin 共存：TextBatchEdit 独立 addin 已在真机稳定加载；再加一个独立 addin 无耦合点（各管各的菜单/Action/生命周期）。

**通道实现（net472 框架自带，零重依赖）**：
- `System.Net.HttpListener`：可绑 `http://127.0.0.1:<port>/...`，后台线程 accept，JSON 用框架/手写都行。
- 选 HTTP 而非命名管道的理由：**AI 客户端在 WSL 侧**，命名管道跨 WSL→Windows 麻烦；TCP loopback 经 localhost 可达。
- URL ACL / 是否需管理员：非管理员监听 `127.0.0.1` 高端口在多数 Windows 配置可行，但 `HttpListener` 对前缀有 urlacl 约束 → **[?真机]**（探针 P3）。备选：若 HttpListener 受 urlacl 所限，退化到 `TcpListener` 自写极简 HTTP（仍框架自带、无需 urlacl）。

**★ 2.9 自带的远程能力（架构分叉，必须真机先判断）[V-元数据]**：
- `Eplan.EplApi.WebService.EplanWebService` / `IEplanWebService`：`ping()`、`callAction(string s)`、`callActionPost(ActionInfo info)`；
  `ActionInfo` 公共字段 = `{ string action; ... parameters }`。
- `Eplan.EplApi.System.EplApplication` 有 `StartEplanRemoteServer` get/set。
- 进程外客户端 `Eplan.EplApi.RemoteClient.EplanRemoteClient` / `IEplanRemoteClient`：
  `Connect(computerName, port)`、`Ping()`、`ExecuteAction(fullAction / action+context)`、`SelectEplanObjects`、
  `LockAllEplanObjects`、`StartEplan/StopEplan`、`GetActiveEplanServersOnLocalMachine`，并有 `License/User/Password/SynchronousMode` 属性。
- 服务端契约 `Eplan.EplApi.Remoting.IEplanRemoting`：`Connect/Disconnect/Ping/ExecuteAction[/Asynch]/SelectObjects...`。

**含义**：官方已有"外部进程 → EPLAN 执行 Action / Ping / 选对象"的远程通道。但：是否默认关闭、如何启用、是否只听本机、
鉴权机制（User/Password/License 字段在客户端侧存在，服务端校验方式未知）、能否限制 loopback——全部 **[?真机]**（探针 P4）。

**规划决策**：
- **MVP 仍自建最小 `HttpListener` loopback 桥**（默认关、loopback-only、自有 token、可做数据模型只读与后续扩展），
  因为它安全姿态可控、能力不限于 Action、不依赖一个我们尚未摸清鉴权的官方服务。
- 同时把"**官方 RemoteServer/WebService 能否安全复用**"作为并行调研；若真机证明它可 loopback-only 且鉴权可靠，
  未来可在桥内直接调用 `EplanWebService.callAction` 或直接暴露官方通道，减少自建执行面。

- 置信度：生命周期/共存/类型 **高**；监听权限与官方通道复用 **[?真机]**。

### Q8 后台线程如何安全切回 EPLAN 主线程？—— 签名已核实，仓库已实证；属 Internal 命名空间需隔离封装

`Eplan.EplApi.Base.Internal.EplanMainThreadDispatcher`（Baseu）**[V-元数据]**：

- `static SetMainThreadDispatcher(d)` / `static GetMainThreadDispatcher()`
- **`static CanAccessMainThread(bool value)`**
- 实例：**`ExecuteInMainThreadSync(ExecuteInEplanMainThreadDelegate pExecuteDelegate, object x)`**
- **`ExecuteInMainThreadAsync(pExecuteDelegate [, x [, y]])`**（3 个重载）
- `AddProgressBackgroundWork(progress, workDelegate)`、`Dispose()`

配套委托（同命名空间）**[V-元数据]**：
- `ExecuteInEplanMainThreadDelegate`（`void()`，`Invoke()`）
- `...Delegate1`（1 参，`Invoke(x)`）
- `...Delegate2`（2 参，`Invoke(x,y)`）
- `...Delegate3`（签名 `Invoke(x)`，参数形态以真机为准）

- 仓库 `scripts/probe/TbeThreadProbeAddIn.cs` / `AGENT.md` 已真机实证该 dispatcher 存在且可用于后台线程切主线程（[V-仓库]）。
- **诚实保留**：命名空间是 **`.Internal`**——非官方稳定承诺，升级版本可能变。
  对策：把所有对它的引用收敛到**单个 `IMainThreadInvoker` 封装**内部，外层只依赖我们自己的接口；
  每次调用前 `CanAccessMainThread` 判定（已在主线程则直调，避免重入死锁）。
- **最大工程风险是封送死锁/饥饿**（模态对话框、长操作阻塞主线程时，Sync 封送会长时间挂起甚至与 UI 互锁）。
  对策：请求队列 + `Async` 优先 + 超时 + 串行/低并发，绝不让外部请求把 EPLAN 卡死（详见 §5 风险 R1）。
- 置信度：签名 **高 [V-元数据]**；可运行 **[V-仓库]**；稳定性/并发行为 **[?真机]**（探针 P2）。

---

## 2. 一句话可行性结论

**主目标可行**：在 EPLAN 进程内常驻一个 loopback-only、默认关闭、token 鉴权的 addin，经已实证的主线程封送器，
能稳定执行"按名 Action + 传参"和"只读数据模型遍历"，可替代绝大多数 CUA 点按钮；
受限处是"无法全量枚举 Action/内置 Ribbon"，而"原生对话框内部控件"有半官方 `RecorderScript` 通道但需逐框录制 ID、
交互可用性待真机；动态执行任意 C# 片段无官方支持、不进 MVP。

---

## 3. 最小可用 MVP（越小越好，4 个只读/白名单端点，分 4 个迭代）

MVP 只做**只读 + 白名单内无害 Action**，写操作/危险操作/动态脚本/对话框操控全部后置且默认禁用。

### 端点契约（HTTP/JSON，统一响应包）

所有响应统一：
```json
{ "ok": true, "requestId": "...", "durationMs": 12,
  "data": { }, "error": null,
  "sysMessages": ["..."], "eplanVersion": "2.9...", "addinVersion": "1.0.yyMM.DDHHb" }
```
失败：`ok=false`，`error={type,message}`，`sysMessages` 透传 EPLAN 系统消息。

| 端点 | 分级 | 内容 | 迭代 |
|---|---|---|---|
| `GET /ping` | 只读 | 存活 + addin 版本 + `EplApplication.Version` + **主线程可达性**（封送一个空委托往返测时延） | I1 |
| `GET /version` | 只读 | 版本详情（EPLAN variant/version、addin informational/file 版本、通道运行模式、是否主线程） | I1 |
| `GET /actions/list` | 只读 | **不承诺全量**：返回①插件内置白名单；②对内置候选名批量 `FindAction(name, silent:true)` 的命中结果 + 命中项参数名 | I2 |
| `POST /action/run` | **白名单** | body `{action, params}`，仅允许白名单内 Action；经 `CommandLineInterpreter`/`Action.Execute` 执行，回结构化结果 | I2 |
| `POST /model/query` | 只读 | MVP 支持两个查询：`currentProject`（全路径/项目名/页数/只读态）、`selectionTypeCounts`（`SelectionSet.Selection` 各 TypeIdentifier 计数） | I3 |

**白名单首批只放无副作用动作**（具体动作名在真机用 `actions/list` 探到后确定；候选为纯导航/信息类），
并叠加**危险前缀黑名单**双闸：`exit/quit/close/delete/remove/项目关闭/退出/保存覆盖` 等即使误配也拒绝。

### 迭代划分（每步独立可 0/0 + 可真机验证）

- **I0 — 脚手架（0 功能）**
  新 `EA.EplAddIn.ControlBridge/`：`ControlBridgeBridgeAddIn : IEplAddIn`（5 方法 + 生命周期日志）、`AddInLogger`（仿 TBE 同款）、
  新 csproj（net472/x64/复用 5 个 `references/EplApi` 引用 + TBE 同款 6 分钟桶动态版本号机制）、`scripts/build-controlbridge.sh`（仿 `build.sh`）、
  加入 `EPL-AddIns.slnx`。**验收：Debug/Release 双 0/0；真机加载后脚本控制台出现 OnRegister/OnInit/OnInitGui/OnExit 日志；TextBatchEdit 同时加载不受影响。**
- **I1 — loopback 骨架 + ping/version + 主线程探活**
  `HttpListener` 绑 `127.0.0.1`，默认**不启用**（见 §4 启用方式）；后台线程 accept，`OnInitGui` 起 / `OnExit` 干净停；
  token 校验；`/ping` 经 `EplanMainThreadDispatcher` 空往返报主线程可达性与时延；全部外调包 try/catch，**任何异常不得冒泡到 EPLAN**。
  **验收：双 0/0；真机从 WSL `curl` 带 token 通，无 token/非 loopback 被拒；EPLAN 正常开关无残留进程/端口。**
- **I2 — actions/list + 白名单 action/run**
  内置候选名清单 + `FindAction` 去伪存真 + `GetParameterProperties` 读参数；`action/run` 走 CLI，白名单+黑名单双闸、超时、结构化结果。
  **验收：list 结果与真机已知 Action 抽样一致；白名单内一个无害动作执行成功并回包；白名单外一律拒绝。**
- **I3 — model/query 只读**
  `currentProject`、`selectionTypeCounts`，封送主线程执行；空项目/无选择返回结构化空态而非异常。
  **验收：在测试项目上数值与人工核对一致；无项目/无选择时返回规范空态。**

每个迭代结束：双 0/0 → fresh review → **真机点验（EPLAN 只能真机）** → 才进下一迭代。

---

## 4. 目标架构与安全模型

### 4.1 进程内架构

```
AI/Dora (WSL)
   │  HTTP/JSON over 127.0.0.1:<port>   (Authorization: Bearer <token>)
   ▼
HttpListener 后台线程 (EPLAN 进程内, addin AppDomain, ShadowCopy)
   │  ① loopback/前缀校验  ② token 校验  ③ 按端点分级  ④ 白名单/黑名单
   ▼
请求队列（串行/低并发 + 每请求超时 + requestId 日志）
   ▼
IMainThreadInvoker  ── CanAccessMainThread? ── 是→直调 / 否→ EplanMainThreadDispatcher
   ▼                                                    (ExecuteInMainThreadAsync 优先, Sync 仅短任务)
EPLAN 主线程：CommandLineInterpreter / ActionManager / ProjectManager / SelectionSet
   ▼
统一 JSON 响应包（data | error{type,message} | sysMessages | durationMs）
```

### 4.2 安全模型（默认安全）

1. **默认关闭**：addin 加载后**不监听任何端口**。启用须显式动作：一个注册到菜单的"启用控制桥"开关 Action
   （或 addin 目录下由用户手动放置的配置文件），并在 UI/状态栏明确"桥已开启 + 端口"。**不做开机自启网络面**。
   `OnExit` 必定停服。
2. **loopback-only**：只绑 `http://127.0.0.1:<port>/`，绝不绑 `+`/`0.0.0.0`/机器名/局域网 IP。端口默认随机高端口或显式配置。
3. **本机 token 鉴权**：启用时生成随机 token，写到**仅本机用户可读**的文件（或一次性显示在 EPLAN 内状态栏/日志），
   每请求必须带正确 `Authorization`。token 不落仓库、不入日志明文。
4. **读写分级 + 默认最小权限**：
   - read 类（ping/version/actions/list/model/query）启用后可用；
   - `action/run` 受**白名单（默认只列无害只读/导航动作）+ 危险动作黑名单**双闸；
   - 写/删除/批量改/保存/退出/关项目/动态脚本 → **MVP 不提供端点**；将来加也默认关闭、逐条显式授权。
5. **资源与稳定护栏**：请求串行或并发上限 1~2、每请求超时（避免主线程被长时间占用）、请求体大小上限、
   全程异常隔离（addin 异常绝不冒泡到 EPLAN）、带 requestId 的受控日志（不记录 token）。
6. **不引入重依赖**：HTTP 用 `HttpListener`（受限时退化 `TcpListener` 手写极简 HTTP）；JSON 用 net472 可达的最轻方式
   （先手写序列化或 DataContract，不为 JSON 引第三方包）。

---

## 5. 风险与不确定点（Top）

- **R1 主线程封送死锁/饥饿（最高）[?真机]**：模态对话框、长操作打开时主线程阻塞，`ExecuteInMainThreadSync` 会长挂，
  甚至与 UI 互锁；高并发外部请求可能把 EPLAN 拖卡。对策：Async 优先 + 队列串行 + 超时 + `CanAccessMainThread` 直调 + 真机压测（P2）。
- **R2 可发现性受限**：ActionManager 不能全量枚举、内置 Ribbon 无枚举 API、`RecorderScript` 控件 ID 需逐框录制且非稳定契约。
  对策：内置随版本维护的 Action 候选目录；"能做什么"以 `FindAction` 实测 + 官方文档为准；对话框操控按框建小目录，不承诺通用。
- **R3 进程内网络宿主与官方远程通道的未知项 [?真机]**：HttpListener urlacl/管理员要求（P3）、
  官方 `StartEplanRemoteServer`/`EplanWebService` 的启用方式/鉴权/是否 loopback（P4）、ShadowCopy 下重载与端口释放。
  对策：先真机探明再定执行面归属；HttpListener 受阻则退化 TcpListener；OnExit/异常双保险停服。
- R4（次）`.Internal` dispatcher 的版本稳定性：收敛到单一封装，升级 EPLAN 时单点复核。
- R5（次）数据模型在"无项目/无选择/独占锁定/只读项目"下的返回形态与异常类型差异（P7），按结构化空态处理。

---

## 6. 工程脚手架清单（I0）

- [ ] 新目录 `EA.EplAddIn.ControlBridge/`
  - `ControlBridgeAddIn.cs`：`IEplAddIn` 5 方法，生命周期日志；I1 起在 OnInitGui/OnExit 管服务。
  - 初期无菜单；I1 加"启用/停用桥"Action + 状态栏/日志提示（遵循 xavier UI 偏好：纯功能名、提示走状态栏）。
  - `AddInLogger.cs`：复制 TBE 受控日志模式（级别 + 模块名 + requestId，禁打印 token/敏感路径）。
  - `BridgeConfig`（启用开关/端口/token 文件路径，默认安全值）。
  - I1：`LoopbackServer`（HttpListener 封装）、`IMainThreadInvoker` + dispatcher 封装、统一 `JsonResponse`、token 校验中间件。
- [ ] `EA.EplAddIn.ControlBridge.csproj`：拷贝 TBE 的 net472/x64/引用方式（5 个 EplApi DLL，`Private=False`）
  与 6 分钟桶动态版本号机制（`major.minor.yyMM.DDHHb` + `release-version.props` 固化模式 + build-version.json 记录）。
- [ ] `scripts/build-controlbridge.sh`：仿 `scripts/build.sh`（develop 强制动态版本；DOTNET_EXE 走 Windows 侧 dotnet.exe）。
- [ ] `EPL-AddIns.slnx` 加入新项目。
- [ ] **不改** TextBatchEdit 任何文件；不共享其代码（需要时复制模式而非引用其内部类型）。
- [ ] 每迭代：Debug/Release 双 0/0、fresh review、真机点验记录回填。

---

## 7. 必须真机回答的问题（给 xavier 的操作版见 `probe-questions.md`）

1. P2 主线程：模态对话框/长操作期间 `CanAccessMainThread` 取值、Sync/Async 封送时延与是否阻塞、可承受的超时与并发。
2. P3 监听：非管理员 EPLAN 下 `HttpListener` 绑 `127.0.0.1:高端口` 是否直接可用（urlacl），防火墙表现；否则验 TcpListener。
3. P4 官方远程通道：`StartEplanRemoteServer` 如何开启/配置/鉴权、是否 loopback-only；`EplanWebService` 能否在 addin 内直接调用。
4. P5 Action 发现：`FindAction` 对内置名的命中、参数属性可读范围；CLI 只读动作返回/SysMessages；离线 Action 目录获取途径。
5. P6 RecorderScript：交互（非 quiet）会话对已打开对话框 `SetField/ClickButton/CallDialogControlCommand` 是否可用；controlId 获取方式与稳定性。
6. P7 数据模型空态：无项目/无选择/只读/独占时 `CurrentProject`、`Selection` 行为与异常。
7. P8 ShadowCopy：`OnBeforeInit` 时机、DLL 锁与升级替换、端口在异常退出后是否释放。

---

## 8. 本轮调研资源与可复现性

- 只读元数据解析脚本：`.temp/py/dump_meta.py`、`.temp/py/dump_one.py`（dnfile 0.18.0，venv 在 `.temp/py/q-venv`）。
- 类型清单/分题 dump：`.temp/reflect/types.txt`、`q1-dispatch.txt`、`q3-ws.txt`、`q4-dm.txt`、`q5-gui.txt`。
- 未派 research 子代理：离线可确认的 API 已全部由 Planner 用真实 DLL 元数据核实，派 research 无增量；
  剩余问题都依赖 EPLAN 真机，子代理也无法离线回答，故本轮 0 个子代理。
