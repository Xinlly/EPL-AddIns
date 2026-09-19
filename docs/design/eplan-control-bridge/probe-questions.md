# EPLAN Control Bridge — 真机验证步骤（请 xavier 点）

> 这些问题**离线无法回答**（只读反射只能确认 API 存在，不能确认运行时行为）。
> 所有探针都是**只读 / 可取消**的：不改项目数据、不写文件（除插件自己的日志）、随时可在 API Add-Ins 对话框注销或忽略。
> 每步请记录：EPLAN 2.9 的确切版本号、是否管理员启动、结果/报错文本、截图或日志行。
> 探针 addin 尚未开发；下列 P1 可现在手动做，P2–P8 由 I0/I1 之后的**探针版插件**一键采集（每个探针独立按钮，点完即出结果）。

---

## P1. 官方远程通道是否已存在 / 可启用（先做，零代码）

目的：判断 2.9 自带的远程执行服务能否直接用，避免重复造轮子。

1. 正常启动 EPLAN 2.9（**记录是否管理员身份**）。
2. 设置中找远程/批处理相关开关：`文件/设置/用户/接口` 或系统服务里是否有 "Remote" / "WebService" / "远程" / 服务器端口配置；记录看到的选项与默认值。
3. 启动后在 Windows 跑（PowerShell）：`netstat -ano | findstr LISTENING | findstr 127.0.0.1`，记录 EPLAN.exe（用任务管理器拿 PID）监听了哪些本机端口。
4. 若有可疑端口，用浏览器访问 `http://127.0.0.1:<port>/`，记录是否有响应（不要对外暴露）。
- 判定：是否存在官方 loopback 服务、默认开还是关、有无鉴权提示。**全程只读，可随时关闭。**

## P2. 主线程封送行为（探针插件，只读）

点一个"主线程探活"按钮（可在 EPLAN 空闲时、打开一个模态对话框时、执行一个长操作时各点一次）：

1. 空闲：`EplanMainThreadDispatcher.CanAccessMainThread` 在插件动作线程返回 true/false；封送一个空委托 Sync/Async 往返耗时（ms）。
2. 打开任意模态"设置/选项"对话框期间再点：`CanAccessMainThread` 值、Sync 是否长时间不返回（探针 3 秒超时自动放弃，**不卡死 EPLAN**）、Async 是否在对话框关闭后才执行。
3. 连点 10 次：观察是否串行、有无卡顿/重入异常。
- 只读，可取消（超时即放弃）。决定：默认用 Async 还是 Sync、超时阈值、并发上限。

## P3. loopback 监听权限（探针插件，只起监听不执行任何 EPLAN 操作）

1. 非管理员启动 EPLAN，探针在 `OnInitGui` 用 `HttpListener` 绑 `http://127.0.0.1:<随机高端口>/ecb/`，记录成功/异常文本（重点看是否 `HttpListenerException: Access is denied`，即 urlacl 限制）。
2. 同机浏览器访问该地址 `/ping`（不带 token），记录是否返回 401/拒绝（验证 token 闸）；带正确 token 再访问一次。
3. 用另一台设备 / WSL 侧分别尝试连该端口，确认**非 loopback 不可达**。
4. 若第 1 步被 urlacl 拒绝：探针改 `TcpListener` 极简 HTTP 再试，记录是否无需特殊权限即可。
5. 正常关闭 EPLAN，`netstat` 确认端口已释放；再强杀 EPLAN 进程一次，确认端口也释放。
- 只起监听 + 回固定字符串，不碰项目；随时可停用。决定：HttpListener 还是 TcpListener、是否需要一次性 urlacl 预留。

## P4. 官方 WebService/RemoteServer 能力（探针插件，只读调用）

1. 探针读取 `new EplApplication().StartEplanRemoteServer` 当前值并记录。
2. 尝试在插件内 `new EplanWebService().ping()`（**只 ping，不 callAction**），记录成功/异常；能否取到服务端口信息。
3. 记录 `EplanServerData`（本机已安装版本/活动服务器枚举 `GetActiveEplanServersOnLocalMachine`）返回。
- 只读；不改开关、不执行动作。决定：执行面是自建还是复用官方。

## P5. Action 发现能力（只读）

1. 插件内置 20~30 个常见内置 Action 名候选（如 `XPrjActionProjectManager_...`、导航/视图类、`XSettingsAction` 等），逐个 `new ActionManager().FindAction(name, silent:true)`，记录命中/未命中；命中的读 `Name/ModuleName/ActionProperties.GetParameterProperties()` 的参数名。
2. 对一个**纯只读/无害**动作（如打开设置或视图刷新类，具体名真机挑），用 `CommandLineInterpreter`（`bEnableExceptions:true,bCollectSysMessages:true`）先 `IsExecutable` 再 `Execute`，记录返回、SysMessages、耗时。
3. 确认能否从官方渠道拿到一份内置 Action 名录（帮助/录制/文档目录）。
- 第 2 步只选确定无副作用的动作；其余只读。决定：actions/list 的数据来源与白名单首批成员。

## P6. RecorderScript 对话框控件驱动（探索性，先不写数据）

> 目的是诚实判定"原生对话框内部控件"到底能不能可靠驱动。

1. 手动打开一个**含输入框和按钮**的简单原生对话框（先选无破坏性的，如"查找/设置"类）。
2. 探针尝试仅**读取/枚举**：`RecorderScript.GetDialogCommands(...)`、`GetDecisions(...)`，记录能否拿到对话框/控件 ID 列表。
3. 对一个明显的只读控件（如只读字段/标签）尝试 `SetField` 后**取消对话框**（不点确定、不保存），记录是否生效、是否报错、线程要求（是否必须在主线程/quiet 模式）。
4. 记录 `controlId` 从哪里获得最可靠（录制器输出 vs 枚举）。
- 一律用"取消"收尾，绝不点确定，不产生数据变更。决定：对话框操控是否可行、成本多大、哪些操作必须改走等价 Action。

## P7. 数据模型空态（纯只读）

探针依次在下列状态点 model/query 采集，记录返回或异常类型：
1. 未打开任何项目：`new ProjectManager().CurrentProject`、`new SelectionSet().Selection`。
2. 打开一个项目但无选择：当前项目名/全路径/页数；选择集计数（期望 0 而非异常）。
3. 选中 1 页、选中几个图元时：`Selection`/`SelectionRecursive` 各 `TypeIdentifier` 计数；`GetSelectedPages()`。
4. 项目以只读/独占方式打开时：上述调用的差异。
- 全程只读。决定：model/query 的空态契约与异常映射。

## P8. ShadowCopy / 加载与共存

1. 加载探针插件 + TextBatchEdit 同时存在，确认两者菜单/Action 互不影响、脚本控制台各自日志正常。
2. 记录 `IEplAddInShadowCopy.OnBeforeInit(originalPath)` 是否被回调、传入路径。
3. 在 API Add-Ins 对话框反复"加载/卸载"探针几次，确认端口不残留、无文件锁定导致无法替换 DLL。
- 只读/可逆。

---

## 反馈格式（建议）

每项回我：`Pn - 步骤k: 结果(成功/失败/现象) + 关键报错原文 + EPLAN版本/是否管理员`。
P1 现在就能做；P2–P8 我会在 I0 脚手架后提供一个**仅含探针按钮、不含任何产品功能**的探针版 addin（双 0/0 后给你），点一遍即可，全部只读可取消。
