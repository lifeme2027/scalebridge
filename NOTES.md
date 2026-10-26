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

## 阶段 1 桌面程序：结构与取舍

* **分层**：`Core`（无 UI、无蓝牙库）→ `Ble.Fake`（演示数据）/ `Data`（SQLite）→ `App`（Avalonia，MVVM，CommunityToolkit.Mvvm）。
  App 经 `IAdapterFactory`、`IDialogService`、`IUiDispatcher` 三个小接口访问外界，测试里全部替换成内存实现，
  所以 ViewModel 的整条流程（扫描→连接→接收→历史→导出）能在没有显示器、没有蓝牙的 CI 上跑。
* **演示场景（Fake）**：`normal-si`、`imperial`、`body-composition`、`timestamp-user-bmi-height`、`invalid-0xffff`、
  `truncated-packet`、`mid-connection-disconnect`、`reconnect`、`multi-device`。都是脚本回放的字节，经过与真机同一套 Core 解析器；
  **只证明「界面与解析链路能处理这些字节」，不证明任何真实设备。**
* **本地化（与计划的偏差）**：不用 ResourceDictionary XAML，而是嵌入的 `zh.json` / `en.json` 字典 + `Loc` 单例 + `{loc:T Key}` 标记扩展
  （对 `Loc.Instance` 的索引器 `[key]` 做 OneWay 绑定）。理由：语言能在运行时切换而无需重启，字典可以被单元测试（zh/en 键一致、占位符一致、
  英文里不能出现中文、XAML 与 ViewModel 用到的键都存在、每个 `BleErrorKind` 都有 `Err.<kind>` 文案）。
  默认跟随系统语言（`zh*` → 中文，其余英文），选择会保存。
* **绑定方式**：关闭了编译绑定（`AvaloniaUseCompiledBindingsByDefault=false`），用反射绑定——索引器绑定在编译绑定下不好用。
  **踩过的坑**：Avalonia 只在 `PropertyChanged` 名为 `"Item"` 时刷新索引器绑定，`"Item[]"` 不够；`Loc` 切换语言时两个都发。
* **数据**：历史与设置在 `scalebridge.db`（路径见 PRIVACY.md）。`MeasurementIngestor` 在删除/清空历史后需要 `Reload()` 才会重建去重状态。
* **导出**：CSV 与 JSON 都导出**全部历史**（与界面上的过滤无关，有测试）。
* **已知限制**：WSS（0x181D）与 BCS（0x181B）的记录不合并（Q17）；没有接收节流与历史条数上限（Q19）；BCS 多包重组留到真实适配器之后（Q6/Q7）。
* **界面检查**：在 `Xvfb 1024x700` 下对 zh/en 的四个页签各截一次图，目视没有溢出，长文本截断；这是人工看图，不是自动化断言。
* **打包**：`release.yml` 发布 `win-x64` 与 `linux-x64` 的**自包含单文件**（含 .NET 运行时；未裁剪；未签名）。
  Linux 产物在 CI 里用 xvfb 跑 `--smoke`；Windows 产物只验证能构建与打包，**没有在 Windows 上启动过界面**。
  工作流只上传 Actions 构件，**不创建 GitHub Release**。
* **Windows 控制台**：App 是 WinExe；带 `--help/--list-scenarios/--smoke/--scenario` 启动时会 `AttachConsole` 接上父进程控制台才能看到输出
  （未在 Windows 上实测）。

## 路线图（仅规划，阶段 1 不实现）

* **阶段 2 — Windows**：WinRT 蓝牙适配器已按规范实现（实验性，见文末「Windows 适配器」）。
* **阶段 2 — Linux**：基于 D-Bus 的 BlueZ 适配器（评估 Tmds.DBus 等库，见下）。仓库里已有他人加入的探索性脚手架 `src/ScaleBridge.Linux`，未接入界面、未真机验证，不算阶段 1 交付。
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

## Linux 适配器与 D-Bus 库评估（时尚1）

