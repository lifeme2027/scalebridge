# shared-vectors/vendor — 厂商私有协议向量（实验性）

本目录里的向量使用与 `shared-vectors/README.md` 相同的文件格式 `scalebridge-vectors/1`，**但放在子目录里**，
因为 `SharedVectorsTests` 只匹配 `shared-vectors/*.json`（不含子目录），并且只认识标准 GATT 特征；
厂商向量由新的测试类 `VendorVectorsTests` 读取。**不改动现有向量文件与 `README.md`。**

**所有厂商协议一律标注：「实验性（仅按规范/公开资料实现，未真机验证）」。**
向量是按 `NOTES.md`「厂商协议来源（时尚1）」里记录的字节布局**手工构造**的，不是从真机抓取；
期望值由独立的 Python `Decimal` 脚本（`generators/`）算出，再与 C# 解析器互相核对。通过向量不代表任何真机验证。

## 与标准向量相比的扩展

顶层多出：

| 字段 | 说明 |
|---|---|
| `vendor` | 厂商名（`xiaomi` / `renpho` / `chipsea-okok`）。 |
| `experimental` | 固定 `true`。 |

每条向量多出：

| 字段 | 说明 |
|---|---|
| `source` | `advertisement-service-data`（广播里的 Service Data）、`advertisement-manufacturer-data`（广播里的 manufacturer-specific data，Chipsea/OKOK）或 `notification`（GATT notify/indicate）。 |
| `context` | 可选对象，目前只有 `assumedUnit`（`Kg`/`Lb`/`Jin`/`StoneLb`），原样传给解析器；缺省为不提供。 |
| `frameType` | 帧类型的稳定标识，决定用哪个解析器（见各文件）。 |
| `description` | 可选，人类可读说明。 |
| `characteristic` | 广播类向量为 `null`；notification 类为 16 位特征 UUID。 |
| `service` | Service Data 广播时是 16 位 UUID；manufacturer data 向量为 `null`。 |
| `hex` | **只含负载**（不含 AD 结构的长度/类型字节，也不含 2 字节 UUID 前缀）。 |

`reject` / `errorCode` / `warnings` / `expect` 的语义不变（`warnings` 是集合，`expect` 的所有键必须出现，缺失值写 `null`）。

## 厂商错误码与警告码

厂商解析器使用独立的枚举（`VendorParseErrorCode` / `VendorParseWarningCode`，在 `ScaleBridge.Core.Protocols`），不改动 `ParseErrorCode`：

* `TooShort`：数据短于该帧要求的长度。
* `AmbiguousUnit`：单位标志位互相矛盾。
* `UnsupportedService`：分发入口收到不认识的 Service UUID。
* `BadHeader`：魔数 / 版本 / 长度字节不符（Chipsea/OKOK）。
* `BadChecksum`：校验和不符（Chipsea/OKOK）。
* `InvalidValue`：字段取值未定义（Chipsea/OKOK 小数位 `11`、v2.0 帧给了 `StoneLb`）。
* `UnitAssumed`（警告）：帧不带单位，调用方没给 `assumedUnit`，按 kg 处理（Chipsea/OKOK v2.0）。
* `TrailingBytes`（警告）：数据比帧长，多余字节被忽略。
* `InvalidTimestamp`（警告）：时间戳字段不是合法日期时间，按 `null` 处理。


## `expect` 的键（Xiaomi，`vendor-xiaomi`）

`protocol` `rawWeight` `rawUnit`(`Kg`/`Lb`/`Jin`) `originalWeight`(设备单位下的值，十进制字符串) `weightKg`(精确换算，十进制字符串)
`timestamp`(`YYYY-MM-DDTHH:MM:SS` 或 `null`) `impedanceRaw`(整数或 `null`；单位未在公开资料中确认，所以只给原始值)
`stabilized` `weightRemoved` `isFinal`(`stabilized && !weightRemoved`)。

`frameType`：`xiaomi-mi-scale-v1-10`（0x181D，10 字节）、`xiaomi-body-composition-scale-2-13`（0x181B，13 字节）、`xiaomi-service-data-dispatch`（按 `service` 分发）。

## Chipsea / OKOK（`vendor-chipsea-okok`，`chipsea-okok.json`）——实验性

实现：`ChipseaOkokParser`。`hex` 是 manufacturer data，**从 0xCA 起**（含 `CA 11` / `CA 20`），`source` 为 `advertisement-manufacturer-data`，`service` 与 `characteristic` 为 `null`。
`frameType`：`chipsea-okok-v11-broadcast-19`、`chipsea-okok-v20-broadcast-15`、`chipsea-okok-manufacturer-data-dispatch`（按版本字节分发）。

`expect` 的键与 Xiaomi 相同；`protocol` 为 `ChipseaOkok11` / `ChipseaOkok20`。差异：

* `rawUnit` 可为 `StoneLb`：`rawWeight` 为 16 位原始值（高字节英石、低字节磅×10），`originalWeight` 是**总磅数**（英石×14 + 磅）。
* `timestamp` 恒为 `null`；`impedanceRaw` 在 v1.1 恒为 `null`，v2.0 为整数原始值（单位未确认）。
* v2.0 向量在没有 `context.assumedUnit` 时 `warnings` 为 `["UnitAssumed"]`。
* v1.1 校验和两种规则都被接受（厂商文档：XOR idx1..17；一份公开抓包：XOR idx0..17），向量两种都有。

Renpho：没有可合规的公开来源，**未实现**，所以没有 `renpho.json`（见 `NOTES.md`）。
