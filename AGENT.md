# AGENT.md — EPL-AddIns

## 项目概况

EPLAN Electric P8 2.9 **Add-in（编译型 DLL）** 集合（C#）。与 EPL-Scripts（源码脚本集合）互补：脚本能力不足、需要 DataModel 等完整 API 或需要高性能/可调试的场景在本仓库开发。

- 目标版本：EPLAN Electric P8 2.9（至少 3 年内不升级 2024+）
- 运行时：.NET Framework 4.7.2（`net472`），x64
- 许可证：AGPL-3.0
- 版权：Xinlly
- 远端：https://github.com/Xinlly/EPL-AddIns （公开仓库，分支 main）
- 前置条件：EPLAN **API Extension** 许可（Add-in 运行与注册必需，脚本不需要）

姊妹仓库（脚本形态 + 离线 API 文档 + 完整 API 速查）：https://github.com/Xinlly/EPL-Scripts

---

## 目录结构

```
EPL-AddIns/
├── EPL-AddIns.slnx              # Visual Studio 解决方案（XML 格式，多项目容器）
├── EA.EplAddIn.Test/            # 第一个 Add-in：Hello World 骨架 + 表格编辑演示
│   ├── EA.EplAddIn.Test.csproj
│   ├── Class1.cs                # IEplAddIn 实现：注册/生命周期/菜单/全局异常
│   ├── AddInLogger.cs           # 文件日志（DEBUG/INFO/WARN/ERROR，按天一个文件）
│   ├── HelloWorldAction.cs      # IEplAction 实现
│   ├── TableEditorForm.cs       # WinForms 表格编辑窗口（DataGridView，界面演示）
│   └── TableEditorAction.cs     # 打开表格窗口的 Action
├── EA.EplAddIn.TextBatchEdit/   # 第二个 Add-in：文本批量编辑（选中文本的中英文）
│   ├── EA.EplAddIn.TextBatchEdit.csproj
│   ├── TextBatchEditAddIn.cs    # IEplAddIn + 菜单
│   ├── TextBatchEditAction.cs   # 取选择集筛 TextBase → 开窗 → 写回
│   ├── TextBatchEditForm.cs     # 多语言表格（块复制粘贴/右键菜单/多标签页）+ UndoStep/Transaction 写回
│   └── AddInLogger.cs           # 与 Test 同构（第三个插件时抽 Shared 项目）
├── references/                  # 本地引用程序集（不入库，见下）
│   └── EplApi/*.dll             # 从 EPLAN 2.9 安装目录复制
├── AGENT.md
└── LICENSE
```

- 解决方案采用多项目结构：以后每个独立 Add-in 一个 csproj，命名 `EA.EplAddIn.<名称>`，统一挂在 `EPL-AddIns.slnx` 下，共享 `references/`。
- `EA` 是组织前缀。新增项目沿用此前缀，不要自创。

### references/ 不入库

`references/EplApi/` 下是 EPLAN 专有程序集，**版权属于 EPLAN，禁止提交到公开仓库**（`.gitignore` 必须忽略）。新机器准备步骤：

