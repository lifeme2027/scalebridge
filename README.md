# ScaleBridge

跨平台桌面蓝牙体重秤接收器：**不需要手机 App**，在 Windows 和 Linux 上直接发现体重秤、连接、实时接收体重并在本机显示，可导出 CSV / JSON。

> **状态：阶段 2（进行中）。** 已有 Core 协议解析、演示用的 Fake 适配器、Avalonia 界面、SQLite 历史、CSV/JSON 导出与 CI/打包；
> Windows 版额外带一个 WinRT 真实蓝牙适配器，**实验性（仅按规范实现，未真机验证）**：默认仍是「演示数据」，需要在设置里手动切换。
> Linux 版仍然只有「演示数据」（BlueZ 适配器尚未接入界面）。**没有任何真实体重秤验证过。**

## 诚实声明

* **没有任何真机验证。** 本项目没有在任何真实体重秤或真实蓝牙硬件上测试过。
* 标准 Weight Scale（0x181D）与 Body Composition（0x181B）的解析目前只有**字节向量单元测试**，
  一律标注为 **「实验性（仅按规范实现，未真机验证）」**，README 与界面里都是如此。
* 小米、Renpho、芯海（OKOK）等私有协议**没有任何一款算「支持」**：仓库里只有按公开资料写的**实验性**解析器与手工向量（见下文「厂商协议（实验性）」），
  没有真机验证、也没有接入连接流程；Renpho 没有解析器。
* 界面里 Fake 适配器产生的是 **「演示数据」**，不是真实体重。

## 纯本地、无遥测

* 程序不发起任何网络请求，没有遥测、统计或更新检查。
* 历史记录和设置保存在本机应用数据目录的 SQLite 文件里；详见 [PRIVACY.md](PRIVACY.md)。

## 技术栈与结构

.NET 8 + Avalonia（MVVM）。

```
src/ScaleBridge.Core          纯 C#（net8.0）：协议解析、数据模型、导出、IBleAdapter 抽象；不依赖任何蓝牙库
src/ScaleBridge.Ble.Fake      演示数据适配器（9 个脚本场景回放，供界面演示与无硬件的 CI 端到端测试）
src/ScaleBridge.Data          SQLite 历史与设置（本地文件，无网络）
src/ScaleBridge.App           Avalonia 桌面界面（程序集名 ScaleBridge；中/英文运行时切换）
src/ScaleBridge.Windows.Core  Windows 适配器的平台无关逻辑（看门狗、状态机、错误映射、广播缓存；在 Linux 上也能跑单元测试）
src/ScaleBridge.Windows       WinRT 外壳（net8.0-windows10.0.19041.0）：只做 WinRT 调用与映射，实验性（仅按规范实现，未真机验证）
src/ScaleBridge.Linux         BlueZ 适配器的探索性脚手架（他人加入，阶段 2；未接入界面，实验性）
tests/ScaleBridge.Core.Tests  xUnit：协议字节向量、导出、诚实标注守卫
tests/ScaleBridge.Data.Tests  xUnit：SQLite 仓库
tests/ScaleBridge.Fake.Tests  xUnit：9 个演示场景的回放
tests/ScaleBridge.App.Tests   xUnit：本地化与 ViewModel 全流程（扫描→连接→接收→历史→导出，使用 Fake）与适配器选择/持久化
tests/ScaleBridge.Windows.Core.Tests  xUnit：Windows 适配器的纯逻辑（用内存替身，Linux 上也跑）
tests/ScaleBridge.Windows.Tests       xUnit：WinRT 外壳（只在 Windows 上运行；不需要蓝牙硬件）
tools/ParseCli                命令行解析工具（引用 Core；不参与发布打包）
shared-vectors/               与语言无关的 GATT 字节向量 JSON（见其 README）
```

协议信息来源与实现取舍见 [NOTES.md](NOTES.md)。

## 运行桌面程序（目前只有「演示数据」）

```bash
dotnet run --project src/ScaleBridge.App                          # 默认：Fake 适配器 + 系统语言
dotnet run --project src/ScaleBridge.App -- --list-scenarios      # 列出演示场景
dotnet run --project src/ScaleBridge.App -- --scenario reconnect --lang zh
```

Windows（PowerShell）与 Linux 用法相同；发布包里直接运行 `ScaleBridge.exe` / `./ScaleBridge` 并带同样的参数。
**界面上所有来自 Fake 的读数都带「演示数据」标记，并常驻「实验性（仅按规范实现，未真机验证）」提示；它们不是真实体重。**

