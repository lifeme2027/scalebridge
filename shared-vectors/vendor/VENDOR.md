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
| `source` | `advertisement-service-data`（广播里的 Service Data / 厂商数据）或 `notification`（GATT notify/indicate）。 |
| `frameType` | 帧类型的稳定标识，决定用哪个解析器（见各文件）。 |
| `description` | 可选，人类可读说明。 |
| `characteristic` | 广播类向量为 `null`；notification 类为 16 位特征 UUID。 |
| `service` | 广播时是 16 位 Service Data UUID；其它情况按协议说明。 |
| `hex` | **只含负载**（不含 AD 结构的长度/类型字节，也不含 2 字节 UUID 前缀）。 |

`reject` / `errorCode` / `warnings` / `expect` 的语义不变（`warnings` 是集合，`expect` 的所有键必须出现，缺失值写 `null`）。

## 厂商错误码与警告码

厂商解析器使用独立的枚举（`VendorParseErrorCode` / `VendorParseWarningCode`，在 `ScaleBridge.Core.Protocols`），不改动 `ParseErrorCode`：

* `TooShort`：数据短于该帧要求的长度。
* `AmbiguousUnit`：单位标志位互相矛盾。
* `UnsupportedService`：分发入口收到不认识的 Service UUID。
* `TrailingBytes`（警告）：数据比帧长，多余字节被忽略。
* `InvalidTimestamp`（警告）：时间戳字段不是合法日期时间，按 `null` 处理。

（后续厂商会追加 `BadChecksum` 等，见各文件。）

## `expect` 的键（Xiaomi，`vendor-xiaomi`）

`protocol` `rawWeight` `rawUnit`(`Kg`/`Lb`/`Jin`) `originalWeight`(设备单位下的值，十进制字符串) `weightKg`(精确换算，十进制字符串)
`timestamp`(`YYYY-MM-DDTHH:MM:SS` 或 `null`) `impedanceRaw`(整数或 `null`；单位未在公开资料中确认，所以只给原始值)
`stabilized` `weightRemoved` `isFinal`(`stabilized && !weightRemoved`)。

`frameType`：`xiaomi-mi-scale-v1-10`（0x181D，10 字节）、`xiaomi-body-composition-scale-2-13`（0x181B，13 字节）、`xiaomi-service-data-dispatch`（按 `service` 分发）。
