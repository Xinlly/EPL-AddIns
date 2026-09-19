# Plugin Manager / 动态加载扩展 — 可行性补充设计（Addendum）

> 状态：Planner 对 xavier 扩展想法（"已注册插件与脚本灵活配合、动态加载 DLL 或脚本、类似插件管理器"）的证据化评估。2026-09-19。
> 仅规划，无产品代码。证据方法同 plan-mvp.md：对 `references/EplApi` 13 个真实 DLL 做只读 ECMA-335 元数据解析（dnfile，不加载/不执行），加 EPLAN 2.9 官方在线文档。
> 证据标记：**[元数据-已验证]**（13 个引用 DLL 真实成员/继承链）、**[文档支持]**（EPLAN 2.9 官方 Infoportal）、**[?真机]**（运行时行为待验）。

---

## 0. 一句话结论

- **"灵活配合"= 复用现有 `action/run` 调度即可，无需新机制**：无论内置 Action、add-in 注册的 Action、还是已加载脚本用 `[DeclareAction]` 注册的 Action，在 EPLAN 里都是同名同参的具名 Action，`CommandLineInterpreter.Execute(name, context)` 一视同仁。
- **脚本能当次动态执行/加载**（官方内置 action `ExecuteScript /ScriptFile:`；注册型脚本加载后持久化、随启动再加载，可 Unload）——这是"当次动态可执行程序"的现实通道。
- **DLL 进程内热插拔不成立**：`Assembly.Load` 后锁定且不可卸载；EPLAN 数据/框架对象（`StorableObject/Project/Page/ActionManager/...`）**直接继承 `System.Object`，非 `MarshalByRefObject`，不能跨 AppDomain 按引用封送**，所以"独立 AppDomain 热卸载 + 继续操作实时模型"走不通。DLL 的现实形态是"下发 + 写入注册 → 下次启动 EPLAN 生效（可借 ShadowCopy 覆盖文件）"，不是进程内热更新。
- 因此插件管理器应**定位为"编排 + 脚本动态执行 + add-in 生命周期管理（安装/启用，重启生效）"，而不是"运行时卸载/替换 DLL"**。建议**不进 4 端点 MVP**，作为**后续迭代 I5，且优先只用其中的"脚本执行"子集**；完整 add-in 安装器涉及企业分发（你们已定 Add-on 才是百人统一分发单位），评估是否独立第二插件/并入 Add-on 流水线。

---

## 1. 编排层：已注册 add-in / 脚本是否统一为具名 Action

**结论：是。** [元数据-已验证] + [文档支持]

- 元数据（`Eplan.EplApi.AFu`）：
  - `IEplAction.Execute(bool bExecute, IActionCallingContext oActionCallingContext)`；add-in 在 `IEplAddIn.OnRegister(ref bLoadOnStart)` 期间向框架注册 Action。
  - `Eplan.EplApi.Scripting.DeclareAction` 构造参数就是 action 名：`.ctor(strActionName)` / `.ctor(strActionName, iOrdinal)`，并有 `get_Name()`。
  - 统一执行入口 `CommandLineInterpreter.Execute(bool, string strExpression)` 与 `Execute(bool, string, IActionCallingContext)`，另有 `IsExecutable(bool, string)`。
- 官方文档《Loading a script (2.9)》原文：脚本加载后 `[DeclareAction("MyScriptAction")]` 标记的函数"is registered in EPLAN as action by the name ... Now the new action can be used like any other action in EPLAN. It can e.g. be called by the command line or ... assigned to a menu point or toolbar button."
- **对桥的含义**：
  - 不需要为"插件动作 vs 脚本动作 vs 内置动作"建三套机制；`action/run` 统一按名调度 + `ActionCallingContext` 传参。
  - 但"取结果"统一受限：Action 标准结果只有 `bool` 返回 + `ActionCallingContext.SysMessages`/`GetException()`，没有通用返回值通道（见 plan-mvp.md §2 D1/D2）。脚本/插件若要回数据，得自己写文件/stdout 或后续暴露桥接 Action。**这是"灵活配合"的主要边界，需在文档和契约里讲清。**

## 2. 脚本动态层