| 参数 | 作用 |
|---|---|
| `--scenario <id>` | 选演示场景（也可在「设置」页选）：`normal-si`、`imperial`、`body-composition`、`timestamp-user-bmi-height`、`invalid-0xffff`、`truncated-packet`、`mid-connection-disconnect`、`reconnect`、`multi-device` |
| `--list-scenarios` | 打印场景并退出 |
| `--fast` | 回放不等待（演示/截图用） |
| `--lang zh\|en` | 覆盖语言；默认跟随系统，可在界面里随时切换 |
| `--scan-timeout <秒>` | 扫描最长持续时间，到点自动停止并提示「发现 N 台」或「未发现设备」（可点「重新扫描」）。默认 30 秒（`--fast`/`--smoke`/`--autoplay` 下 3 秒）；`0` = 不限时。也可用环境变量 `SCALEBRIDGE_SCAN_TIMEOUT`（秒），命令行优先 |
| `--autoplay` / `--tab 0-3` | 启动后自动 扫描→连接，并停在指定页（设备/实时/历史/设置）；演示与截图辅助 |
| `--version` | 打印版本与构建提交 SHA（两行：`ScaleBridge 0.1.0` / `commit: <sha>`）后退出。SHA 由构建时的 git 检出自动写入程序集；没有注入（例如从没有 `.git` 的源码压缩包构建）时打印 `commit: unknown`，不会伪造。同样的信息也显示在「设置」页底部 |
| `--smoke` | 无人值守自检：扫描→连接→收到数据→写入历史，打印 `SCALEBRIDGE_SMOKE_OK` 后退出（CI 用） |
| 环境变量 `SCALEBRIDGE_DATA_DIR` | 改数据目录（默认见 [PRIVACY.md](PRIVACY.md)） |

键盘：`F5` 开始扫描，`Esc` 停止扫描，`Ctrl+1..4` 切换页签；按钮与表格也可以用 Tab 与方向键操作。
导出：CSV 为 UTF-8（带 BOM）+ CRLF，以 `=` `+` `-` `@` 开头的文本前面补 `'` 防公式注入；JSON 带 `format` 与 `version` 字段。
适配器选择：Linux 构建只有「演示数据（Fake）」；Windows 构建多一个「真实蓝牙」选项，默认仍是「演示数据」，
选择会保存在设置里。真实蓝牙标注为 **实验性（仅按规范实现，未真机验证）**，蓝牙缺失或未打开时会给出提示而不是崩溃；
`--smoke` 一律强制使用演示数据。详见 [NOTES.md](NOTES.md) 的「Windows 适配器」。

已知限制：Weight Scale 与 Body Composition 的记录**不会合并**成一条；没有历史条数上限或接收节流；
BCS 多包重组尚未实现，见 [NOTES.md](NOTES.md)。

## 打包与 CI

* `.github/workflows/ci.yml`：Ubuntu + Windows 构建并跑全部测试；Ubuntu 上另有 xvfb 下的应用启动冒烟（9 个场景各启动一次）。
* `.github/workflows/release.yml`：手动触发或推送 `v*` 标签时，为 `win-x64`、`linux-x64` 发布**自包含单文件**
  （内含 .NET 运行时，不需要另装；体积较大）并作为 Actions 构件上传。每个构件名与压缩包名都带构建提交的前 7 位 SHA：
  `ScaleBridge-win-x64-<sha7>.zip`、`ScaleBridge-linux-x64-<sha7>.tar.gz`，旁边附 `SHA256SUMS.txt` 与 `BUILD-INFO.txt`（见下一节）。
  **该工作流只产出构件，不会创建 GitHub Release。**
* 当前没有代码签名；Windows 可能出现 SmartScreen 提示。

## 构件与版本

每个 release 构件都能追溯到构建它的提交（这是因为早先出现过「构件由 A 提交构建、却被当作 B 提交验收」的错位）：

* 构件文件名里的 `<sha7>` 是构建提交的前 7 位；可执行文件的版本号内嵌为 `0.1.0+<完整 40 位 SHA>`（`Directory.Build.props` 的 `Version` + `-p:SourceRevisionId`）。
  工作流会在打包前检查检出的 `HEAD` 就是触发它的提交，并检查该字符串确实在可执行文件里，否则失败。
* 包里有 `BUILD-COMMIT.txt`（提交、运行链接、版本）；构件里压缩包旁边还有：
  * `SHA256SUMS.txt`：压缩包的 SHA-256；
  * `BUILD-INFO.txt`：提交、工作流运行链接、构建日期（UTC）、dotnet SDK 版本、文件名、大小与 SHA-256。
* 校验：从 Actions 页面下载构件（得到一个含压缩包、`SHA256SUMS.txt`、`BUILD-INFO.txt` 的 zip），解压到同一个目录后——
  ```bash
  sha256sum -c SHA256SUMS.txt          # Linux / macOS；输出 "…: OK" 才算通过
  ```
  ```powershell
  (Get-FileHash -Algorithm SHA256 .\ScaleBridge-win-x64-<sha7>.zip).Hash   # 与 SHA256SUMS.txt 里的值（不区分大小写）比较
  ```
* 看它是哪个提交构建的：`./ScaleBridge --version` 输出 `ScaleBridge 0.1.0` 和 `commit: <SHA>`（设置页也会显示）；
  Windows 构件可对照 `BUILD-INFO.txt` / `BUILD-COMMIT.txt`。把输出的 SHA 与你要验收的提交比较。
