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

## 厂商协议来源（时尚1）

> **实验性（仅按规范/公开资料实现，未真机验证）**——本节所有厂商协议均如此。
> 解析器位于 `src/ScaleBridge.Core/Protocols/`，向量位于 `shared-vectors/vendor/`（格式扩展见 `shared-vectors/vendor/VENDOR.md`）。

**净室规则**：只使用 Bluetooth SIG 规范、厂商公开文档、以及对字节布局的**文字性**公开描述（博客、wiki、论坛、协议笔记）。
不阅读、不复制、不改写 GPL 项目（ble-scale-sync、openScale 等）的源码或结构；代码结构为本项目独立设计。
向量是按下面记录的布局**手工构造**的（用独立 Python `Decimal` 脚本算期望值），**不是**从真机抓取。
布局里无法从公开文字描述确认的字段，下面明确标「未确认」，代码里跳过或只给原始值。

**检索卫生说明（诚实记录）**：网络检索的结果摘要里偶然出现过 ble-scale-sync 的 issue/PR 文本，以及 custom-components/ble_monitor（MIT）的解析代码片段。
前者未打开、未采用（其中控制位的描述还与下面的公开文字资料互相矛盾）；后者只用来核对「控制位在第几字节第几位」这类事实，没有借用其结构或代码。
openScale 只引用了它的 **wiki 文字页**（逆向笔记），没有看它的源码。`smartscale_reader` 只读了它 README 的字节/控制位表（该包本身是 GPL-3.0，没有看其代码）。
检索中还看到过 openScale 的 issue / 提交说明文字。其中 issue #496 的**文字描述**（含厂商 PDF 附件）用于 Chipsea/OKOK；Renpho 检索时出现的 openScale 提交说明与 ble-scale-sync 文本**没有采用**。一条 Gadgetbridge 的 Lua 片段看到过但未采用。

### 小米体重秤（Mi Scale / Mi Body Composition Scale 2）——实验性

实现：`XiaomiScaleParser`（`ParseMiScaleFrame` / `ParseBodyCompositionScale2Frame` / `ParseServiceData`）。输入是广播 Service Data（或 notify）的**负载**（不含 2 字节 UUID 前缀）。

使用的页面与取到的事实：

| 来源 | 取到的事实 |
|---|---|
| openScale wiki「Xiaomi Bluetooth Mi Scale」（文字逆向笔记）`https://github.com/oliexdev/openScale/wiki/Xiaomi-Bluetooth-Mi-Scale` | 一代 Mi Scale 10 字节帧：byte0 状态（bit0=磅，bit4=斤，bit5=稳定，bit7=已离秤，bit1–3/6 未知）；byte1–2 体重小端；byte3–4 年（小端）；byte5–9 月日时分秒；磅/斤除以 100、否则除以 200；日期仅在「稳定且未离秤」时有效；体重通知需要打开 notify（文字里隐含是 Weight 服务 0x181D 的体重特征，**未明确写 0x2A9D**）。 |
| freenetwork/ble-in-xiaomi 文档 `doc/devices/huami.health.scale2.md`（`https://github.com/freenetwork/ble-in-xiaomi/blob/master/doc/devices/huami.health.scale2.md`） | 体脂秤 2：广播 Service Data（adType 0x16）UUID 后 13 字节载荷：控制字节 2 个、年（小端）、月日时分秒、阻抗（小端）、体重（小端）；千克 ×200（即 raw/200）、磅与斤 ×100。控制位表给了「磅 / 空载 / 斤 / 稳定 / 有阻抗」的位号，但**位号编号方向有歧义**（按 16 位字的高位在前编号），需与下一行交叉核对。 |
| cezaris13/MiCompositionScale2 README `https://github.com/cezaris13/MiCompositionScale2` | 同一份 13 字节布局与控制位表（相同的位号编号），确认体重 raw/200 为 kg、raw/100 为磅/斤。 |
| smartscale_reader（Dart）API 文档 `https://pub.dev/documentation/smartscale_reader/latest/` | 明确按「第几字节第几位」给出：byte0 bit0 = 磅；byte1 bit6 = 斤；byte1 bit1 = 有阻抗；byte1 bit5 = 稳定；byte1 bit7 = 已离秤；13 字节包结构。用它消除了上面位号编号的歧义（与高位在前编号的换算一致）。 |
| Mi Scale 2 / 0x181D 广播示例 gist `https://gist.github.com/passcod/2132f8d1ca33232108f00e86f6147e47` | 10 字节 0x181D 广播帧（第二代 Mi Smart Scale 的广播）与一代同布局；时间戳字段布局相同。它给的单位位（bit1=kg，bit2=磅）与 wiki（bit0=磅）**冲突**，本项目**不采用** gist 的单位位，只采用 wiki 的位（bit0=磅、bit4=斤、都没有=kg）。 |
| Bluetooth SIG GATT Specification Supplement「Date Time」 | 时间戳 7 字节格式，与现有 `GattDateTime` 相同（年小端 + 月日时分秒）。 |

