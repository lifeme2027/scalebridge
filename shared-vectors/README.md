# shared-vectors — 与实现语言无关的字节向量

这些 JSON 文件描述「一段 GATT 字节 → 应解析出什么（或应被拒绝）」。
.NET 版 `ScaleBridge.Core` 用它们做测试（`tests/ScaleBridge.Core.Tests/SharedVectorsTests.cs`），
其它实现（例如 Web 版）可以用同一批文件验证自己的解析器。

**重要：这些向量是按规范手工构造的，不是从真实设备抓取的；通过这些向量不代表任何真实设备验证过。**
对应协议在项目里一律标「实验性（仅按规范实现，未真机验证）」。
期望值由另一套独立脚本（Python `Decimal`）按规范算出，再与 C# 解析器相互核对。

## 文件格式 `scalebridge-vectors/1`

文件顶层：

| 字段 | 类型 | 说明 |
|---|---|---|
| `format` | string | 固定为 `"scalebridge-vectors/1"`。格式有不兼容改动时才会换版本号。 |
| `suite` | string | 套件名（`weight-scale` / `body-composition`）。 |
| `description` | string | 人类可读的说明。 |
| `vectors` | array | 向量列表，见下。 |

每条向量：

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 全仓库唯一的名字（可用来按名字定位失败项）。 |
| `service` | string | 服务 16 位 UUID，大写十六进制，例如 `"181D"`。 |
| `characteristic` | string | 特征 16 位 UUID，例如 `"2A9D"`。 |
| `hex` | string | 输入字节，十六进制，字节间用空格，例如 `"00 59 36"`；空串表示 0 字节。 |
| `reject` | bool | `true`：解析器必须**拒绝**这段数据（返回错误，不能崩溃）。 |
| `errorCode` | string | 仅 `reject=true` 时出现，期望的错误码，见下。 |
| `warnings` | string[] | 仅 `reject=false` 时出现：解析成功但应给出的警告码（无顺序，集合必须完全一致）。 |
| `expect` | object | 仅 `reject=false` 时出现：期望的解析结果，**所有键都必须出现**，不存在的值写 `null`。 |

### 错误码（`errorCode`）

* `TooShort`：数据短于该特征的最小长度（连必选字段都不全）。例外：Body Composition Measurement 在已读到 Flags 且 bit12 置位时优先 `MultiPacketNotSupported`（见 NOTES.md Q21）。
* `Truncated`：Flags 声明存在的可选字段放不下（截断包）。
* `MultiPacketNotSupported`：Body Composition 的 Multiple Packet Measurement 位（bit12）被置位，阶段 1 不支持。

### 警告码（`warnings`）

* `ReservedBitsSet`：Flags/Feature 的保留位被置位。
* `TrailingBytes`：数据比 Flags 描述的长，多余字节被忽略。
* `InvalidTimestamp`：时间戳字段存在但不是合法日期时间（年 1582–9999，月日时分秒在范围内，日历上真实存在），按 `null` 处理。
* `FieldsIgnoredMeasurementUnsuccessful`：Weight/Body Fat 为 0xFFFF（测量不成功）时，Flags 却声明了时间戳与用户 ID 之外的可选字段，这些字段被忽略。
* `OptionalFieldMarkedUnknown`：可选字段取 0xFFFF。规范没有为该字段定义此值，本项目保守地按「未知」(`null`) 处理。
* `ReservedResolutionCode`：Feature 里的分辨率码是保留值（质量 ≥8、身高 ≥4），按「未知」处理。

### 数值与单位的表示

* 带小数的值一律写成**字符串**（如 `"70.005"`），比较时按十进制数值比较（`"70.005"` 等于 `"70.0050"`），不要用二进制浮点。
* 质量对象：`{"raw": 整数, "original": "设备单位下的值(kg 或 lb)", "kilograms": "精确换算成 kg"}`。
* 身高对象：`{"raw": 整数, "original": "设备单位下的值(m 或 in)", "meters": "精确换算成 m"}`。
* `unit`：`"si"`（kg / m）或 `"imperial"`（lb / in）。
* 精度：质量 SI 0.005 kg、Imperial 0.01 lb；身高 SI 0.001 m、Imperial 0.1 in；BMI、体脂率、肌肉率 0.1；阻抗 0.1 Ω；基础代谢 1 kJ。
  这些是固定刻度，**不随 Feature 的分辨率位变化**（Feature 的分辨率码只作为精度提示解码）。
* 磅/英寸换算用定义值：1 lb = 0.45359237 kg，1 in = 0.0254 m，结果是精确十进制数（最多 10 位小数），没有舍入。
* `timestamp`：`"YYYY-MM-DDTHH:MM:SS"`（设备本地时间，不带时区）或 `null`。
* `userId`：整数；字段不存在或 `0xFF`（未知用户）时为 `null`。
* 所有整数字节序均为小端；所有 `raw` 为字段的无符号整数原值。

### `expect` 各特征的键

* `2A9E` Weight Scale Feature：`timeStampSupported` `multipleUsersSupported` `bmiSupported`
  `weightResolutionCode` `heightResolutionCode` `weightResolutionKg` `weightResolutionLb` `heightResolutionMeters` `heightResolutionInches`
  （分辨率码为 0 或保留值时对应分辨率为 `null`）。
* `2A9D` Weight Measurement：`unit` `measurementUnsuccessful` `weight` `timestamp` `userId` `bmi` `height`
  （`measurementUnsuccessful=true` 时 `weight` 为 `null`）。
* `2A9B` Body Composition Feature：`timeStampSupported` `multipleUsersSupported` `basalMetabolismSupported` `musclePercentageSupported`
  `muscleMassSupported` `fatFreeMassSupported` `softLeanMassSupported` `bodyWaterMassSupported` `impedanceSupported`
  `weightSupported` `heightSupported` `massResolutionCode` `heightResolutionCode` `massResolutionKg` `massResolutionLb`
  `heightResolutionMeters` `heightResolutionInches`。
* `2A9C` Body Composition Measurement：`unit` `measurementUnsuccessful` `bodyFatPercent` `timestamp` `userId`
  `basalMetabolismKj` `musclePercent` `muscleMass` `fatFreeMass` `softLeanMass` `bodyWaterMass` `impedanceOhm` `weight` `height`
  （`measurementUnsuccessful=true` 时 `bodyFatPercent` 与所有可选量为 `null`）。

字段布局与规范来源见仓库根目录 `NOTES.md` 的「协议信息来源」。