> **实验性（仅按规范/公开资料实现，未真机验证）。** 下面的评估与实现只依据公开资料（NuGet、GitHub、BlueZ 官方文档、Tmds.DBus 文档）和自己的设计；
> 没有阅读或复制 ble-scale-sync（GPL-3.0）。资料整理于 2026-10-26（Asia/Shanghai）；NuGet 的发布时间是 UTC，GitHub 的时间带 Z 的也是 UTC。

### D-Bus 库评估

| 包 | 最新版（发布时间 UTC） | 许可证 | 目标框架 | 维护 / AOT / 备注 |
|---|---|---|---|---|
| **Tmds.DBus.Protocol**（选用） | 0.95.1（2026-09-04） | MIT | netstandard2.0/2.1、net6.0、**net8.0**、net9.0 | 官方定位「现代、低层、NativeAOT 兼容」；net7.0+ 设置了 `IsAotCompatible`，不兼容的成员标了 Obsolete + RequiresUnreferencedCode。依赖 System.IO.Pipelines。我用 `-p:IsAotCompatible=true` 编译 Linux 项目，没有裁剪/AOT 警告（只是编译期分析，没有真的做 NativeAOT 发布）。 |
| Tmds.DBus | 0.95.1（2026-09-04） | MIT | net6.0、netstandard2.0（没有 net8.0 资产） | 官方文档写「维护模式，不应用于新项目」；基于反射和运行时代码生成，我判断不适合 AOT（推断，没有官方 AOT 声明）。NuGet 上没有 deprecated 标记。 |
| Tmds.DBus.Generator | 0.95.1（2026-09-04） | NuGet nuspec 没有 license 元素；仓库为 MIT | netstandard2.0 源生成器 | **在 SDK 8 下不可用**，见下。 |
| Linux.Bluetooth（SuessLabs） | 稳定版 5.67.1（2024-09-08）；预发布 6.0.0-pre3（2026-04-10） | Apache-2.0 | netstandard2.0 | 6.0.0-pre3 依赖旧的 Tmds.DBus 0.92.0；README 写 BlueZ ≥ 5.50、在 Ubuntu 20.04 上测过。仓库 2026-09-05 仍有提交。稳定版 5.67.1 的依赖版本我没核实，但它早于 0.92.0 的安全修复。 |
| HashtagChris.DotNetBlueZ | 1.3.1（2020-11-02） | Apache-2.0 | netstandard2.0 | 依赖 Tmds.DBus 0.7.0（在漏洞范围内），仓库 2020-11-02 后没有提交，README 自称「quick and dirty」，实质无人维护。 |

* **安全公告**：CVE-2026-39959 / GHSA-xrw6-gwf8-vvr9（高危，2026-04-08 公布）：知名名称所有者造成的信号伪造、fd 耗尽、畸形消息体导致 DoS。
  Tmds.DBus 0.92.0、Tmds.DBus.Protocol 0.92.0 / 0.21.3 起修复；本项目固定使用 0.95.1，不受影响。
* **决定**：在 Tmds.DBus.Protocol ≥ 0.95.1 上写一层薄的 BlueZ 代理，藏在 `IBleAdapter` 之后（可替换）。不用 Tmds.DBus、DotNetBlueZ、Linux.Bluetooth。
* **主要风险**：
  1. 0.x 版本号且发生过破坏性变更（0.93、0.94），只固定 0.95.1，别信旧样例（仓库 `docs/protocol.md` 还在用旧的 `Connection.Session` API）。
  2. 基本只有一位维护者。
  3. Protocol 的 issue #456（同一次读取里无 fd 的消息先于带 fd 的消息时 fd 被关闭，2026-10-25 提出）：只在用 fd 传递（AcquireNotify/AcquireWrite）时相关——我们不用。
  4. `PropertiesChanged` 的 `Value` 可能只是「失效」而不带值 → 回退 `ReadValue`。
  5. 文档提到 Protocol 不像旧库那样保证信号顺序（我没有验证到什么程度）→ 所有处理器只写队列，状态以最终快照/最新值为准。
  6. Generator 的 NuGet 元数据缺 license 元素（仓库是 MIT），合规扫描器可能报。
  7. BlueZ 版本差异、配对/绑定、适配器的 D-Bus 权限策略；BlueZ 文档读的是 master，不是固定发行版。
  8. 漏洞版本：必须 Tmds.DBus.Protocol ≥ 0.21.3 / 0.92.0。

