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
* 截断：Flags 声明存在而放不下的字段 → `Truncated` 错误；连必选字段都不全 → `TooShort`（BCS 例外：Flags 已读到且 bit12 置位时优先 `MultiPacketNotSupported`，见「解析策略与已知分歧」Q21）；绝不抛异常。
* Body Composition 的 Multiple Packet（bit12）：只识别，返回 `MultiPacketNotSupported`，**阶段 1 不拼包**。
* 数值全部用 `decimal`（lb→kg 乘 0.45359237，in→m 乘 0.0254，均为定义值，二位小数乘八位小数 = 十位小数，精确无舍入）。

## 解析策略与已知分歧

本节是「测试1」交叉比对（它独立写的参考解析器对我们的 235 条 shared-vectors：221 一致、14 条策略差异、0 条无法解释）之后，
对它提出的 Q1–Q21 逐条给出的**明确决定**。原则：有规范依据或明确理由的就坚持并写明，不为迎合测试而改；
真有缺陷的才改。**本轮没有改动任何解析行为，shared-vectors 一字未动**（Web 端 JS 解析器依赖同一份向量，无需跟进）。
需要落地的数据管线策略（Q13/Q14/Q16）已实现为 `Core/Models/MeasurementPolicy.cs`，并由
`MeasurementPolicyTests` / `ParsePolicyTests` 钉住。

> 所有标准协议解析均为「实验性（仅按规范实现，未真机验证）」；下列「规范依据」均为按 SIG 规范实现、**待与规范原文逐字段复核**。

### 专题速查

| 主题 | 决定 |
|---|---|
| Imperial 换算与舍入 | lb→kg 乘 0.45359237、in→m 乘 0.0254（定义值），`decimal` 精确，**解析、存储、导出都不舍入**（例：150.09 lb = 68.0796788133 kg）。只有**显示**才舍入：kg 3 位、lb 2 位，`MidpointRounding.AwayFromZero`（不是银行家舍入）。显示值绝不回写存储。JSON 导出的数值是原样十进制数字令牌（不经 double 写出）。 |
| Flags 位含义 | WSS（1 字节）：bit0 单位、bit1 时间戳、bit2 用户 ID、bit3 BMI+身高。BCS（2 字节）：bit0 单位、1 时间戳、2 用户 ID、3 基础代谢（kJ）、4 肌肉率、5 肌肉量、6 去脂体重、7 软瘦体重、8 身体水分量、9 阻抗（0.1 Ω）、10 体重、11 身高、12 多包。**只信 Flags**，不按设备名猜单位。 |
| 保留位 | 置位只给 `ReservedBitsSet` 警告并照常解析；**不改变字段顺序/长度**（保留位不携带字段）。 |
| 非法时间戳 | 7 字节 Date Time（年 uint16 + 月日时分秒）。年不在 1582–9999、月/日/时/分/秒越界、日历上不存在（2 月 30 日）、**或任一年/月/日为 0** → 时间戳为 null + `InvalidTimestamp`，其余字段照常解析。不区分「全 0（未知）」和「越界」：二者对使用者的结果相同（没有可信设备时间 → 回退接收时间），区别只在设备是否违规，不值得多一个状态。 |
| BCM 多包 | 阶段 1 不拼包：Flags bit12 置位 → `MultiPacketNotSupported`。 |
| 体重阈值 | 恰好 0 不自动入库；< 2.000 kg 或 > 200.000 kg 入库但标「异常」；**2.000 与 200.000 恰好正常**。阈值是常量，阶段 1 不可配置。 |
| 去重键 | 设备 + 来源特征 + 用户 + 设备时间戳（到秒）+ 全部测量值；**没有设备时间戳就不去重**。 |
| userId 0xFF 与缺失 | 都是 `null`（「未指定用户」），见 Q20。0 是合法用户号，不是未知。 |

### Q1–Q21 逐条决定