1. 从 EPLAN 安装目录 `C:\Program Files\EPLAN\Platform\2.9.x\Bin\` 复制所需的 `Eplan.EplApi.*u.dll` 到 `references\EplApi\`
2. csproj 中以 `<HintPath>..\references\EplApi\xxx.dll</HintPath>` 引用，`<Private>False</Private>`（不拷贝到输出目录，运行时由 EPLAN 进程提供）

---

## 构建

```powershell
dotnet build EPL-AddIns.slnx
```

- 输出：`<项目>\bin\Debug\net472\<项目名>.dll`
- 跨平台开发时可在 WSL 中调用 Windows 的 dotnet：`"/mnt/c/Program Files/dotnet/dotnet.exe" build EPL-AddIns.slnx`
- 需要 6 分钟粒度的构建号时用统一入口 `./scripts/build.sh [Debug|Release]`（先算动态版本再编译，见下节）。
- 重命名项目/目录后若出现怪问题，删除 `.vs/`、`bin/`、`obj/` 后重新生成。

### 程序集版本号（构建标识）

规则 `major.minor.yyMM.DDHHb`，示例 `1.0.2609.13202` = v1.0 系列、2026 年 9 月、13 日 20 点、`b=floor(分/6)`（一小时 10 个 6 分钟桶，取 0~9）。前两段 `1.0` 手动维护发布系列；四段均为数字且 <65535（build 最大 `9912`、revision 最大 `31239`）。解析：`b=r%10`、`HH=(r/10)%100`、`DD=r/1000`，不依赖前导零。

- **Git tag / AssemblyVersion / FileVersion / InformationalVersion 四者一致**：tag 带 `v`（`v1.0.2609.13202`），三个 .NET 字段用纯数字（`1.0.2609.13202`），InformationalVersion 带 `v`。
- **develop 动态 / main 固化（可复现）**：
  - develop（日常测试）：`./scripts/build.sh` 用 bash `date` 按北京时间计算并写不入库的 `.temp/TextBatchEdit.DynamicVersion.props`（6 分钟粒度），版本随构建变，便于识别 ShadowCopy 旧 DLL。
  - main / tag（正式发布）：入库的 `EA.EplAddIn.TextBatchEdit/release-version.props` 提供固定 `VersionBuildPart`/`VersionRevisionPart`，checkout 同一 tag 重编版本号不变。
  - 直接用 `dotnet`/VS 编译且两种 props 都不存在时，csproj 兜底按小时粒度 `ddHH` 动态（mode=`dynamic-hour-fallback`），保证可编译。
  - 每次构建后写 `.temp/build-version.json`（含三字段版本、buildPart/revisionPart、mode、生成时间），`.temp/` 已 gitignore。
- 发布用 `./scripts/release-from-develop.sh [--dry-run|--no-push]`：develop 构建→读 build-version.json→merge 到 main→固化 release-version.props→Release 编译→读真实 DLL 三字段逐项校验→提交→打 annotated tag→推送。
- 版本在**编译时**写入 DLL，重启 EPLAN 不变——不要用运行时 `DateTime.Now` 做构建标识（每次启动都变，无意义）。
- 用途：判断 EPLAN 实际加载的 DLL 是不是最新构建。打开 EPLAN 的 **API 模块（拼接软件）** 对话框，"装配名称"列显示 `EA.EplAddIn.TextBatchEdit, Version=1.0.2609.13202, Culture=neutral, PublicKeyToken=null`。
- 插件日志 `OnInit` 也会记录程序集全名（含版本），可与对话框对照。
- 注意：版本号变化后旧注册不会自动更新，需在 Add-in/API 模块管理器中**卸载旧 DLL 再重新加载**；EPLAN 另有 ShadowCopy 缓存（`%AppData%\EPLAN\ShadowCopyAssemblies\`），怀疑加载旧缓存时先在管理器卸载、完全退出 EPLAN，再清该目录后重新加载。

### 协作约定（AI 协作者必守）

- **默认在代码修改完成后自动执行 `dotnet build` 验证编译通过**（2026-09-11 起的约定，无需等待指示）；用户明确说"先别编译"时例外。
- 修改代码只动与需求直接相关的行，禁止整文件重写、顺手改格式/注释。
- EPLAN 实际加载、注册、运行测试由用户在 Windows 的 EPLAN 中完成；助手无法启动 EPLAN，不得声称"已验证运行"。

### 日志（AddInLogger）

- 文件：`AddInLogger.cs`，四级 `Debug/Info/Warn/Error`（含异常完整堆栈），按天一个文件 `addin-yyyy-MM-dd.log`
- 位置（TextBatchEdit）：优先 `PathMap.SubstitutePath("$(MD_SCRIPTS)")\.log`（脚本主数据目录，与 EPL-Scripts 的 `.log` 约定一致，便于统一查看；用户在该目录建 `.log` junction/目录）；失败回退 DLL 旁 `logs\`，再失败回退 `%TEMP%\EA.EplAddIn.TextBatchEdit\logs`。每次启动 `OnInit` 会记录实际 `log dir`
- 位置（Test 项目）：DLL 旁 `logs\`，不可写时降级 `%TEMP%\EA.EplAddIn.Test\logs`
- 排障时看日志：时间戳 + 级别 + 线程 ID + 消息；`OnInit` 订阅了 UI 线程异常和 AppDomain 未处理异常
- 当前为调试期默认 `MinLevel=Debug`，正式分发前改为 `Info`（未来接 App.config 的 `log4net`/`Serilog` 之前，这个轻量实现够用）

### Git 提交与推送身份

- **助手（Dora）代做的提交，作者/提交者一律署名 `Dora <52117993+Xinlly@users.noreply.github.com>`（2026-09-13 起的规则）**。该邮箱是 Xinlly 账号（id 52117993）的 ID 型 GitHub noreply：按数字 ID 归属，**计入 Xinlly 的贡献且不暴露真实邮箱**；`git` 的 author name 仍为 `Dora`（`git log`/API raw/本地克隆可见），但 GitHub 网页作者链接会统一渲染为账号登录名 Xinlly —— "计入本人"与"网页显示 Dora"二者不可兼得，用户已选择前者。
- 用户本人手动提交（VS/VSCode/命令行）：全局身份 `Xinlly <52117993+Xinlly@users.noreply.github.com>`（NixOS xavier 与 Windows Admin0 两处 `~/.gitconfig` 均已配置）。
- **已废弃的邮箱**：`dora@noreply.local`（不归属任何账号、不计贡献）；`dora@users.noreply.github.com`（无数字 ID 的旧格式，会错误归属到真实第三方账号 github.com/dora，id 149025，严禁再用）；真实邮箱 `Xinlly@outlook.com`（隐私原因不再用于提交）。
- 实现方式：不改仓库 git config；Dora 代提交用当次环境变量 `GIT_AUTHOR_NAME=Dora GIT_AUTHOR_EMAIL=<noreply>`、`GIT_COMMITTER_NAME/EMAIL` 同名同邮箱，不影响用户手动提交。
- 推送认证始终使用 Xinlly 的 GitHub token（pusher=Xinlly，与作者独立）；WSL 侧推送走 `https_proxy=http://127.0.0.1:35353`，token 不写入 remote 配置。
- 重写历史用 `--force-with-lease`；推送到显式 URL 时需写成 `--force-with-lease=main:<远端当前sha>`，否则 lease 校验找不到跟踪引用。本仓库历史曾于 2026-09-13 整体改写：11 条 Dora 提交邮箱由 `dora@noreply.local` 改为上述 ID 型 noreply（name 保持 Dora），SHA 全部变化，用户首提交 `510d907` 保持不变；改写前曾建备份分支 `backup/pre-noreply-20260913`，确认无误后已于 2026-09-14 删除。