### Generator 在 SDK 8 下不可用

Tmds.DBus.Generator 0.95.1 是针对 `Microsoft.CodeAnalysis.CSharp` 4.12.0 编译的；在本机 SDK 8.0.426 的编译器上，加载时出现分析器警告 **CS9057**（分析器引用的编译器版本比当前高），
**没有生成任何代理代码**。所以 `src/ScaleBridge.Linux/Bluez/TmdsBluezBus.cs` 里的代理是手写的（用 `DBusConnection`、`MessageWriter`、`Reader`、`VariantValue`、`MatchRule`）。
升级到更新的 SDK 后可以重新评估，但手写层很小（约 400 行），而且被 `IBluezBus` 隔离。

### 选定的 BlueZ 流程（依据 bluez/doc 的 org.bluez.Adapter/Device/GattCharacteristic 文档）

系统总线上的 `org.bluez`：

1. **扫描**：`Adapter1.SetDiscoveryFilter`（`UUIDs`、`Transport=le`、`DuplicateData=true`；被拒绝时退回客户端过滤）→ 先订阅 `ObjectManager.InterfacesAdded/InterfacesRemoved` 与 `Properties.PropertiesChanged`，再 `StartDiscovery`，之后用 `GetManagedObjects` 取现有设备快照；增量属性合并到每台设备的缓存，映射成 `AdvertisedDevice`；结束时 `StopDiscovery`（best-effort）。
2. **连接**：先订阅设备的 `PropertiesChanged`，再 `Device1.Connect`，等待 `ServicesResolved == true`（超时默认 30 秒，抛 `BleTimeoutException`）；`Connected` 变 false → `DeviceDisconnectedException` / 连接状态 Dropped。
3. **GATT 查找**：`GetManagedObjects`，按 `GattService1.UUID` + `GattCharacteristic1.Service` + 设备路径前缀按 UUID 找特征。
4. **读**：`GattCharacteristic1.ReadValue({})`。
5. **通知**：先订阅特征路径的 `PropertiesChanged`，再 `StartNotify`；从变化里取 `Value`（`ay`），只被失效或类型异常时回退一次 `ReadValue`；释放时 `StopNotify`（best-effort）。**不使用 `AcquireNotify` / fd。**
6. **错误**：`org.bluez.Error.*` 与 `org.freedesktop.DBus.Error.{ServiceUnknown,NameHasNoOwner,AccessDenied,NoReply,Timeout,NoServer,Disconnected}` 经 `BluezErrorMapper` 映射为 `BleException` 子类；异常消息只含操作名和 D-Bus 错误名。

### 真实总线与假总线各自覆盖了什么

* **`TmdsBluezBus`（真实实现，Tmds.DBus.Protocol）**：`GetManagedObjects` 解析（`a{oa{sa{sv}}}`，含 `ay`、`as`、`a{qv}`、`a{sv}`、`o`、`n`、`q`、`u`、`b`）、`SetDiscoveryFilter`、`StartDiscovery/StopDiscovery`、`Connect/Disconnect`、`ReadValue`、`StartNotify/StopNotify`、
  三种 `AddMatch` 观察者（`InterfacesAdded`、`InterfacesRemoved`、`PropertiesChanged`，可按路径或 `/org/bluez` 命名空间）、`DBusErrorReplyException` → `BluezDBusException`、连接断开事件 `ConnectionClosed`。
  **验证方式**：`TmdsBluezBusIntegrationTests` 在私有 `dbus-daemon` 上用 Tmds.DBus.Protocol 自己写的假 `org.bluez` 服务走真实线上编码/解码，另外还有一个经由 `BluezBleAdapter` 的端到端用例（可用性 → 扫描 → 连接 → 读 → 订阅）。
  这些测试在找不到 `dbus-daemon` 的机器（例如 Windows CI）上标为跳过。
  **这只证明实现与我们对 D-Bus 规范和 BlueZ 文档签名的理解自洽，没有对真实 BlueZ 验证过。**