采用的字节布局（负载，小端）：

* **Frame A（0x181D，10 字节）**：`status(1) | weight(2) | year(2) month day hour minute second`。
  kg = raw/200（0.005 kg/LSB）；磅 = raw/100 lb；斤 = raw/100 斤。
* **Frame B（0x181B，13 字节）**：`ctrl0(1) | ctrl1(1) | year(2) month day hour minute second | impedance(2) | weight(2)`。
  磅 = ctrl0 bit0；斤 = ctrl1 bit6；有阻抗 = ctrl1 bit1；稳定 = ctrl1 bit5；已离秤（资料里也叫「空载」）= ctrl1 bit7。
* 换算：磅→kg 乘 0.45359237（定义值）；1 斤 = 500 g，所以斤→kg 乘 0.5。全部 `decimal`。
* 没有校验和（公开资料里没有）；只做长度检查：短于固定长度 → `TooShort`；更长 → 解析前 N 字节并给 `TrailingBytes` 警告。
* 磅位与斤位同时置位 → 无法判定单位 → `AmbiguousUnit` 错误（资料没说同时置位意味着什么）。

**未确认 / 刻意不做**：

* **阻抗单位与量纲**：公开文字资料只说「阻抗（小端 uint16）」，没有确认单位或比例，所以只暴露 `ImpedanceRaw`（原始整数），不映射成欧姆；也不根据阻抗计算体脂等（公式来自逆向实现，不在本次范围）。
* byte0 的 bit1–3/6（一代）、ctrl0 bit1、ctrl1 bit2 等「观测恒为 1 / 未知」位：忽略，不报警告。
* 一代帧「体重通知」具体走哪个特征（文字里隐含 0x2A9D，未明确）；向量里那条 notification 向量只代表「同一 10 字节负载」。
* 历史记录特征 `0x2A2F`（自定义，多条记录）的分帧与确认流程：**未实现**。
* 日期在「未稳定」时可能无意义：解析器只要字节构成合法日期就返回，调用方应只信 `IsFinal` 的帧。
* 不同固件对 `0x181D` / `0x181B` 的使用以及型号（XMTZC01HM / 04HM / 05HM）与帧的对应关系：公开资料说法不一，**未核实**；解析器只按服务 UUID + 长度分发。

### Renpho——**跳过（未实现）**

没有找到可以合规使用的来源：Renpho 没有公开的厂商协议文档，也没有 Bluetooth SIG 规范覆盖其私有帧；
网络上能找到的字节布局只出现在 GPL 项目的源码/提交说明/文档里（ble-scale-sync、openScale 等），或来自对 App 的反编译，而且存在多个互不兼容的型号/固件变体。
按净室规则这些都不能用，也不能「凭记忆」补布局，所以**本次不提供 Renpho 解析器、向量或测试**。
检索卫生说明：检索结果摘要里出现了 openScale 的提交说明与 ble-scale-sync 的提交/文档文本；这些页面**没有打开、没有采用**。
补齐条件：出现厂商发布的协议文档，或非代码的、可交叉验证的文字性字节布局说明，再单独立项。

### Chipsea / OKOK 风格广播体重帧——实验性

实现：`ChipseaOkokParser`（`ParseV11Broadcast` / `ParseV20Broadcast` / `ParseManufacturerData`）。输入是 manufacturer-specific data，**从 0xCA 字节起**（Android 上即「公司 ID」两字节 `CA 11` / `CA 20` 起）。多字节字段大端。只实现广播里的体重帧。

使用的页面与取到的事实：

