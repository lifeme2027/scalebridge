# 第三方声明（THIRD-PARTY-NOTICES）

ScaleBridge 自身以 MIT 许可证发布（见 [LICENSE](LICENSE)、[NOTICE](NOTICE)）。本文件列出：

1. 实际使用并随构件分发的第三方软件包（名称 / 版本 / 许可证 / 来源），以及它们是怎么核实的；
2. 仅用于构建/测试、不随构件分发的包；
3. 协议与规范的参考来源；
4. 与 GPL 项目的关系声明与净室过程记录。

> 核实时间：2026-10-26（Asia/Shanghai）。版本以 `*.csproj` 里固定的版本为准，会随依赖升级而变化；升级依赖时请同步更新本文件。
> 本项目没有任何真机验证；所有协议实现一律是「实验性（仅按规范/公开资料实现，未真机验证）」。

## 1. 随构件分发的第三方软件

### 1.1 核实方式（不是按名字猜的）

* 仓库没有 `packages.lock.json`，所以用 NuGet 的还原结果核实：对 `src/ScaleBridge.App`（两个目标框架 `net8.0` 与
  `net8.0-windows10.0.19041.0`）分别在 `-r linux-x64`、`-r win-x64` 下 `dotnet restore`，读取 `obj/project.assets.json`
  里全部 `package` 类型的条目（包含传递依赖）；对 `ScaleBridge.Data`、`ScaleBridge.Linux`、各测试项目同样处理。
* 许可证取自还原到本机 NuGet 缓存里**每个包自带的 `.nuspec`**（`<license type="expression">` 的 SPDX 表达式）和包内
  `LICENSE` / `THIRD-PARTY-NOTICES` 文件，并打开了其中的文件头核对；`.nuspec` 没有 SPDX 表达式的包单独标注。
* 原生库的归属另外用 `dotnet publish -r linux-x64 --self-contained`（`-f net8.0`）实际发布后列出输出目录核对：
  确有 `libSkiaSharp.so`、`libHarfBuzzSharp.so`、`libe_sqlite3.so`、`Tmds.DBus.Protocol.dll` 等。
  Windows 构件的原生部分（`libSkiaSharp.dll`、`libHarfBuzzSharp.dll`、`e_sqlite3.dll`、ANGLE）按包内 `runtimes/win-x64/native`
  与 `.nuspec` 推断，**本地没有做 Windows 发布，未逐文件核对**（CI 的 win-x64 构件是实际产物，可自行核对）。
* 用到的版本固定写在：`src/ScaleBridge.App/ScaleBridge.App.csproj`、`src/ScaleBridge.Data/ScaleBridge.Data.csproj`。

### 1.2 直接依赖（`*.csproj` 里的 `PackageReference`）

| 包 | 版本 | 许可证 | 用途 | 来源 |
|---|---|---|---|---|
| Avalonia | 11.3.13 | MIT | 桌面界面框架 | <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia.Desktop | 11.3.13 | MIT | 平台后端聚合 | 同上 |
| Avalonia.Themes.Fluent | 11.3.13 | MIT | 主题 | 同上 |
| Avalonia.Controls.DataGrid | 11.3.13 | MIT | 历史记录表格 | 同上 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM 辅助（源生成器 + 运行库） | <https://github.com/CommunityToolkit/dotnet> |
| Microsoft.Data.Sqlite | 8.0.32 | MIT | 本地历史/设置的 SQLite 访问（`ScaleBridge.Data`） | <https://github.com/dotnet/efcore> |
| Tmds.DBus.Protocol | 0.95.1 | MIT | BlueZ 适配器的 D-Bus 访问（`src/ScaleBridge.Linux`）；**`ScaleBridge.App.csproj` 目前没有 `ProjectReference` 指向 `ScaleBridge.Linux`，所以 0.95.1 这个版本不在构件里**，构件里的是下面 1.3 的 0.21.2（见 1.4 的安全提示） | <https://github.com/tmds/Tmds.DBus> |
| Microsoft.NET.ILLink.Tasks | 8.0.32 | MIT | 构建期（单文件发布的裁剪工具链），不进入运行时输出；<https://github.com/dotnet/runtime> |

### 1.3 传递依赖（随 App 构件分发的部分）