---

## Add-in 生命周期（IEplAddIn，官方文档核实）

| 方法 | 调用时机（文档原文语义） | 典型用途 |
|------|--------------------------|----------|
| `OnRegister(ref bool bLoadOnStart)` | 在 Add-in 管理器中选中 DLL 注册时，**仅一次**。返回 `false` 拒绝注册 | 设加载策略、一次性初始化 |
| `OnInit()` | Add-in 被加载时（启动自启，或按需加载首次载入） | 初始化运行时资源、注册非 GUI 对象 |
| `OnInitGui()` | UI 初始化完成后；**唯一适合修改界面的位置**；仅启动自启时调用 | 注册菜单/工具栏/Ribbon |
| `OnExit()` | EPLAN 关闭时（仅启动时加载过才调用） | 清理 |
| `OnUnregister()` | 从系统注销时，一次 | 清理注册期资源 |

`bLoadOnStart`：

- `true`：每次 EPLAN 启动自动加载，依次走 `OnInit` → `OnInitGui`
- `false`：注册但不自动加载；其声明的 action 系统已知，首次被调用时才加载 DLL。此时 `OnInitGui` 不在启动时执行，普通菜单项不会出现

**实测确认的注意点**：

- 替换 DLL 文件**不会**重新触发 `OnRegister`；要复验注册逻辑必须先注销（`OnUnregister`）再重新注册。
- EPLAN 注册记录保存 DLL 的**绝对路径**。项目目录/文件名变更后，必须在 Add-in 管理器中注销旧项再重新注册。
- `OnRegister` 执行时 GUI 已就绪，可弹 `Decider`；`OnInit` 时 GUI 尚未初始化，弹 GUI 对话框的可行性未经官方证实，慎用。