**结论：官方支持"按文件一次性执行"与"加载注册（持久化）"两类；只能引用 Base/AFu/Gui 三程序集。** [文档支持] + [元数据-已验证]

### 2.1 一次性执行（当次动态，最契合桥）
- 官方内置 action **`ExecuteScript`**，参数 **`/ScriptFile:<.cs/.vb 路径>`**，附加 `/Param*` 透传给脚本；可从命令行、工具栏按钮、菜单、或**命令行表达式**调起。
  - 2.9 官方页：`.../Plattform/2.9/Content/htm/availableactions_o_executescript.htm`、`scripts_k_scriptemitparameter.htm`；示例 `W3u.exe ExecuteScript /ScriptFile:"...\SimpleScriptWithParameters.cs" /Param1:Hello ...`。
  - 2022 API 页补充：传给 ExecuteScript 的 `ActionCallingContext` 会继续透传给脚本。
- **进程内路径**（本插件要走的）：`new CommandLineInterpreter(true, true).Execute(false, "ExecuteScript /ScriptFile:\"...\"")` 或带 `ActionCallingContext` 形式 [?真机：在 add-in 内对 ExecuteScript 用表达式形式还是 context 形式更稳，需真机各试一次]。
- 模板作者（mrlucmorin/EPLANScriptItemTemplates）明确：脚本平时**不驻留内存**，由 Utilities > Scripts > Run 或 ExecuteScript 每次调起——意味着**同一文件改完可再次执行到新内容（天然"刷新"）**，但每次都要重新编译，启动有开销 [文档支持]，重复执行的编译耗时与并发锁需真机量。

### 2.2 加载/注册型（持久、可 Unload）
- 官方《Loading a script (2.9)》：脚本可 load / unload；load 时不执行 start 函数，而是注册 `[DeclareAction]/[DeclareMenu]/[DeclareRegister]/[DeclareEventHandler]` 等；**"Once the script was loaded, it will be automatically loaded during the Startup of EPLAN"**；卸载走 Utilities 菜单的 "Unload" 对话框。
- 元数据中只有这些 scripting attribute 类（`Eplan.EplApi.Scripting.Declare* / Start`），**没有公共的"运行时加载/卸载脚本文件"托管方法**；加载/卸载是 EPLAN 框架行为，UI 由 Utilities > Scripts 菜单提供。是否存在对应内置 action（如 `XPrjAction...LoadScript`）供 CLI 调起，**13 个 DLL 的用户字符串堆未能稳定提取到该 action 名 [?真机]**（见 §6 Q2），不能凭猜写进实现。
- 脚本可用 API 面：官方规定 Script 仅可引用 EPLAN 的 Base / AFu / Gui 三个程序集，不能引用额外 .NET/EPLAN 程序集（DataModel/HEServices 等不可用）——这与你们既有结论一致。**含义：脚本动态层做不了数据模型深操作，那些必须由 add-in（DLL）承载。**

### 2.3 脚本加载时机/重复加载
- 执行型：每次调用即时编译运行，不驻留（最灵活、最安全可控——文件可放受控目录、执行完即走）。
- 注册型：加载一次后写进 EPLAN 脚本注册、随启动自动加载；重复加载同名脚本的行为（替换还是报错/重复注册）**[?真机]**，卸载只能通过官方 Unload UI（是否可编程调起待验）。

## 3. DLL 动态层：加载、锁定、AppDomain、热替换（核心诚实结论）

### 3.1 Assembly.Load 的锁定与不可卸载 — [元数据-已验证 .NET 事实]
- net472 默认加载上下文 `Assembly.LoadFrom/Load(byte[])` 加载到**主 AppDomain**：从文件加载会**锁定该 DLL（及依赖）直到进程退出**；`Load(byte[])` 虽不锁原文件，但程序集**永不卸载**，反复加载会内存泄漏且同名程序集只认第一份。
- EPLAN 自己的加载器在元数据里有迹可循：`Eplan.EplApi.ApplicationFramework.Internal.SystemServices`（AFu，Internal 命名空间，非公开承诺）：
  - `static LoadAssembly(strAssemblyPath)`
  - `static GetOriginalAssemblyPathFromShadowCopyAssemblyPath(strShadowCopyAssemblyPath)`
  - `static getAssemblyProbingPaths()`
  说明 EPLAN 内部确有"按路径加载程序集 + ShadowCopy 路径映射"机制，但它在 `...Internal` 下，**不是公开 API，随时可能变，不能作为产品依赖**（最多真机侦察参考）。