* **`FakeBluezBus`（内存假总线）**：所有业务逻辑——扫描合并与去重、过滤、连接等待与超时、通知与回退、错误映射、取消与释放顺序、竞争（先订阅后调用）——都只在这里测过。

### 没有真机验证的假设

* `ConnectionAttemptFailed` 映射为 `Timeout` 是判断，不是文档规定。
* `StartDiscovery` / `StartNotify` 返回 `InProgress` 时视为已在进行并容忍。
* `SetDiscoveryFilter` 返回 `NotSupported` / `InvalidArguments` / `Failed` 时忽略并改为客户端过滤。
* `Value` 被失效（`PropertiesChanged` 的 invalidated 里有 `Value`）时回退 `ReadValue` 的行为；以及 `PropertiesChanged` 的先后顺序。
* `ServicesResolved` 的语义（连接后何时变 true、断开后何时回到 false）。
* 扫描里存在一个已接受的小竞争：在订阅与 `GetManagedObjects` 快照之间到达的增量，可能被稍旧的快照覆盖。
* 地址脱敏格式（保留前 4 个字节：`AA:BB:CC:DD:**:**`，路径中 `dev_AA_BB_CC_DD_**_**`）是我自己定的。
* 默认连接超时 30 秒是经验值。
* 适配器未上电（`Powered=false`）时 `GetAvailabilityAsync` 报 `Unavailable`，而不是 `PermissionDenied`。
* 只用第一个（或指定名称的）适配器；每次扫描/连接各开一条自己的总线连接；没有做配对/绑定。
* 系统总线地址只用 `DBUS_SYSTEM_BUS_ADDRESS` 或默认的 `/var/run/dbus/system_bus_socket`。
* 手写的线上编解码只对我们自己的假服务验证过（见上），真实 BlueZ 的属性类型、可选属性和额外接口可能不同；无法识别类型的属性会被丢弃而不是报错。
* `/org/bluez` 命名空间的 `PropertiesChanged` 匹配规则以及知名名称 `org.bluez` 的发送方过滤在私有 `dbus-daemon` 上工作，在真实系统总线（带策略、`bluetoothd`）上的行为未验证。

### 来源

* https://www.nuget.org/packages/Tmds.DBus 、/Tmds.DBus.Protocol 、/Tmds.DBus.Tool 、/Tmds.DBus.Generator 、/Linux.Bluetooth 、/HashtagChris.DotNetBlueZ（以及 api.nuget.org 的 registration / nuspec JSON）
* https://github.com/tmds/Tmds.DBus（含 GitHub API 的 repo / issues / releases，GHSA-xrw6-gwf8-vvr9）
* https://tmds.github.io/Tmds.DBus/
* https://github.com/SuessLabs/Linux.Bluetooth ；https://github.com/hashtagchris/DotNet-BlueZ
* https://github.com/bluez/bluez/tree/master/doc（org.bluez.Adapter.rst、Device.rst、GattCharacteristic.rst）

## Windows 适配器

> **实验性（仅按规范实现，未真机验证）。** 作者没有蓝牙硬件，也没有可运行 Windows 图形界面的机器；
> 这里的一切只按微软官方文档与 Bluetooth SIG 规范编写，**从未在真实的 Windows 电脑、真实蓝牙适配器或真实体重秤上运行过**。
> 默认选项仍是「演示数据」（Fake）；真实蓝牙需要用户在设置里手动切换。

### 结构（为什么拆成两个项目）

