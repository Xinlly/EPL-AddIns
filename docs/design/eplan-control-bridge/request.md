# EPLAN 控制桥（Eplan Control Bridge）— 需求输入（待规划）

> 状态：xavier 原始需求，2026-09-19。本文只记录需求与边界，**不含实现结论**；由 Planner 探测 EPLAN 2.9 API 后产出可行性评估与 MVP 规划。从简单入手，初期不做复杂功能。

## 1. 背景与动机
- 目前让 AI 操控 EPLAN 主要靠 **CUA / 视觉识别点按钮**，太不可靠（像素、分辨率、语言、弹窗、焦点都会导致误操作）。
- 希望做一个**运行在 EPLAN 进程内的新 Add-in**，对外提供稳定的**程序化操控接口**，让 AI（Hermes/Dora）能随时、可靠地调用 EPLAN API，而不是模拟鼠标键盘。

## 2. xavier 原话（需求源头）
> 准备开个新插件，通过该插件控制 eplan 的窗口以及按钮，主要是能够灵活控制 eplan，而不是局限于当前通过 cua 控制（视觉控制太不可靠了）。
> 这个插件用于探测 eplan 的接口，灵活执行你想要的脚本或灵活调用数据模型，提供可靠日志分析，能让你随时灵活控制、调用 EPLAN API，为以后插件开发提供帮助。
> 或许我说的无法实现，你先开个新工作区让 Planner 去规划，刚开始可以不实现复杂功能，从简单入手。

## 3. 期望能力（按优先级，未承诺全部实现）
1. **接口探测/枚举**：列出当前 EPLAN 可调用的 Action、数据模型对象/类型、菜单或命令，供 AI 发现"能做什么"。
2. **灵活执行**：执行指定 Action（带参数）、运行脚本片段、或调用数据模型查询/操作。
3. **数据模型访问**：读取（初期以只读为主）项目、页、设备、文本等对象数据。
4. **可靠日志与结果回传**：每次调用返回结构化结果（成功/失败、返回值、异常类型与消息、耗时、EPLAN 日志），便于 AI 判断与排错。
5. **窗口/按钮控制（可行性待探）**：优先走 Action/API 层完成等价操作；对"原生对话框内部控件"能否程序化驱动需专门评估，不能驱动的要明确列出并给替代 Action。

## 4. 初步形态设想（供 Planner 评估，不是定论）
- Add-in 内启动一个**仅绑定 loopback（127.0.0.1）** 的本地通道（候选：net472 内置 `HttpListener` 的 HTTP/JSON，或命名管道），外部 AI 发结构化命令，Add-in **封送到 EPLAN 主线程**执行后回 JSON。
- 关注仓库已实证存在的 `Eplan.EplApi.Base` 内 `EplanMainThreadDispatcher`（ExecuteInMainThreadSync/Async、CanAccessMainThread），作为主线程封送候选。
- 安全：loopback-only、启动需显式开启、token 或本机鉴权、**只读/写操作分级与白名单**、危险操作（删除/批量改/退出/保存）需显式确认或默认禁用。

## 5. 必须先回答的可行性问题（Planner 调研项）
- EPLAN 2.9 下：枚举全部已注册 Action 的官方途径是什么？ActionManager / `Eplan.EplApi.ApplicationFramework` 能力边界？
- 运行脚本片段的官方支持方式（Scripting / XPrjAction / 加载外部脚本文件）？能否动态执行传入代码，还是只能执行预注册 Action？
- 数据模型只读遍历的最小路径与权限要求。
- 能否枚举/触发菜单、Ribbon 命令；能否枚举/操控原生对话框与子控件（预期大多不行，需证据）。
- 进程内常驻 HTTP/命名管道服务在 EPLAN 生命周期中的启停位置（OnInitGui / OnExit）、ShadowCopy 加载、与现有 Add-in 的共存。
- 线程模型：后台通道线程如何安全切回 EPLAN 主线程（结合真机线程探针结论）。

## 6. MVP 建议方向（最终由 Planner 定，越小越好）
- 建议最小闭环：加载 Add-in → loopback 服务起来 →
  1) `ping/version`（返回 EPLAN 版本、插件版本、主线程可达性）；
  2) `actions/list`（枚举可用 Action，哪怕先返回一部分/分类）；
  3) `action/run`（执行一个**安全白名单内**的 Action，带参数，回结构化结果与日志）；
  4) `model/query` 一个最小只读样例（如当前项目名 / 选中对象类型计数）。
- 对话框控件驱动、动态脚本执行、写操作编排留到后续迭代。

## 7. 约束
- 独立的新 Add-in 项目，不改动 TextBatchEdit 既有功能；复用本仓库的工程结构、`references/EplApi` 引用方式、构建脚本（动态版本号）、日志器与发布流程。
- 目标 EPLAN 2.9、.NET Framework 4.7.2；不引入重依赖（HTTP 优先用框架自带 `HttpListener`）。
- 每个可发布版本走 Debug/Release 0/0 + fresh review + 真机验证（EPLAN 只能真机点）。
- 默认安全：不显式开启不对网络暴露；写操作默认关闭。