### 3.2 独立 AppDomain 热卸载：理论可、对 EPLAN 对象不可行 — [元数据-已验证：决定性]
- net472 支持创建可卸载的子 AppDomain（`AppDomain.CreateDomain` + `AppDomainUnload`），但跨域访问只允许两类：`MarshalByRefObject`（按引用代理）或 `[Serializable]`（按值拷贝）。
- 对 13 个 DLL 的真实继承链解码（TypeDef Extends coded-token 手工解析）：

  | 类型 | 直接基类 |
  |---|---|
  | `DataModel.StorableObject` | **`System.Object`** |
  | `DataModel.Project` | `StorableObject`（→Object） |
  | `DataModel.Page` | `DocumentBase`→`Group`→`Placement`→`StorableObject`（→Object） |
  | `ApplicationFramework.Action` | `System.Object` |
  | `ApplicationFramework.ActionManager` | `System.Object` |
  | `ApplicationFramework.CommandLineInterpreter` | `System.Object` |
  | `HEServices.SelectionSet` | `System.Object` |

  **没有一个 EPLAN 模型/框架对象是 `MarshalByRefObject`，也未标可序列化跨域。** 结论：把"操作实时项目/页/选择集"的逻辑放进可卸载子 AppDomain **不可行**——对象无法跨域按引用传递（按值封送一个实时 EPLAN COM/原生支持的模型对象也不被支持）。
- 唯一能用子 AppDomain 的场景：加载一个**纯计算、只与 EPLAN 交换可序列化 DTO/字符串**的插件（例如独立算法、外部格式解析），由主域的桥通过序列化管道喂数据、收 JSON。这能做到热卸载，但**完全接触不到实时 EPLAN API**，价值有限，且增加一整层序列化/版本契约复杂度，违背 K2（简单），**MVP 与近期不做**。

### 3.3 EPLAN add-in 的注册/加载生命周期 — [元数据-已验证 + 文档]
- 接口就是 `IEplAddIn` 五方法（OnRegister/OnInit/OnInitGui/OnExit/OnUnregister）。`OnRegister(ref bool bLoadOnStart)` 设 `bLoadOnStart=true` 后，EPLAN 在**启动序列**加载该 add-in；注册信息由 EPLAN 的 add-in 管理持久化。
- 元数据里**没有**"程序化安装/注册一个新 add-in DLL"的公共 API；与 add-in 管理相关的只有 `Eplan.EplApi.System.EplApplication.ShowApiAddInDialog()`（弹出"API Add-Ins"管理对话框）。也就是说**"注册新 DLL"是 UI/配置行为，框架未承诺运行时可编程注册并立即生效**。
- 因此：**新 DLL 通常必须经 add-in 管理注册并在下次 EPLAN 启动时加载才生效**；"注册后当次会话立刻可用新 DLL"**[?真机]**（EPLAN 可能允许在 API Add-Ins 对话框里当场加载，但这是 UI 行为，无公共 API，且加载后仍不可卸载）。

### 3.4 ShadowCopy 对热替换的实际意义
- add-in DLL 被 EPLAN 以 ShadowCopy 方式复制到缓存加载，所以**原始 DLL 文件在 EPLAN 运行期间可以被覆盖/替换**（这也是你们开发期能重新拷贝 DLL 的原因），但**正在运行的会话用的还是内存里旧副本**——ShadowCopy 解决的是"文件不被锁、可覆盖"，**不是"进程内热更新"**。
- 现实交付形态（"DLL 插件管理器"能诚实做到的）：
  1. 桥（或配套安装器）把新版本 DLL 下发到受控 add-in 目录、覆盖旧文件（ShadowCopy 允许覆盖）；
  2. 通过 EPLAN 的 add-in 注册机制登记（可编程注册 API 不存在 → 要么预置注册项、要么引导用户在 `ShowApiAddInDialog` 确认，**具体能否纯写入注册配置免 UI [?真机]**）；
  3. **提示/调度重启 EPLAN**，下次启动加载新版本；
  4. 回滚=目录里保留上一版 DLL + 注册指针，重启切回。