| 包 | 版本 | 许可证（`.nuspec`） | 说明 / 来源 |
|---|---|---|---|
| Avalonia.X11 / Avalonia.FreeDesktop / Avalonia.Win32 / Avalonia.Native / Avalonia.Skia / Avalonia.Remote.Protocol | 11.3.13 | MIT | Avalonia 平台后端；<https://github.com/AvaloniaUI/Avalonia>（还原图里另有 `SkiaSharp/HarfBuzzSharp.NativeAssets.macOS` 与 `.WebAssembly`，不是本项目目标 RID，不随 linux-x64 / win-x64 构件分发；Avalonia.Native 的 macOS 原生库同理） |
| Avalonia.BuildServices | 11.3.2 | MIT | 构建期包，不进入运行时输出；<https://github.com/AvaloniaUI/Avalonia.BuildServices> |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | `.nuspec` 为 `type="file"`：包内 `LICENSE` 是 ANGLE 的 BSD 风格许可证（已打开核对文件头） | 仅 Windows（ANGLE 的 Direct3D 后端）；<https://github.com/AvaloniaUI/angle> |
| SkiaSharp | 2.88.9 | MIT（Xamarin/Microsoft） | 2D 渲染的 .NET 绑定；<https://github.com/mono/SkiaSharp> |
| SkiaSharp.NativeAssets.Linux / .Win32 | 2.88.9 | MIT（绑定）；原生库内含 Skia 等，见下 | 原生 `libSkiaSharp`；包内 `THIRD-PARTY-NOTICES.txt` 列出的组件：ANGLE、HarfBuzz、skia、etc1、gif、libpng、DNG SDK、expat、freetype、ICU、imgui、jsoncpp、libjpeg-turbo、libwebp、libmicrohttpd、piex、sdl、sfntly、SPIR-V Headers/Tools、zlib，**全文已随构件放进 `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`** |
| HarfBuzzSharp | 8.3.1.1 | MIT | 文字排版（HarfBuzz，「Old MIT」许可证）的 .NET 绑定；<https://github.com/mono/SkiaSharp> |
| HarfBuzzSharp.NativeAssets.Linux / .Win32 | 8.3.1.1 | MIT | 原生 `libHarfBuzzSharp`；声明同上 |
| MicroCom.Runtime | 0.11.0 | MIT | Avalonia 的 COM 互操作；<https://github.com/kekekeks/MicroCom> |
| Tmds.DBus.Protocol | **0.21.2**（经 Avalonia.FreeDesktop 传递引入） | MIT | Avalonia 的 Linux 后端使用；<https://github.com/tmds/Tmds.DBus>。**见 1.4 的安全提示** |
| System.IO.Pipelines | 8.0.0 | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Data.Sqlite.Core | 8.0.32 | MIT | `Microsoft.Data.Sqlite` 的核心；<https://github.com/dotnet/efcore> |
| SQLitePCLRaw.core / .provider.e_sqlite3 / .lib.e_sqlite3 / .bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | SQLite 的 .NET 原生绑定与捆绑的原生库 `libe_sqlite3`（linux-x64）/ `e_sqlite3.dll`（win-x64）；作者 Eric Sink，<https://github.com/ericsink/SQLitePCL.raw> |
| SQLite（`e_sqlite3` 内的 C 库） | 随 SQLitePCLRaw 2.1.13 | 公有领域（以 <https://sqlite.org/copyright.html> 页面文字为据：「SQLite Is Public Domain」） | <https://sqlite.org> |
| System.Memory | 4.5.3 | `.nuspec` 无 SPDX，`licenseUrl` 指向 dotnet/corefx 的 LICENSE.TXT；包内 `LICENSE.TXT` 为 MIT（已打开核对） | 只出现在 netstandard 依赖图中，net8.0 下由运行时提供，**发布输出里的 `System.Memory.dll` 来自 .NET 运行时而不是这个包** |

### 1.4 自包含发布内置的 .NET 运行时，以及两条需要说明的事

* **.NET 运行时**：release 构件是自包含单文件，内置 `Microsoft.NETCore.App.Runtime.<rid>` 8.0.32（`.nuspec` 许可证 MIT，
  来源 <https://github.com/dotnet/runtime>，包内 `THIRD-PARTY-NOTICES.TXT` 列出其自带的第三方声明）。
* **Windows SDK .NET 投影（仅 win-x64 构件）**：`net8.0-windows10.0.19041.0` 目标框架使用 `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.56
  （含 `Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`），它的 `.nuspec` 只写了 `licenseUrl=https://aka.ms/WinSDKLicenseURL`，
  指向的是「Microsoft Windows SDK for Windows 10」软件许可条款（Microsoft 专有许可，**不是开源许可证**）。
  我读过这份条款里有「Distributable Code（可分发代码）」条款，但**没有核实这些投影程序集是否属于其中允许再分发的范围**，
  也没有请法务确认——这是一个**未决项**，影响 win-x64 构件的再分发；发布到公开 Release 之前应先确认。