### DLL 命名约定

官方文档（AddIns.html）：程序集**应**命名为 `<公司名>.EplAddIn.<项目名>.dll`。Add-in 管理器按此约定识别，不要输出不符合该形态的 DLL 名。项目名/DLL 名/命名空间保持一致（如 `EA.EplAddIn.Test` → `EA.EplAddIn.Test.dll`）。

---

## 注册与调试循环

1. `dotnet build` 产出 DLL
2. EPLAN 中：文件 → 设置（或 API 相关入口）→ Add-in 管理器 → 加载/注册 DLL
3. 调试：Visual Studio → 附加到进程 `W3u.exe` / `Eplan.exe`；项目属性勾选 **Enable native code debugging**（EPLAN 底层为混合 C++/C#）
4. 失败时用户提供系统消息窗口内容或日志，助手只凭证据定位，不猜
5. 改名/换路径后：先注销旧 DLL，再注册新 DLL

加载目录与企业批量分发方式（Add-on 包）尚未落地，见"待确认"。

---

## 已验证 API 用法（2.9 离线文档核实签名）

### 消息框：`Eplan.EplApi.Base.Decider`

```csharp
new Decider().Decide(
    EnumDecisionType.eOkDecision,      // 仅 OK 按钮
    "标题",
    "内容",
    EnumDecisionReturn.eOK,            // quiet 模式默认返回
    EnumDecisionReturn.eOK);
```

`EnumDecisionType`：eYesNoDecision / eOkCancelDecision / eOkDecision / eRetryCancelDecision / eYesNoCancelDecision 等。
返回值 `EnumDecisionReturn`：eOK=1, eCANCEL=2, eYES=6, eNO=7 …（注意 eYES≠eOK）。
非阻塞消息输出用 `BaseException.FixMessage()` 写入系统消息窗口。

### 自定义 Action：IEplAction + [DeclareAction]

- `[DeclareAction("ActionName")]` 标注在实现 `IEplAction` 的类上（命名空间 `Eplan.EplApi.Scripting`，程序集 AFu）
- Action 名**禁止包含 `.`**
- 必须实现：`bool Execute(ActionCallingContext ctx)`、`void GetActionProperties(ref ActionProperties p)`、`bool OnRegister(ref string Name, ref int Ordinal)`

### 菜单

