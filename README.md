# ScaleBridge

跨平台桌面蓝牙体重秤接收器：**不需要手机 App**，在 Windows 和 Linux 上直接发现体重秤、连接、实时接收体重并在本机显示，可导出 CSV / JSON。

> **状态：阶段 1（开发中）。** 目前只有 Core 协议解析、演示用的 Fake 适配器、界面、导出与 CI/打包；
> **还没有真实蓝牙适配器**（Windows / Linux 适配器属于阶段 2），所以现在**不能连接真实体重秤**。

## 诚实声明

* **没有任何真机验证。** 本项目没有在任何真实体重秤或真实蓝牙硬件上测试过。
* 标准 Weight Scale（0x181D）与 Body Composition（0x181B）的解析目前只有**字节向量单元测试**，
  一律标注为 **「实验性（仅按规范实现，未真机验证）」**，README 与界面里都是如此。
* 小米、Renpho、芯海（OKOK）等私有协议属于后续阶段，**当前不支持**。
* 界面里 Fake 适配器产生的是 **「演示数据」**，不是真实体重。

## 纯本地、无遥测

* 程序不发起任何网络请求，没有遥测、统计或更新检查。
* 历史记录和设置保存在本机应用数据目录的 SQLite 文件里；详见 [PRIVACY.md](PRIVACY.md)（随首个 UI 提交加入）。

## 技术栈与结构

.NET 8 + Avalonia（MVVM）。

```
src/ScaleBridge.Core          纯 C#（net8.0）：协议解析、数据模型、导出、IBleAdapter 抽象；不依赖任何蓝牙库
src/ScaleBridge.Ble.Fake      演示数据适配器（脚本回放，供界面演示与无硬件的 CI 端到端测试）
src/ScaleBridge.App           Avalonia 桌面界面
tests/ScaleBridge.Core.Tests  xUnit
shared-vectors/               与语言无关的 GATT 字节向量 JSON（见其 README）
```

协议信息来源与实现取舍见 [NOTES.md](NOTES.md)。

## 构建与测试

```bash
dotnet build
dotnet test
```

需要 .NET 8 SDK（`global.json` 固定 8.0.100，允许 latestFeature 滚动）。

## 许可证与致谢

MIT，见 [LICENSE](LICENSE)。

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
