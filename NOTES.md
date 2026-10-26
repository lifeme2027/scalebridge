# ScaleBridge 开发笔记

> 状态：阶段 1 进行中（Core + Fake 适配器 + Avalonia UI + 导出 + CI + 打包）。本文件随功能提交持续更新。

## 诚实声明（硬性规则）

* **没有在真机上验证过的协议与设备，一律不写「支持」**，统一标成
  **「实验性（仅按规范实现，未真机验证）」**。
* 这包括标准 Weight Scale（0x181D）与 Body Composition（0x181B）：目前只有**字节向量单元测试**，
  没有任何真实体重秤验证过。
* Fake 适配器产生的是**演示数据**，UI 上必须明显标注，不得被当成真实体重。
* 本项目没有在任何真实蓝牙硬件上运行过；开发服务器没有蓝牙硬件。

## 协议信息来源

所有解析器均为**独立实现**（自己写的 C#），只参考公开的 Bluetooth SIG 规范与 UUID/字节布局这类**事实性信息**。
参考项目 [ble-scale-sync](https://github.com/KristianP26/ble-scale-sync) 是 GPL-3.0：
**本项目没有复制、改写它的任何代码、文件结构或测试**；仅把它当作「有哪些设备/协议」的事实性线索。
本项目采用 MIT 许可证。

| 特征 | UUID | 布局来源（本次开发实际读过的页面） | 复核状态 |
|---|---|---|---|
| Weight Scale Feature | 0x2A9E | SIG《Weight Scale Service v1.0.1》§3.1（Weight Scale Feature）；字段/位定义与 oesmith/gatt-xml 的 `org.bluetooth.characteristic.weight_scale_feature.xml` 交叉核对 | 按 SIG 规范实现；**待与规范原文逐字段复核** |
| Weight Measurement | 0x2A9D | SIG《Weight Scale Service v1.0.1》§3.2（Weight Measurement） | 按 SIG 规范实现；**待与规范原文逐字段复核**；未真机验证 |
| Body Composition Feature | 0x2A9B | SIG《Body Composition Service v1.0.1》§3.1；与 gatt-xml 的 `body_composition_feature.xml` 交叉核对 | 按 SIG 规范实现；**待与规范原文逐字段复核** |
| Body Composition Measurement | 0x2A9C | SIG《Body Composition Service v1.0.1》§3.2 | 按 SIG 规范实现；**待与规范原文逐字段复核**；未真机验证 |
| Date Time（时间戳 7 字节） | 0x2A08 格式 | SIG GATT Specification Supplement 的 Date Time 特征格式；gatt-xml `date_time.xml` | 按 SIG 规范实现；待复核 |

规范页面：

* `https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/WSS_v1.0.1/out/en/index-en.html`
* `https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BCS_v1.0.1/out/en/index-en.html`
* `https://github.com/oesmith/gatt-xml`（社区镜像的 SIG XML；仅作交叉核对，不是权威来源）

### 字节布局摘要（小端）

**Weight Measurement 0x2A9D**：`Flags(1) | Weight(2) | [Time Stamp(7)] | [User ID(1)] | [BMI(2) Height(2)]`

* Flags：bit0 单位（0=SI kg/m，1=Imperial lb/in），bit1 时间戳，bit2 用户 ID，bit3 BMI+身高（一对，同时出现），bit4–7 保留。
* 分辨率：体重 SI 0.005 kg、Imperial 0.01 lb；身高 SI 0.001 m、Imperial 0.1 in；BMI 0.1。
* Weight = 0xFFFF 表示 Measurement Unsuccessful；此时只保留时间戳与用户 ID，其余可选字段被忽略。
* User ID = 0xFF 表示未知用户。

**Body Composition Measurement 0x2A9C**：
`Flags(2) | Body Fat %(2) | [Time Stamp(7)] | [User ID(1)] | [Basal Metabolism(2, kJ)] | [Muscle %(2)] | [Muscle Mass(2)] | [Fat Free Mass(2)] | [Soft Lean Mass(2)] | [Body Water Mass(2)] | [Impedance(2, 0.1 Ω)] | [Weight(2)] | [Height(2)]`

* Flags：bit0 单位，bit1 时间戳，bit2 用户 ID，bit3 基础代谢，bit4 肌肉率，bit5 肌肉量，bit6 去脂体重，bit7 软瘦体重，
  bit8 身体水分量，bit9 阻抗，bit10 体重，bit11 身高，bit12 **Multiple Packet Measurement**，bit13–15 保留。
* Body Fat % 分辨率 0.1 %；质量字段 SI 0.005 kg / Imperial 0.01 lb；Body Fat % = 0xFFFF 表示 Measurement Unsuccessful。

**Feature 特征**（uint32）：

* Weight Scale Feature：bit0 时间戳，bit1 多用户，bit2 BMI，bit3–6 体重分辨率码，bit7–9 身高分辨率码。
* Body Composition Feature：bit0–10 依次为 时间戳、多用户、基础代谢、肌肉率、肌肉量、去脂体重、软瘦体重、身体水分量、阻抗、体重、身高；
  bit11–14 质量分辨率码，bit15–17 身高分辨率码。
* 分辨率码表（质量 1–7：0.5/0.2/0.1/0.05/0.02/0.01/0.005 kg 对应 1/0.5/0.2/0.1/0.05/0.02/0.01 lb；身高 1–3：0.01/0.005/0.001 m 对应 1/0.5/0.1 in）
  取自 gatt-xml 的枚举表。

### 实现取舍（规范没有明确规定、由本项目自行决定的地方）

* **质量字段的分辨率不由 Feature 决定。** 任务说明里写的是「各质量字段分辨率按 Feature 里的位决定」，
  但 SIG 规范文字说明 Feature 里的分辨率位只是告诉客户端设备的大致精度，**不改变**测量字段 1 LSB 的含义
  （0.005 kg / 0.01 lb 是固定刻度）。因此本项目固定使用 0.005 kg / 0.01 lb，Feature 的分辨率码只解码出来作为显示精度参考。
  这是与任务字面要求的一处偏差，已在汇报中说明。
* 可选字段（BMI、身高、Body Composition 的各可选字段）取值 0xFFFF：规范没有为这些字段定义此特殊值，
  本项目保守地当作「未知」（null）并给出 `OptionalFieldMarkedUnknown` 警告。
* 保留位被置位只给 `ReservedBitsSet` 警告；数据比 Flags 描述的长，只给 `TrailingBytes` 警告；
  非法时间戳（年 1582–9999、月日时分秒越界、日历上不存在的日期，如 2 月 30 日）得到 null + `InvalidTimestamp` 警告。
* 截断：Flags 声明存在而放不下的字段 → `Truncated` 错误；连必选字段都不全 → `TooShort`；绝不抛异常。
* Body Composition 的 Multiple Packet（bit12）：只识别，返回 `MultiPacketNotSupported`，**阶段 1 不拼包**。
* 数值全部用 `decimal`（lb→kg 乘 0.45359237，in→m 乘 0.0254，均为定义值，二位小数乘八位小数 = 十位小数，精确无舍入）。

## 路线图（仅规划，阶段 1 不实现）

* **阶段 2 — Windows**：WinRT 蓝牙适配器，目标框架 `net8.0-windows10.0.19041`。
* **阶段 2 — Linux**：基于 D-Bus 的 BlueZ 适配器（评估 Tmds.DBus 等库，见下）。
* **阶段 3**：小米 / Renpho / 芯海（Chipsea/OKOK）等私有协议的解析器，一律先标「实验性」。

### BlueZ / Tmds.DBus 初步评估（未完成，供阶段 2 参考）

* Tmds.DBus（`https://github.com/tmds/Tmds.DBus`）：经 GitHub API 查询（2026-10-26）——许可证 MIT，未归档，
  最近一次推送 2026-09-04，约 367 star。是否与 BlueZ 5.x 的 GATT 接口配合良好、NuGet 包的发布节奏与目标框架：**未核实**。
* 其它 .NET BlueZ 封装库（如 `Linux.Bluetooth` 等）的维护状态与许可证：**未核实**。
* BlueZ 版本差异、权限（bluetoothd 与用户会话总线）、发现过滤行为：**未核实**，需在真机/真实 BlueZ 上验证。