`new Menu().AddMenuItem("菜单文本", "ActionName")` —— 2 参数重载，追加到"实用工具/工具"菜单末尾，在 `OnInitGui` 中调用。
右键菜单用 `ContextMenu` + `ContextMenuLocation`：
- **图形编辑器（图纸页面对象）右键**：`DialogName="Editor"`、`ContextMenuName="Ged"`——两个属性分别赋值，**不能拼成 `"Editor.Ged"`**（实测可用，见 EPL-Scripts 的 ContextMenuHelloWorld）。注册：`new ContextMenu().AddMenuItem(loc, "菜单文本", "ActionName", separatorBefore, separatorBehind)`，在 `OnInitGui` 调用
- 菜单项启用/隐藏：`IEplActionEnable.Enabled` **仅对主菜单/工具栏/Ribbon 生效；对 `Editor/Ged` 图形编辑器右键注入项 EPLAN 完全不回调（2.9 日志验证全程无调用）**，`ContextMenu` 也无置灰/隐藏属性。官方菜单项的置灰/隐藏由平台内部代码控制，未开放给第三方。第三方（DanielPa/Eplanwiki SwitchMacroVariant，同样挂 Editor/Ged）也只能常驻+点击时校验。
- 试过且**被日志证伪**的动态时机：① 400ms 轮询（可行但被否决，性能/时序差）；② `new EventHandler("onActionEnd.String.*").NameEvent`——订阅成功，启动期有 selectionset/XGedOpenSchemePage 等内部事件，但**用户在图形编辑器里的点选/框选/右键一条 onActionEnd 都不发**（2.9 实测），不能用来跟踪选择。
- ❌ **WinForms 消息过滤器也已实测证伪（2.9）**：物理右键确认落在 GED 画布（WindowFromPoint pid=EPLAN），右键确实弹出 MFC 菜单（出现 `Afx:...:800...` 弹出窗口类），但 `Application.AddMessageFilter` 全程零回调。根因：EPLAN 主界面是 **MFC/BCG 自己的消息循环（主窗口类 `AfxMDIFrame140u`），不是 WinForms `Application.Run`**，IMessageFilter 只在 WinForms 消息泵被查询，MFC 泵取消息时不经过它。
- ✅ **右键项按选择显隐——最终可行方案（2.9 实测三场景全通过）**：菜单项**常驻注册一次**（`ContextMenu.AddMenuItem(Editor/Ged,...)`），在 `OnInitGui`（MFC UI 线程）用 P/Invoke 装**线程局部**钩子 `SetWindowsHookEx(WH_CALLWNDPROC=4, proc, IntPtr.Zero, GetCurrentThreadId())`：
  - 用 `WH_CALLWNDPROC`（消息送窗口过程**之前**回调，不是 CALLWNDPROCRET），拦 `WM_INITMENU(0x0116)` 与 `WM_INITMENUPOPUP(0x0117)`，`wParam` 即本次弹出菜单的临时 HMENU。
  - 回调里 `GetMenuItemCount` + 逐行 `GetMenuStringW(...,MF_BYPOSITION)` 按菜单文本（去 `&`）定位本插件项；查 `new SelectionSet().Selection` 是否含 `TextBase`：**不含就 `DeleteMenu(hMenu, pos, MF_BYPOSITION)` 从本次菜单删除**（删后项数立即 -1），含则不动（默认亮、可点）。只删每次弹出的临时副本，不动注册，下次弹出框架重新生成。
  - **为什么不能置灰只能删**：GED 右键是 **BCG 自绘菜单**，它在自己的 `OnInitMenuPopup` 里把底层 HMENU 项拷成 BCG 菜单项对象，此后绘制与**命令路由都看 BCG 对象**，标准 `EnableMenuItem(MF_GRAYED)` 即使返回成功也只改了 HMENU 标志——实测视觉可能像灰但**点击照样触发命令**（曾用 CALLWNDPROCRET 置灰，被用户实测推翻）。在 BCG 处理“之前”把项从 HMENU 删掉，BCG 遍历时根本看不到该项，才真正不可点。
  - **关键时序已验证**：右键**未预选**的文本对象时，EPLAN 在右键按下、发出 `WM_INITMENU(POPUP)` 之前就已把该对象选入选择集，故更早的 CALLWNDPROC 钩子里读到的 SelectionSet 已含该 Text（实测直接右键文本→保留），"直接右键对象"场景天然覆盖，无需轮询。
  - 委托必须存为实例字段防 GC 回收；回调全程 try/catch 并 `CallNextHookEx` 透传（绝不吞消息/异常）；`OnExit`/`OnUnregister` 用 `UnhookWindowsHookEx` 卸载。线程钩子无需 DLL 注入、仅影响本进程 UI 线程，比全局钩子安全。
  - 实测硬证据（看项数/存在性与点击，不看颜色）：空白右键→`DeleteMenu=True`、项数 21→20、视觉逐项确认菜单中已无该项；Ctrl+A 后右键→保留（26 项未删）；无预选直接右键文本→保留（27 项）。
  - 注意：打开调试开关 `USER.EnfMVC.ContextMenuSetting.ShowIdentifier` 后，GED 右键菜单底部会多一个显示菜单标识的 **"Editor.Ged" 项**（只读、非插件添加），属调试显示，关掉开关即消失，别误判为插件垃圾项。
- 双屏注意：主屏虚拟原点可能不是 (0,0)（实测上方有第二屏，虚拟 origin (0,-1080)），CUA foreground 点击用虚拟坐标，物理 SetCursorPos 用主屏坐标，两套坐标需换算；Win32 枚举窗口确认命中 pid 比坐标日志可靠。
- 其它对话框内右键 ID（非图纸）仍需先开 `USER.EnfMVC.ContextMenuSetting.ShowIdentifier` 读取真实 DialogName/菜单 ID，禁止猜测（如文本编辑框是 `GedEditGuiText/1002`，表格编辑对话框各有不同 ID）