* `src/ScaleBridge.Windows.Core`（`net8.0`）：不引用任何 WinRT 的纯逻辑——`WindowsBleAdapter` / `WindowsBleConnection`（实现 Core 的 `IBleAdapter` / `IBleConnection`）、
  看门狗 `WinWatchdog`、连接状态机、广播缓存、地址转换、错误映射，以及一小组 `IWin*` 接口（平台替身点）。
  这一层可以在 Linux 上直接做单元测试（`tests/ScaleBridge.Windows.Core.Tests`，用内存替身，含时间由 `TimeProvider` 驱动的看门狗测试）。
* `src/ScaleBridge.Windows`（`net8.0-windows10.0.19041.0`）：薄薄一层 WinRT 外壳，只负责调用 WinRT API 并把结果换成上面的接口类型。
  用 .NET 内置的 WinRT 投影（C#/WinRT，由 Windows TFM 提供），**没有任何第三方 NuGet 包**。
  `tests/ScaleBridge.Windows.Tests` 只在 Windows 上运行（Linux 上 `IsTestProject=false`，只编译）；这些测试同样不需要蓝牙硬件。
* `EnableWindowsTargeting=true`：Linux CI 也能还原并编译 Windows 项目；运行 WinRT 代码只能在 Windows 上。
* App 对 `net8.0` 与 `net8.0-windows10.0.19041.0` 双目标；只有 Windows 目标引用 `ScaleBridge.Windows`（条件 `ProjectReference` + `#if WINDOWS`），
  Linux 构建里根本没有这个适配器，界面只会出现「演示数据」。

### API 选择

| 需求 | 用到的 API | 取舍 |
|---|---|---|
| 可用性 | `BluetoothAdapter.GetDefaultAsync`、`IsLowEnergySupported`、`GetRadioAsync` + `Radio.State` | 没有适配器 / 不支持 LE / 无线电关闭或被策略禁用 → `BleAvailability.Unavailable`。**刻意不调用 `Radio.RequestAccessAsync`**，避免在后台弹出系统授权对话框。`Radio.State=Unknown` 视为可用（由随后的操作报真实错误）。 |
| 扫描 | `BluetoothLEAdvertisementWatcher`，`ScanningMode = Active` | Active 才会发出 scan request，拿到完整名称和扫描响应。回调里只做拷贝入队（有界通道，满时丢最旧），不在 WinRT 回调线程里做重活。 |
| 广播内容 | `LocalName`、`ServiceUuids`、`ManufacturerData`、`RawSignalStrengthInDBm` | 广播包与扫描响应是**两个独立事件**，所以按地址做缓存合并（名称、服务、厂商数据取并集；有容量上限）。RSSI 的 127 在规范里是「不可用」，映射成 `null`。 |
| 连接 | `BluetoothLEDevice.FromBluetoothAddressAsync` + `GattSession.FromDeviceIdAsync`（`MaintainConnection = true`） | `MaintainConnection` 失败不致命（只记录原因，继续走普通 GATT 流程）。文档说明系统会无限期等待连接，真正的连接发生在第一次 GATT 操作，所以必须有我们自己的看门狗。 |
| 服务发现 | `GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached)`、`GetCharacteristicsAsync(Uncached)` | 见下「Windows 行为差异」。只查询 Core 关心的 0x181D / 0x181B 两个服务，不枚举全部服务。 |
| 读 | `GattCharacteristic.ReadValueAsync(Uncached)` | 状态码与 ATT 错误码都映射。 |
| 订阅 | 特征支持 Indicate 就用 Indicate，否则 Notify：`WriteClientCharacteristicConfigurationDescriptorWithResultAsync` + `ValueChanged` | 0x2A9D / 0x2A9C 按规范是 Indicate。用 `…WithResultAsync` 才能拿到 `ProtocolError` 里的 ATT 错误码。同一特征重复订阅：复用已有的 CCCD 写入，不重复写；最后一个订阅者退出时才写 `None`。 |
| 通知字节 | `CryptographicBuffer.CopyToByteArray` | **必须在 `ValueChanged` 回调内拷贝**，`IBuffer` 在回调返回后可能被复用；测试里专门有「缓冲区被复用」用例。 |
| 超时 | 每个异步步骤包一层看门狗（连接默认 10 s，单步 9 s，可用性 5 s） | WinRT 的异步操作可能长期不完成；到点就取消并抛 `BleTimeoutException`，同时 `Cancel()` 底层操作。 |
| 状态事件 | `BluetoothLEDevice.ConnectionStatusChanged` | 首次连接建立之前收到的 `Disconnected` 视为噪声，不算掉线；建立之后的断开才变成 `Dropped`，订阅流抛 `DeviceDisconnectedException`。 |