- 这本质是"带编排和安全校验的 add-in 安装/启用器 + 重启生效"，**不是热插拔**。对百人企业环境，正式分发你们已确定用 **Add-on**（EPLAN 官方打包/自动更新单位），桥内置安装器不应替代它，只能在开发/测试期做"快速下发 + 提示重启"。

## 4. 与 EPLAN 自带 add-in 管理器 / 远程服务的职责边界

- **EPLAN 自带**（元数据已证）：
  - add-in 管理 UI：`EplApplication.ShowApiAddInDialog()`（注册/启用/停用，重启语义）。
  - 脚本管理：Utilities > Scripts（Run 一次性执行；Load/Unload 注册型）。
  - 远程/批处理：`WebService.EplanWebService.callAction/ping`、`EplanServerData(活动服务器枚举)`、`RemoteClient.EplanRemoteClient(ExecuteAction/Ping/...)`（见 plan-mvp.md §2 Q0）。
- **桥不重复造的**：DLL 的注册持久化、脚本注册的存储格式、官方远程协议的服务端实现——这些归 EPLAN。
- **桥独有增量价值**（自带能力没有或不适合 AI 的）：
  1. **loopback-only、显式启用、token、读写分级**的统一安全闸（官方远程服务的启用/鉴权/是否仅本机仍未知 [?真机 P1/P4]，且面向批处理不一定适合交互式 AI）。
  2. **结构化结果 + 可靠日志 + 主线程封送**（成功/异常类型/SysMessages/耗时、Async 队列）。
  3. **统一发现与白名单**：把内置动作、已加载 add-in 动作、已注册脚本动作编目（发现手段仍受限，见 plan-mvp.md §2 D1）。
  4. **脚本受控执行**：把 `ExecuteScript` 包成带鉴权、目录白名单、参数白名单、可取消、结果回收的 `script/run`。
  5. （远期）DLL 的"下发→校验→落位→注册引导→提示重启→回滚"编排，作为开发/测试期加速器，**企业正式分发让位 Add-on**。
- **若真机 P4 证明官方 RemoteServer 已是 loopback + 强鉴权且满足交互需求**：桥的执行面应考虑**直接复用/薄封装官方通道**，自建面只补安全/日志/发现/脚本编排，避免维护两套 action 调用路径。

## 5. 最小安全模型（动态加载 = 任意代码执行，必须更严）

任意 DLL/脚本加载在能力上等于在 EPLAN 用户身份下执行任意代码。除 plan-mvp.md §4 外，追加：

1. **默认全关、分级能力位**（配置 + 启动参数，默认值最严）：
   - `modelRead=on`（只读查询，MVP）
   - `actionRun=off→白名单`（MVP 后期）
   - **`scriptRun=off`（默认禁）**；开启后只允许"受控目录 + 文件名白名单/哈希"。
   - **`addinInstall=off`（默认禁，且需要独立强确认/双 token）**；MVP 不实现。
2. **脚本**：只执行 `<受控脚本根目录>`（如 `…/ControlBridge/scripts/`，与系统 Scripts 目录分离）下的文件；请求只给**逻辑名/清单 ID**，服务端映射到固定路径，**禁止请求直接传任意绝对路径/`..`**；每个脚本记录 **SHA-256 白名单**（未登记哈希拒绝）；扩展名限 `.cs/.vb`；执行前写审计日志（谁、哪个脚本、参数摘要、哈希）。
3. **DLL**（远期）：目录白名单 + 强名称/Authenticode 或至少 SHA-256 清单 + 版本；安装动作幂等、可回滚；**明确"重启才生效"**，不宣称热更新；安装与启用分两步、都要显式授权。
4. **token 与写操作分级**沿用 plan §4（Bearer、HMAC 本地令牌、loopback-only、跨账户拒绝、写/危险操作默认禁/需确认）。脚本/DLL 能力一律按"写+危险"对待，即使脚本只读也先按最高等级，待真机确认脚本 API 面后再降。
5. **资源/取消**：脚本执行包超时与取消边界（ExecuteScript 若不可中途取消，需在文档明示并建议脚本自带退出条件）；并发为 1。
6. **审计**：所有 script/addin 操作单独审计日志（与调用日志分开），含哈希与落位路径；不留存敏感参数明文。
7. 明确写入用户文档：**开启动态加载=允许经本机 loopback 在 EPLAN 账户下运行任意被放行代码**；不建议在不受信任的多用户机器上开启。