| 来源 | 取到的事实 |
|---|---|
| 厂商文档「Protocol of OKOK APP」PDF（v1.1 系列；文件本身来自 openScale issue 的附件，但它是厂商文档，不是代码）`https://github.com/oliexdev/openScale/files/5224454/OKOK.Protocol.pdf` | 帧结构 `0xCA｜版本｜数据域长度｜数据域｜校验和`；v1.1 广播 19 字节：锁定标志（idx3）、设备类型（idx4）、体重（idx5–6，大端）、产品 ID（idx7–10）、「消息体属性」（idx11，表 1）、保留；表 1：bit4–3 单位（00 kg / 01 斤 / 10 lb / 11 st:lb），bit2–1 小数位（00 一位 / 01 零位 / 10 两位），其余保留；示例 50 kg = 0x01F4、50 lb = 0x01F4 且属性 0x10；st:lb 示例 4:13 = 0x0482（高字节英石、低字节磅×10）；校验和为 XOR，文档称「从版本字节到数据域末尾」。 |
| openScale issue #496 的文字描述 `https://github.com/oliexdev/openScale/issues/496`（只读 issue 正文与评论里的文字，不看代码） | v2.0 广播以 `CA 20` 开头，15 字节；体重为 raw/10、阻抗原始值（一位用户的解码日志里阻抗显示为 raw/10 Ω，本项目只暴露原始整数）；末尾可带 6 字节设备地址；一份抓包里 v1.1 校验和与 XOR(idx0..17) 吻合；v2.0 帧里「最终」标志位与序号的位置；帧里不含单位。 |
| 一个 Gadgetbridge issue 的文字讨论（URL 未记录；其中的 Lua 片段看到过但**未采用**） | 只用来交叉印证「v2.0 帧没有单位字节」与最终标志的位置。 |

采用的字节布局：见上面 `ChipseaOkokParser` 类注释与 `shared-vectors/vendor/VENDOR.md`。要点：

* **v1.1（19 字节）**：见上表。锁定 == 1 才算稳定（实机可能一直为 0，见下）；st:lb 时 `OriginalWeight` 是**总磅数**（英石×14 + 磅），`RawUnit = StoneLb`；斤→kg ×0.5，磅→kg ×0.45359237。
* **v2.0（15 字节）**：`CA 20 0B | 未知(4) | 未知(1) | 最终标志(1) | 序号(1) | 体重×10(2) | 阻抗原始值(2) | XOR(idx1..13)`。**帧不含单位**：`assumedUnit` 缺省按 kg 并给 `UnitAssumed` 警告；调用方给 Kg/Lb/Jin 则无警告；`StoneLb` → `InvalidValue`。
* 校验和之后的字节（6 字节设备地址）静默忽略；时间戳为 `null`；v1.1 无阻抗，v2.0 的阻抗只给 `ImpedanceRaw`。
* 错误顺序：`TooShort` → `BadHeader`（魔数/版本/长度字节）→ `BadChecksum` → `InvalidValue`。分发入口：不足 2 字节 `TooShort`，非 0xCA 或未知版本 `BadHeader`。

**未确认 / 冲突 / 刻意不做**：

* **v1.1 校验和规则冲突**：厂商文档说 XOR(idx1..17)，一份公开抓包吻合 XOR(idx0..17)。无法裁决，所以两种都接受（向量里两种都有）。
* **文档内部不一致**：PDF 里「4:13 ST:LB」示例写的属性字节是 0x00，而按表 1 应为 0x18；本项目按表 1，向量用 0x18。
* 锁定字节：公开资料称实机即使重量稳定也可能一直为 0——所以 `Stabilized` 在这类设备上可能永远为 false，调用方需自行处理（例如用「读数不再变化」判定）。
* 一条检索摘要称属性字节 bit7 表示「稳定/最终」（示例 0x25）——与厂商文档表 1（bit7–5 保留）冲突，**未采用**。
* v2.0 的字节 3–7（可能含产品 ID / 设备属性）含义未知，忽略；序号不暴露；阻抗的单位未经确认（只有一位用户的 raw/10 Ω 说法），只给原始值。
* **没有实现**：未命名 Yoda0 秤上的 `C0` 变体（公开描述互相矛盾）、连接后 0x10–0x15 指令帧与 0x12/0x13 通知帧（体脂、历史、时间字节序/偏移无法从 PDF 文本确认）、任何体成分计算。
* v1.1 里「设备类型」不校验；st:lb 在 v2.0 中的编码未知，因此拒绝。