错误映射（`WinErrorMapper`）：无线电/适配器问题 → `BluetoothUnavailable`；`AccessDenied` 与「需要认证/加密」类 ATT 错误（0x05、0x08、0x0C、0x0F）→ `PermissionDenied`（附「请先在 Windows 设置里配对」的提示）；
`Unreachable` 与「设备未连接」类 HRESULT → `DeviceDisconnected`；看门狗到期 → `Timeout`；其余 → `Other`。异常消息只含操作名与错误码，不含设备地址。

### Windows 行为差异（按文档整理；**均未真机验证**）

* **已配对/已缓存的 GATT 数据**：Windows 会缓存已配对设备的服务与特征。默认 `Cached` 可能返回过期的结果（甚至设备不在身边也「成功」），所以服务发现、特征发现、读取一律用 `Uncached`。代价是每次都真正访问设备，更慢。
* **有的设备要先在系统里配对**：对需要加密/认证的特征，未配对时读写会以 `AccessDenied` 或 ATT 认证类错误失败。本适配器**不会**自己发起配对（避免在没有人看着的时候弹出系统配对 UI），只给出「先在 设置 → 蓝牙和设备 里配对」的提示。常见的体重秤广播式/开放式 GATT 不需要，但是否如此取决于具体型号，**未核实**。
* **写 CCCD 失败的常见原因**：设备不在范围内或已断开（`Unreachable`）；特征不支持 Notify/Indicate（`ProtocolError`，ATT 0x0A/0x03 一类）；需要加密但没有配对（0x05/0x0F）；另一个应用占用同一连接；订阅过程中系统回收了特征对象。
* **对象必须持有引用**：`GattCharacteristic` 被 GC 回收后 `ValueChanged` 会静默停止。适配器在订阅期间持有特征、服务与设备对象，直到取消订阅。
* **`ValueChanged` 在 WinRT 线程池线程上触发**：不能在回调里阻塞，所以只拷贝字节并写入有界通道。
* **扫描**：Windows 对同一设备会反复触发 `Received`；扫描响应与广播分开到达（见缓存）；同一设备的 RSSI 会抖动。

### 取舍

* 连接成功只代表「找到了设备并完成了服务发现」；是否持续连接依赖 Windows 的 `MaintainConnection`，并不保证长时间在线。
* 不做自动重连：掉线后由上层（用户或 `ConnectAsync` 再次调用）发起新连接；适配器保证旧连接对象被完整释放后新连接才不会串线（有「掉线后重连」的单元测试，用替身）。
* 释放（`DisposeAsync`）幂等，等待进行中的操作最多 2 秒，之后强制清理；与订阅、读取并发释放有专门的竞态测试（替身）。
* 不采用任何第三方蓝牙库；也没有照抄或参考其他项目（尤其 GPL 项目）的代码或结构，实现只来自下面列出的官方文档。

### 未真机验证清单