* **Tmds.DBus.Protocol 0.21.2 的已知漏洞**：Avalonia.FreeDesktop 11.3.13 传递引入的 `Tmds.DBus.Protocol` 是 0.21.2，
  而 GitHub 安全公告 GHSA-xrw6-gwf8-vvr9（CVE-2026-39959，严重度 high，2026-04-08）写明 `Tmds.DBus.Protocol < 0.21.3` 受影响
  （0.21.3、0.92.0 起修复）。已核对公告内容，并确认 `dotnet publish -r linux-x64` 的发布输出里带的 `Tmds.DBus.Protocol.dll`（200 KB，2024-11-22 的文件时间）来自 0.21.2 这个旧版本包（还原图里只有 0.21.2）。
  处理方式（升级为直接引用 0.95.1 等）属于 `src/`，不在本次范围，已在验收修复报告里单列。

### 1.5 原生库里的 libmicrohttpd（LGPL）说明

SkiaSharp 的 `THIRD-PARTY-NOTICES.txt` 列了 libmicrohttpd（LGPL-2.1 / eCos 双许可），那是 Skia 源码树里工具部分的依赖。
我用 `strings -a` 在 NuGet 缓存里的 `libSkiaSharp.so`（linux-x64）和 `libSkiaSharp.dll`（win-x64）里搜索 `MHD_` 与 `microhttpd`（不区分大小写），**都是 0 个命中**，
因此判断它没有被链接进这两个库；这是基于字符串的判断，不是对二进制做完整依赖分析。

## 2. 仅构建 / 测试用、不随构件分发的包

| 包 | 版本 | 许可证（`.nuspec`） | 用途 |
|---|---|---|---|
| xunit（及 xunit.core / assert / extensibility.core / extensibility.execution） | 2.9.3 | Apache-2.0 | 单元测试 |
| xunit.analyzers | 1.18.0 | Apache-2.0 | 测试分析器 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | 测试运行器 |
| xunit.abstractions | 2.0.3 | `.nuspec` 无 SPDX，`licenseUrl` 指向 xunit 仓库 license.txt（**未打开核对**） | 测试 |
| Microsoft.NET.Test.Sdk、Microsoft.TestPlatform.*、Microsoft.CodeCoverage | 17.11.1 | MIT | 测试宿主 |
| Newtonsoft.Json | 13.0.1 | MIT | 测试宿主依赖 |
| System.Reflection.Metadata | 1.6.0 | `.nuspec` 无 SPDX，`licenseUrl` 指向 dotnet/corefx（**未打开核对**） | 测试宿主依赖 |

另：`web/`（浏览器版）与 `shared-vectors/`、`tools/ParseCli` 的依赖不在本表范围；`web/` 的 npm 依赖**没有在本次核实**。

## 3. 协议与规范的参考来源

以下全部引自 [NOTES.md](NOTES.md) 已记载的来源，没有新增本次未读过的来源。所有解析器均为独立实现；
**NOTES.md 记载的「布局来源」是本项目开发时实际读过的页面**，每一项实现仍是「实验性」。

* **Bluetooth SIG 规范**（权威来源）：《Weight Scale Service v1.0.1》、《Body Composition Service v1.0.1》
  （`https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/WSS_v1.0.1/out/en/index-en.html`、
  `…/BCS_v1.0.1/out/en/index-en.html`）；GATT Specification Supplement 的 Date Time 特征格式。
  发生冲突时以 SIG 规范为准（NOTES Q18）。
* **gatt-xml 社区镜像**：<https://github.com/oesmith/gatt-xml>（SIG XML 的社区镜像，只用于交叉核对，不是权威来源；
  用到 `weight_scale_feature.xml`、`body_composition_feature.xml`、`date_time.xml` 与枚举表）。
* **小米体重秤（Mi Scale / Mi Body Composition Scale 2）**——只读**文字性**公开资料（NOTES.md「厂商协议来源（时尚1）」）：
  openScale **wiki** 页面「Xiaomi Bluetooth Mi Scale」（逆向笔记文字，没有读 openScale 源码）；
  freenetwork/ble-in-xiaomi 的 `doc/devices/huami.health.scale2.md`；cezaris13/MiCompositionScale2 的 README；
  `smartscale_reader`（Dart）的 API 文档页（只读 README/文档里的字节与控制位表，没有读其代码）；
  passcod 的 0x181D 广播示例 gist（其单位位与 wiki 冲突，**未采用**）。