* 局限（如实说明）：SHA-256 只能证明「你拿到的文件和工作流上传时一致」，**不是代码签名**，也不证明构建环境可信；
  本项目目前没有代码签名，也没有可复现构建的承诺。Windows 构件在 CI 里只做了构建与内嵌版本检查，没有在 CI 里运行；
  **所有蓝牙行为仍是「实验性（仅按规范/公开资料实现，未真机验证）」**。

## 构建与测试

```bash
dotnet build
dotnet test
```

需要 .NET 8 SDK（`global.json` 固定 8.0.100，允许 latestFeature 滚动）。

## 命令行解析工具 `tools/ParseCli`

把一段 GATT 字节交给 Core 的解析器，输出与 `shared-vectors` 同形的 JSON（`reject` / `errorCode` / `warnings` / `expect`，十进制值一律是字符串）。
**用途：让别的实现（独立写的向量、Web 版、第三方脚本）直接对照 C# 解析器；它不是产品功能，不进发布包。** 同样是「实验性（仅按规范实现，未真机验证）」。

```bash
# 单条
dotnet run --project tools/ParseCli -- --service 181D --char 2A9D --hex "00 59 36"

# stdin：每行一条；行首可写 "服务/特征 字节"，否则用 --service/--char 的默认值；空行和 # 开头的行忽略；输出 JSON Lines
printf '181D/2A9D 00 FF FF\n181B/2A9C 00 00 EA 00\n' | dotnet run --project tools/ParseCli -- --stdin

# 向量文件（scalebridge-vectors/1）：输出同格式文档，里面是「实际」解析结果
dotnet run --project tools/ParseCli -- --vectors shared-vectors/weight-scale.json
# 与文件里写的期望比较：列出每条差异，有差异退出码 1（可喂别人写的向量文件）
dotnet run --project tools/ParseCli -- --vectors their-vectors.json --compare
```

支持 `181D/2A9E`、`181D/2A9D`、`181B/2A9B`、`181B/2A9C`。字节可写成 `00 59 36`、`005936`、`00:59:36`；UUID 可带 `0x`。
退出码：`0` 成功，`1` `--compare` 有差异，`2` 用法或输入错误（stdin 模式下坏行输出 `{"line":N,"cliError":…}` 并继续）。
比较规则与 `SharedVectorsTests` 一致：`reject`/`errorCode` 相同，成功时警告集合相同、`expect` 逐键相同（十进制串按数值比较）。

## Web 版（`web/`）

静态单页，用浏览器的 Web Bluetooth 接收标准体重秤数据，数据只存在本浏览器的 IndexedDB 里，不上传；目标部署位置 `https://life.kastop.com/box`。
真实蓝牙路径同样是 **「实验性（仅按规范实现，未真机验证）」**；不支持 Web Bluetooth 的浏览器可用 **「演示数据」** 模式与 CSV/JSON 导入导出。
厂商广播类私有协议在 Web 版暂未支持。浏览器限制（仅 Chromium、需 HTTPS 与用户点击、只能访问声明的服务等）、`web/tools/parse-cli.mjs` 的用法和部署同步方式见 [web/README.md](web/README.md)。
CI 为独立的 `.github/workflows/web.yml`（与 .NET 的 ci.yml 互不影响）。

## 许可证与致谢

MIT，见 [LICENSE](LICENSE)。第三方依赖及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)，
版权与分发声明见 [NOTICE](NOTICE)；协议资料来源与净室过程记录在 THIRD-PARTY-NOTICES.md 的第 3、4 节。

感谢 [ble-scale-sync](https://github.com/KristianP26/ble-scale-sync)（GPL-3.0）——它只作为
**事实性参考**（有哪些设备/协议存在）。**本项目没有复制或改写它的任何代码、文件结构或测试**；
所有解析器都按公开的 Bluetooth SIG 规范独立实现。

## 厂商协议（实验性）

`src/ScaleBridge.Core/Protocols/` 里有按**公开文字资料**独立实现的厂商私有协议解析器。它们**一律是「实验性（仅按规范/公开资料实现，未真机验证）」**：
只有手工构造的字节向量（`shared-vectors/vendor/`）和单元测试，**没有任何真机验证**，界面与文档不得写成「支持」。
字节布局的来源页面、取到的事实以及未确认项见 [NOTES.md](NOTES.md) 的「厂商协议来源（时尚1）」；实现遵守净室规则（不使用 GPL 项目的代码或结构）。

| 厂商 | 状态 |
|---|---|
| 小米 Mi Scale / Mi Body Composition Scale 2（广播 Service Data 0x181D / 0x181B） | 实验性：已有解析器与向量；阻抗只给原始值 |
| Renpho | **跳过**：没有可合规使用的公开文档（只有 GPL 项目源码 / 反编译来源），不提供解析器 |
| Chipsea / OKOK 风格广播体重帧（manufacturer data，`0xCA 0x11` 19 字节 / `0xCA 0x20` 15 字节） | 实验性：已有解析器与向量；v2.0 帧不含单位（调用方给定或按 kg 并告警）；C0 变体与连接后帧未实现 |