### 选择集与文本对象（TextBatchEdit 已核实签名）

- 取当前选择：`new Eplan.EplApi.HEServices.SelectionSet().Selection` → `StorableObject[]`（只在单个项目内有效；选中节点时只返回第一个元素）
- 文本对象：自由文本 `Text`、路径文本 `PathText` 的共同基类是 `Eplan.EplApi.DataModel.Graphics.TextBase`；用 `.OfType<TextBase>()` 一次覆盖两类。**注意命名空间冲突**：该命名空间下还有 `Color` 类型，与 `System.Drawing.Color` 冲突时写完全限定名；同理 `Menu` 用 `Eplan.EplApi.Gui.Menu`
- 多语言内容：`TextBase.Contents` 是 `Eplan.EplApi.Base.MultiLangString`（get/set 都有）
  - **写回必须走 setter 整体赋回**：`var mls=new MultiLangString(); ...; text.Contents = mls;`。只对 getter 返回对象就地 `AddString/DeleteString` 不落库（实测回写不生效）
  - 读：`GetString(ISOCode.Language.L_zh_CN)` / `L_en_US`；枚举名带 `L_` 前缀（`L_zh_CN`=100 简体中文，未设置该语言时的行为待实测，调用方 try/catch 兜底）
  - 写：`AddString(lang, val)`；删：`DeleteString(lang)`
  - 判断已有语言：`var list = new LanguageList(); contents.GetLanguageList(ref list);`，元素访问必须用 C++/CLI 访问器 **`list.get_Language(i)`**（C# 写 `list.Language[i]` 报 CS1546）
  - **翻译 vs 未翻译**：未翻译文本是"语言无关串"（语言码 `L___`=0，所有语言显示相同）；语言列表为空或含 `L___` 即为未翻译。**写语言无关串用 `AddString(ISOCode.Language.L___, 值)`**；读干净值优先 `GetStringToDisplay(源语言)`/`GetString(L___)`（多读法对比后取不带前缀者，DEBUG 留证）。**`InternalString` 是带内部语言标记的编码串**（形如 `??_??@文本;`），不能直接当显示值
- **修改落库与撤销栈（实测纠正）**：
  - `LockingStep` 只是对象锁容器（收集/释放锁柄，用于多人协作），**与撤销无关**；内置 `IEplAction`/模态对话框中平台隐式提供，**不要显式 new**（显式裸锁改数据会导致 EPLAN 撤销栈被清空、Ctrl+Z 和撤销按钮变灰）
  - 让修改进入撤销栈的正确范式：`UndoStep` + `Transaction`：
    ```csharp
    var undo = new UndoManager().CreateUndoStep();
    undo.SetUndoDescription("步骤提示");
    using (var txn = new TransactionManager().CreateTransaction()) {
        // ... 修改对象 ...
        txn.Commit();
    }
    undo.CloseOpenUndo();   // 成为一个可 Ctrl+Z 的撤销点；绝不能调 undo.DoUndo()（那是立即编程撤销）
    undo.Dispose();
    ```
  - 异常路径 `txn.Abort()`；无模式对话框/离线 EXE 才需另加显式 `LockingStep`
  - **回读校验（防"UI 成功、模型未落库"）**：`txn.Commit()` 后重新读 `t.Contents`，逐语言比对期望值；不一致记 ERROR 并在结果对话框列出问题行，不假设全部生效
  - **项目源语言 / 项目翻译语言（项目层级设置，可人为调整，各项目不同）**——设置 ID 权威来源：EPLAN 安装目录 `Bin\en-US\SettingsDesc.csv`（UTF-16，`XTrProjectSettingsTab`）；真实存储格式以"项目设置导出 XML"为准（`<CAT PROJECT><MOD TRANSLATEGUI>`）：
    - **键不带 `PROJECT.` 前缀**：`ProjectSettings` 已锚定 PROJECT 分类，`project.Settings.GetStringSetting("TRANSLATEGUI.xxx", 0)`；带前缀反而抛 `S063108 设置路径不存在`（实测踩坑）
    - 翻译语言集合：`TRANSLATEGUI.TRANSLATE_LANGUAGES` 是**单个分号串**（索引 0，如 `"en_US;zh_CN;ja_JP;"`），按 `';'` 拆分——**不是按索引多值**（我上一版按多值读，读空后只剩源语言，setter 整体替换还把其他语言清掉了）
    - 源语言：`TRANSLATEGUI.SOURCE_LANGUAGE`（单值短码 `zh_CN`）；失败回退只读属性 `Properties.PROJ_SOURCELANGUAGE.ToInt()`
    - 显示语言：`TRANSLATEGUI.DISPLAYED_LANGUAGES`（同样分号串，如 `zh_CN;en_US;`）
    - 短码↔枚举：`new ISOCode().SetString(code)` 后无参 `GetNumber()`；枚举→短码 `GetString(lang)`
  - **表格语言列结构**：独立"源语言"列置于最左，右侧依次为全部翻译语言（含源语言本身，按设置固定顺序）；源语言列与语言区里的源语言列双向同步值。未翻译行仅源语言列可编辑（语言无关串），语言区只读
  - **"不自动翻译"标志**：`TextBase.IsAutomaticallyTranslated`（`bool { get; set; }`，DataModelu.dll），文档原文对应 UI 的 "Do not translate automatically"。**语义反相**：UI 勾选"不自动翻译"= 属性设 `false`。它与"多语言/语言无关串"是两个相互独立的属性，互不影响（另有 `LanguageMode`（`TextBase.TextLanguageMode` 枚举）+ `FixedLanguage` 控制单语言显示模式，本插件未用）