| # | 议题 | 决定 | 理由 |
|---|---|---|---|
| Q1 | Imperial→kg 常数、舍入、存储 | **采纳** | 现状即如此：常数取国际磅定义值，decimal 精确；存储/导出用精确值，仅显示舍入（kg 3 / lb 2 位，AwayFromZero）。Web 端同样用精确十进制（共享向量保证）。`Imperial_weight_is_stored_and_exported_exactly…` 钉住。 |
| Q2 | Flags bit0：0=SI，1=Imperial | **采纳** | 规范就是这么定义；只信 Flags，不按设备名/型号推断单位。 |
| Q3 | 保留位 | **采纳**（警告+接受） | 保留位对未来版本可能有含义，拒绝会让新固件设备无法使用；警告足以暴露。不改变解析顺序（`Q3_reserved_bits…`）。 |
| Q4 | 非法时间戳；0 与非法是否区分 | **采纳现状，不新增「未知」状态** | 见专题：全 0 与越界一律 null + `InvalidTimestamp`。WSS 的时间戳字段要求合法日期，全 0 本就不合规；区分只增加状态而不改变行为。「0 = unknown」的措辞待与 GSS 原文复核。 |
| Q5 | 多余字节 | **采纳** | 只给 `TrailingBytes` 警告，绝不当作下一个字段（避免把垃圾读成 userId/BMI）。 |
| Q6 | 多包续包是否还带 Body Fat（不一致处理） | **阶段 1 不实现；将来的规则先写下** | 将来拼包时：续包与首包的 Body Fat 不一致 → 丢弃整组并报告，不择一。 |
| Q7 | 多包配对/顺序/超时 | **不采纳（阶段 1 不拼包）** | 现状：bit12 置位一律 `MultiPacketNotSupported`。拼包是阶段 2/3 议题，届时需要通过测试1的 `VEC-BCM-SEQ`；这里不假装支持。 |
| Q8 | 测量不成功 + 其他已声明字段 | **采纳，且声明为有意** | 只读时间戳与用户 ID，其余声明字段忽略并给 `FieldsIgnoredMeasurementUnsuccessful`，**即使字节不存在**（`08 FF FF`）也接受。理由：0xFFFF 已完整表达「测量失败」，严格判错只会丢掉这条有用信号；不产生记录，所以没有数据风险。 |
| Q9 | 可选字段 0xFFFF 是未知还是字面值 | **采纳「未知」** | 14 条差异里 11 条是这一类。规范没有为可选字段定义 0xFFFF；字面值意味着 BMI 6553.5、身高 65.535 m、基础代谢 65535 kJ、阻抗 6553.5 Ω、肌肉率 6553.5 %、质量 327.675 kg，均物理不可能；0xFFFF 是业界通用的「无值」占位。结果 null + `OptionalFieldMarkedUnknown`。代价：原始值在解析结果里不保留（调用方有原始字节；ParseCli 可复现）。 |
| Q10 | Feature 分辨率码 0 / 保留值 | **采纳** | 码 0 → null（不警告）；保留码 → null + `ReservedResolutionCode`。Feature 的分辨率码只是显示精度参考，**不改变** Measurement 里 1 LSB 的含义（固定 0.005 kg / 0.01 lb，见「实现取舍」）。 |
| Q11 | 基础代谢单位 | **采纳 kJ，不提供 kcal** | 字段规范单位是 kJ；UI 一律标 kJ。kcal 换算（÷4.184）与取整规则会引入第二套数值，阶段 1 不做。 |
| Q12 | BMI 与身高 | **采纳** | 成对出现；UI **绝不**用体重/身高重算后覆盖设备给的 BMI（设备可能按自己的口径算，且身高可能是设备里存的用户资料）。 |
| Q13 | 去重键 | **采纳（有调整）** | 键 = 设备+来源+用户+设备时间戳（秒）+全部测量值，只有设备给了有效时间戳才有键；同时间戳**不同数值保留两条**（不静默覆盖）；**无时间戳不去重**，因为两次相同读数是两次真实称重，窗口去重会误吞。跨连接、跨重启有效（`MeasurementIngestor` 启动时从仓库建键）。 |
| Q14 | 设备时间戳无时区、时钟漂移/复位 | **采纳** | 设备时间按「设备本地时间」原样存储，**不做时区转换**；接收时间另存（带偏移）；无设备时间或时间非法时，列表用接收时间排序/显示并标注来源。设备时间比接收时的本地墙钟晚 > 24 h 或早 > 366 天 → 标「设备时钟可疑」（如电池耗尽后复位到 2000 年），仍然入库，不改写时间。 |
| Q15 | Notify 与 Indicate | **采纳（阶段 2 落实）** | 0x2A9D/0x2A9C 规范是 Indicate；适配器接口已有「订阅 notify/indicate」，Windows/Linux 适配器应按特征属性选择（两者都支持时按规范用 Indicate）。阶段 1 只有 Fake。 |
| Q16 | 体重阈值 | **采纳（有调整）** | 0 不自动入库（不含信息；实时区仍提示）。< 2 kg / > 200 kg **入库但标异常**，不丢弃：婴儿秤、宠物秤、超大量程秤都可能合法，丢数据比标注更糟；2.000 / 200.000 恰好正常。不可配置（阶段 1）。 |
| Q17 | 合并同一次测量的 WM 与 BCM | **不采纳（阶段 1）** | WSS 与 BCS 产生**各自独立的记录**，不合并。没有可靠连接键（用户 ID、时间戳都是可选字段）；错误合并比不合并更糟。将来可考虑：同设备、同用户、同设备时间戳都存在且体重差 ≤ 0.1 kg 才合并。 |
| Q18 | gatt-xml 与 SIG HTML 冲突 | **采纳：SIG 规范为准** | gatt-xml 只是社区镜像，只用于交叉核对（NOTES「协议信息来源」已这样标注）。 |
| Q19 | 节流/历史上限 | **不采纳（阶段 1）** | 不设历史条数上限：静默删用户数据不可接受，SQLite 与 DataGrid 虚拟化可以承受；实时区只显示最新一条，真实秤的上报频率远低于 UI 刷新，不加节流。风险：恶意/故障设备狂发包可能刷屏——阶段 2 接真实适配器后再按实测决定。 |
| Q20 | userId 0xFF 与缺失 | **采纳现状（都为 null），不加 `userIdPresent`** | 都表示「没有可归属的用户」，历史里归入同一「未指定用户」组；加字段要改 shared-vectors 的 expect 形状，Web 端同步改，收益只是诊断。需要时原始字节仍可复现（ParseCli）。`Q20_user_id_0xFF…` 钉住；0 与 254 是合法用户号。 |
| Q21 | 错误码优先级 | **采纳并写明** | ① BCS 数据不足 2 字节（读不到 Flags）→ `TooShort`；② Flags 已读到且 bit12 置位 → `MultiPacketNotSupported`（即使只有 `00 10` 两个字节也如此：Flags 已能认出这是多包，比「数据太短」更有用）；③ 不足最小长度（WSS 3 / BCS 4）→ `TooShort`；④ Flags 声明的字段放不下 → `Truncated`。 |

### 与测试1 工具对接

* ParseCli 输出采用 shared-vectors 的形状（`reject/errorCode/warnings/expect`，十进制为字符串），而测试1 的 `sb_run_vectors.py`
  期望 `status/weight_raw/...` 的另一套规范化键，调用形式也是 `{kind} {hex}`。需要由测试1 一侧加一层薄适配：
  `wm`→`--service 181D --char 2A9D`、`wsf`→`181D/2A9E`、`bcm`→`181B/2A9C`、`bcf`→`181B/2A9B`；错误码/警告名直接用我们的 `ParseErrorCode` / `ParseWarningCode`。
* 他们的 340 条向量可批量走 `--stdin`（行格式 `181D/2A9D 00 59 36`），一次启动处理全部，避免每条一次 `dotnet run` 的开销。

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