1. Windows 上 `BluetoothLEAdvertisementWatcher` 的 Active 扫描是否能拿到体重秤的名称与服务（含扫描响应的合并行为）。
2. `GetGattServicesForUuidAsync(Uncached)` 在已配对与未配对设备上的结果；是否需要先配对。
3. 对 0x2A9D / 0x2A9C 的 Indicate 订阅（CCCD 写入）的成功与失败路径，及失败时 Windows 返回的真实状态码与 HRESULT（`WinErrorMapper` 里的 HRESULT 表是按文档整理的，没有在真实栈里确认出现）。
4. 看门狗数值（10 s / 9 s / 5 s）是否合适；`MaintainConnection` 在真实设备上的效果。
5. 「首次连接前的 `Disconnected` 是噪声」这个判断在真实设备上是否成立；掉线后能否重连。
6. 进程退出、休眠/唤醒、蓝牙开关被切换时 WinRT 对象的行为。
7. 打包后的自包含单文件版本能否正常加载 WinRT 投影（CI 只验证能构建与打包，且 `--smoke` 只启动演示数据，不碰蓝牙）。
8. 适配器在真实 Windows 版本（Windows 10 19041 起）上的兼容性。
9. Windows 上 App 界面本身（CI 只做 `--smoke` 无头冒烟，没有人看过界面）。

### Core 抽象与 Linux 适配器之间的摩擦（仅记录，没有改动 Linux 与 Core 的既有行为）

* Core 的 `BleErrorKind` 没有「找不到设备」与「需要配对」；这里分别落到 `Other` 与 `PermissionDenied`。
* `BleAvailability` 区分不了「蓝牙没打开」和「没有适配器」，只能靠异常消息文字区分。
* `IBleConnection.SubscribeAsync` 的文档要求掉线时抛 `DeviceDisconnectedException`，Linux 适配器的订阅流在掉线时是安静结束；本适配器按文档抛异常。
* 释放之后再调用 `DisconnectAsync`：Linux 抛异常，本适配器幂等返回。
* Core 的 `GattCharacteristicProperties` 数值与 WinRT 的枚举值不同，所以映射只能按名称，不能强制转换。
* `IBleAdapter` 的注释仍写着「阶段 1 只有 Fake」。
* 我开始本任务时 Linux 适配器的构造函数是 `internal`、也没有真实的 `IBluezBus`；其后另一条工作线加入了基于 Tmds.DBus.Protocol 的真实总线并公开了构造函数。
  它**尚未接进界面**（本任务按约定没有碰 `src/ScaleBridge.Linux`，Linux 构建的界面仍只有「演示数据」）。

### 来源（只用了微软官方文档与 Bluetooth SIG 规范）

* BluetoothLEAdvertisementWatcher：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher
* BluetoothLEScanningMode：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothlescanningmode
* RawSignalStrengthInDBm：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementreceivedeventargs.rawsignalstrengthindbm
* BluetoothLEDevice.FromBluetoothAddressAsync：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.frombluetoothaddressasync
* BluetoothLEDevice.GetGattServicesForUuidAsync：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.getgattservicesforuuidasync
* BluetoothLEDevice.ConnectionStatusChanged：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.connectionstatuschanged
* BluetoothCacheMode：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothcachemode
* GattSession.MaintainConnection：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattsession.maintainconnection
* GattCharacteristic.ReadValueAsync：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.readvalueasync
* GattCharacteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.writeclientcharacteristicconfigurationdescriptorwithresultasync
* GattCharacteristic.ValueChanged：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.valuechanged
* GattCharacteristicProperties：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristicproperties
* GattCommunicationStatus：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcommunicationstatus
* BluetoothError：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetootherror
* BluetoothAdapter.GetDefaultAsync：https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothadapter.getdefaultasync
* RadioState：https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radiostate
* CryptographicBuffer.CopyToByteArray：https://learn.microsoft.com/en-us/uwp/api/windows.security.cryptography.cryptographicbuffer.copytobytearray
* GATT 客户端指南：https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client
* 设备配对：https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/pair-devices
* Win32 系统错误码（HRESULT 对照）：https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-
* C#/WinRT 与 .NET 的 WinRT 投影：https://learn.microsoft.com/en-us/windows/apps/develop/platform/csharp-winrt/
* Bluetooth Core Specification Vol 3 Part F §3.4.1.1（ATT Error Response 与错误码）：https://www.bluetooth.com/specifications/specs/core-specification/
* Bluetooth Core Specification Vol 4 Part E §7.5.4（Read RSSI：无法读取时为 127）：同上。