---

## 当前子项目：EA.EplAddIn.Test

Hello World 骨架，验证 Add-in 全链路：

- `Class1 : IEplAddIn`：`OnRegister` 设 `bLoadOnStart = true` 并弹"插件已加载！"提示（标题 "MyFirst Add-in"）；`OnInitGui` 注册"Hello World"菜单
- `HelloWorldAction : IEplAction`：`[DeclareAction("HelloWorldAction")]`，执行时弹"Hello World!"
- `TableEditorForm` / `TableEditorAction`：WinForms `DataGridView` 表格式编辑窗口（4 列演示数据，就地编辑、底部空行新增），支持矩形区域 Ctrl+C/X/V 块复制粘贴（Tab/换行分隔，可与 Excel 互贴）、Delete 清空、右键菜单（剪切/复制/粘贴/清除/全选）；菜单项"表格式编辑（演示）"以模态 `ShowDialog()` 打开；暂未接 EPLAN 数据。EPLAN 公共 API 无表格控件（Gui 命名空间仅 7 类型），此类界面只能用 WinForms 自行实现

这是验证用骨架。业务功能成熟前，本仓库只提交基础脚手架与通用工具，核心业务逻辑公开范围由 Xinlly 决定。

---

## 通用知识指针（不重复维护）

以下内容在 EPL-Scripts 仓库的 AGENT.md 中，Add-in 同样适用，需要时去查：

- EPLAN 程序集 ↔ 命名空间对照表、可用 API 边界
- Scripts / Add-ins / Add-ons 区别、.NET 4.7.2 环境信息
- 核心 API 速查（Base / ApplicationFramework / Gui / DataModel / HEServices / MasterData）
- 常用内置 Action 列表、性能优化（LockingStep / UndoManager / DMObjectsFinder）
- 隐藏设置、常见坑点、2.9 特有问题、社区资源链接
- 离线 API 文档：EPL-Scripts 仓库的 `References/api-2.9/`（HTML + SQLite，917 类型 / 39043 成员，查 API 签名首选 SQLite，禁止凭记忆写签名）

---

## 待确认

- [ ] EPLAN 2.9 Add-in 的标准加载目录（`$(MD_ADDINS)` 还是安装目录 `Bin\AddIns`）
- [ ] 是否需要 post-build 自动拷贝 DLL 到加载目录
- [ ] Add-on 打包与 100 人规模集中分发/更新机制
- [ ] `OnInit` 阶段弹 GUI 对话框的可行性（当前提示放在 `OnRegister`）
- [ ] `.gitignore` 落地（至少包含 `bin/`、`obj/`、`.vs/`、`references/`、`*.user`）

---

*最后更新：2026-09-14*