* **Chipsea / OKOK 风格广播帧**——NOTES.md 的这一节在提交 `c1874a8` 里记录了来源：
  厂商文档「Protocol of OKOK APP」PDF（`https://github.com/oliexdev/openScale/files/5224454/OKOK.Protocol.pdf`，
  是厂商文档，只是文件挂在 openScale issue 的附件里）；openScale issue #496 的**文字描述**（只读 issue 正文与评论，不看代码）；
  一个 Gadgetbridge issue 的文字讨论（只用于交叉印证，其中的 Lua 片段看到过**未采用**，URL 没有记录）。
  > 注意：这一节与「Renpho 跳过」一节在后来的提交 `03934ef`（Q1–Q21 的 NOTES 改动）里从 NOTES.md **丢失了**；
  > 上述内容以 `git show c1874a8:NOTES.md` 为准，`shared-vectors/vendor/VENDOR.md` 与 `ChipseaOkokParser.cs` 的头注释目前只指向 NOTES.md 的「厂商协议来源（时尚1）」一节，而该节在 HEAD 已没有 Chipsea 的内容。
  > 修复 NOTES.md（恢复这两节）不在本次改动范围内，已写入验收修复报告。
* **Renpho**：没有找到可合规使用的来源，**没有实现**（见上，同在 `c1874a8`）。
* **Linux / Windows 适配器**：BlueZ 官方文档（`bluez/doc` 的 `org.bluez.Adapter` / `Device` / `GattCharacteristic`）、Tmds.DBus 文档、
  微软官方文档（`learn.microsoft.com` 的 Windows.Devices.Bluetooth 等 API 页）与 Bluetooth Core Specification；清单见 NOTES.md
  「Linux 适配器与 D-Bus 库评估」与「Windows 适配器」两节的「来源」。

## 4. 与 GPL 项目的关系声明与净室记录

**声明：`ble-scale-sync`（GPL-3.0）只作为事实性线索（「有哪些设备/协议存在」），本项目没有复制其代码、文件结构或测试。**
同样，openScale（GPL-3.0）只引用了它的 wiki 文字页与 issue 文字描述，没有阅读或复制它的源码；`smartscale_reader` 只读了 README 的位表。

净室过程记录（只写 NOTES.md 已有的事实，没有补充）：

* **规则**（NOTES.md「厂商协议来源（时尚1）」）：只使用 Bluetooth SIG 规范、厂商公开文档、以及对字节布局的**文字性**公开描述
  （博客、wiki、论坛、协议笔记）；不阅读、不复制、不改写 GPL 项目（ble-scale-sync、openScale 等）的源码或结构；代码结构为本项目独立设计。
* **读了哪些页面**：见第 3 节的列表，对应 NOTES.md 的「使用的页面与取到的事实」表（Xiaomi 一节仍在 NOTES.md；Chipsea/OKOK 一节见提交 `c1874a8`）。
  NOTES.md 的记载日期：「资料整理于 2026-10-26（Asia/Shanghai）」（Linux 适配器一节的 Tmds.DBus 查询也记为 2026-10-26）；**NOTES.md 没有记录具体读者姓名，也没有每个页面各自的阅读时间**，所以这里不写姓名和逐页时间（git 提交作者均为 `composehk`，仅此而已）。
* **没读哪些代码**：没有打开 ble-scale-sync 的任何源码（检索摘要里偶然出现过它的 issue/PR/提交文本，**未打开、未采用**；
  其中控制位的描述还与公开文字资料互相矛盾）；没有读 openScale 源码（只读 wiki 与 issue 文字）；没有读 `smartscale_reader` 的代码；
  Renpho 检索时出现的 openScale 提交说明与 ble-scale-sync 文本**没有采用**。
* **偶然接触的 MIT 代码片段**：检索摘要里出现过 custom-components/ble_monitor（MIT）的解析代码片段，只用来核对「控制位在第几字节第几位」这类事实，
  没有借用其结构或代码；一条 Gadgetbridge 的 Lua 片段看到过但未采用。
* **向量**：`shared-vectors/vendor/` 的向量是按记录的布局**手工构造**的，期望值由独立 Python `Decimal` 脚本（`shared-vectors/vendor/generators/`）计算，
  不是从真机抓取，也不是来自其他项目的测试数据；通过向量**不代表任何真机验证**。
* **独立复核**：测试1 的验收报告（阶段一，§3.3；在本仓库之外）用标识符、8-token 滑窗、十六进制常量、注释行的重合度对 openScale 与 ble-scale-sync 做过相似度检查，
  结论是只有协议事实常量（如 `0xCA 0x11`、SIG 标准 UUID）与通用词重合，8-token 滑窗重合为 0；这是事实陈述，不是法律结论。
  仍建议由未接触过 GPL 代码的人再复核一次字节布局表。
* Windows 适配器（NOTES.md「Windows 适配器」）：NOTES.md 记载为「只按微软官方文档与 Bluetooth SIG 规范编写」，来源清单见该节「来源」，没有列出其他项目。