## 6. 路线图建议（修订）

- **不并入 4 端点 MVP（I0–I4）**。MVP 先把"通道 + 主线程封送 + 只读 + 白名单 action"这条最稳的闭环做扎实；动态加载会显著放大安全面和真机不确定项，拖慢 MVP。
- **I5 — script/run（脚本动态执行，最小、最高性价比的"插件管理器"子集）**：
  - 依赖：I1 通道/封送、I4 结果与日志、§5 的受控目录/哈希/能力位。
  - 范围：仅封装 `ExecuteScript`，受控目录 + 哈希白名单 + 逻辑名映射 + 默认关闭；先只支持执行型（不驻留、天然刷新），不碰注册型 Load/Unload。
  - 交付后 AI 即可"丢一个受控脚本进去当次执行"，满足 xavier "灵活执行脚本片段/扩展能力" 的大半诉求，且无需重启、不安装 DLL。
- **I6 — 动作/脚本发现与编目增强**：actions/list 增补"已注册脚本动作"分组（真机确定能否经内置 action/API 枚举注册脚本；不能则维护清单文件）。
- **I7（评估后再定，可能独立第二插件或并入 Add-on 工具链）— add-in 生命周期编排**：DLL 下发/哈希校验/落位/注册引导/**提示重启**/回滚；**明确不做进程内热卸载**。因企业百人分发已选 Add-on，这一迭代的定位仅限开发/测试期快速迭代，是否做、做成独立安装器插件，建议在 I5 真机数据回来后由 xavier 决策，不提前投入。
- **子 AppDomain 热卸载：不立项**（接触不到实时模型，价值不抵复杂度），除非未来出现"纯计算插件热插拔"的明确需求。

### 与现有 4 端点 MVP 的依赖关系
- `ping/version`、`actions/list`、`action/run`、`model/query` 对动态层**零依赖**，应先独立交付。
- `script/run`（I5）复用 I1 通道、I2 封送、I4 结果/日志，执行内核就是受安全闸包裹的一次 `action/run("ExecuteScript",…)`——**架构上它是 action/run 的特化，不是新机制**。
- add-in 安装（I7）只依赖通道做指令与审计，不依赖模型查询。

---

## 附：本 addendum 证据清单
- 元数据（dnfile 只读，13 DLL）：`IEplAction`、`DeclareAction(.ctor(strActionName[,iOrdinal]))`、`CommandLineInterpreter.Execute/IsExecutable`、`ActionCallingContext`、`EplApplication.ShowApiAddInDialog`、`Internal.SystemServices.LoadAssembly/GetOriginalAssemblyPathFromShadowCopyAssemblyPath/getAssemblyProbingPaths`、`EplanWebService/EplanRemoteClient/EplanServerData`；继承链手工解码证明 `StorableObject→System.Object` 等（非 MBV/MBR）。
- 官方文档（2.9 Infoportal）：Action `ExecuteScript /ScriptFile:/Param*`（availableactions_o_executescript.htm、scripts_k_scriptemitparameter.htm）；《Loading a script 2.9》RegisterAScript.html（[DeclareAction] 注册后"like any other action"、随启动自动加载、Unload 菜单）；脚本仅可引用 Base/AFu/Gui。
- [?真机] 待验项：①是否存在可编程调起的"加载/卸载注册型脚本"内置 action 名；②add-in 内对 ExecuteScript 用表达式还是 context 更稳、重复执行编译耗时/并发；③脚本注册型同名重载行为；④新 add-in DLL 能否纯写注册配置免 UI、注册后当次会话是否可用；⑤官方 RemoteServer 的 loopback/鉴权真相（plan P1/P4）。以上并入 probe-questions.md 跟踪。
