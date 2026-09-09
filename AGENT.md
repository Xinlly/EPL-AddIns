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
├── EA.EplAddIn.Test/            # 第一个 Add-in：Hello World 骨架
│   ├── EA.EplAddIn.Test.csproj
│   ├── Class1.cs                # IEplAddIn 实现：注册/生命周期/菜单
│   └── HelloWorldAction.cs      # IEplAction 实现
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
- 重命名项目/目录后若出现怪问题，删除 `.vs/`、`bin/`、`obj/` 后重新生成。

### 协作约定（AI 协作者必守）

- **未经用户明确要求，不要主动执行编译。** 代码修改与构建验证是两个动作，修改完成后等待指示再 build。
- 修改代码只动与需求直接相关的行，禁止整文件重写、顺手改格式/注释。
- EPLAN 实际加载、注册、运行测试由用户在 Windows 的 EPLAN 中完成；助手无法启动 EPLAN，不得声称"已验证运行"。

### Git 提交与推送身份

- **默认规则：助手（Dora）代做的提交，作者/提交者一律署名 `Dora <dora@noreply.local>`**（不归属任何 GitHub 账号；注意不要用 `xxx@users.noreply.github.com` 格式，那会归属到真实同名账号）。
- 仅当用户明确说明"这是我的提交"时，才使用用户身份 `Xinlly <Xinlly@outlook.com>`。
- 实现方式：不改仓库 git config；提交时用当次环境变量 `GIT_AUTHOR_NAME/EMAIL`、`GIT_COMMITTER_NAME/EMAIL`，不影响用户在 VS 中的手动提交。
- 推送认证始终使用 Xinlly 的 GitHub token（GitHub 记录的 pusher 是 Xinlly，作者是 Dora，两者独立）；WSL 侧推送走 `https_proxy=http://127.0.0.1:35353`，token 不写入 remote 配置。
- 重写历史用 `--force-with-lease`；推送到显式 URL 时需写成 `--force-with-lease=main:<远端当前sha>`，否则 lease 校验找不到跟踪引用。

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
右键菜单需用 `ContextMenu` + `ContextMenuLocation`（先开 `USER.EnfMVC.ContextMenuSetting.ShowIdentifier` 查真实菜单 ID，禁止凭猜测写 ID）。

---

## 当前子项目：EA.EplAddIn.Test

Hello World 骨架，验证 Add-in 全链路：

- `Class1 : IEplAddIn`：`OnRegister` 设 `bLoadOnStart = true` 并弹"插件已加载！"提示（标题 "MyFirst Add-in"）；`OnInitGui` 注册"Hello World"菜单
- `HelloWorldAction : IEplAction`：`[DeclareAction("HelloWorldAction")]`，执行时弹"Hello World!"

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

*最后更新：2026-09-09*
