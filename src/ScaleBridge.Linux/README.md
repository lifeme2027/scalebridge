# ScaleBridge.Linux（实验性）

> **实验性（仅按规范/公开资料实现，未真机验证）。**
> 这个适配器只依据 BlueZ 官方 D-Bus API 文档和 Tmds.DBus.Protocol 文档写成，
> 没有在真实的 BlueZ / 蓝牙硬件 / 体重秤上跑过。它与 BlueZ 的交互被一个私有 `dbus-daemon`
> 上的**自制假 `org.bluez` 服务**测过（见下文），这只能证明实现与我们对文档的理解自洽，不能证明真机可用。

`BluezBleAdapter` 在 Linux 上实现 `ScaleBridge.Core.Ble.IBleAdapter`：扫描、连接、读特征、订阅通知。
项目在任何操作系统上都能编译；在非 Linux 上 `GetAvailabilityAsync` 返回 `Unavailable`，`ScanAsync`/`ConnectAsync` 抛 `BluetoothUnavailableException`。

## 用法

```csharp
IBleAdapter adapter = new BluezBleAdapter();          // 系统总线上的 BlueZ，取第一个适配器
IBleAdapter hci1    = new BluezBleAdapter("hci1");    // 指定适配器
```

需要：BlueZ（官方文档按 5.x 写，未核对具体版本）、蓝牙适配器已上电、当前用户有权访问系统总线上的 `org.bluez`（D-Bus 策略）。

## 实现取舍

* **库**：`Tmds.DBus.Protocol` 0.95.1（MIT、net8.0、AOT/裁剪友好）上的一层很薄的手写代理。
  不用旧的 `Tmds.DBus`（维护模式、反射/运行时代码生成）、`DotNetBlueZ`、`Linux.Bluetooth`。
  不用 `Tmds.DBus.Generator`：它 0.95.1 需要 Roslyn 4.12，SDK 8.0.x 下报 CS9057 且什么都不生成，所以代理是手写的。
* **不使用 `AcquireNotify` / 文件描述符**（避开 Tmds.DBus issue #456）。通知走 `StartNotify` + 特征 `Value` 的 `PropertiesChanged`；
  `Value` 只被标记为失效或类型异常时回退到一次 `ReadValue`。
* **流程**：`Adapter1.SetDiscoveryFilter`/`StartDiscovery`/`StopDiscovery`；`ObjectManager` 的 `InterfacesAdded` 与设备 `PropertiesChanged`
  合并为扫描流；`Device1.Connect` 之前先订阅 `PropertiesChanged`，再等 `ServicesResolved == true`（带超时，默认 30 秒）；
  GATT 特征通过 `GetManagedObjects` 按服务/特征 UUID 查找；订阅在 `StartNotify` 之前建立，释放时 best-effort `StopNotify`。
* **错误**：`org.bluez.Error.*`、`NotAvailable`、`NotReady`、`ServiceUnknown`、`AccessDenied` 等映射到 `ScaleBridge.Core.Ble` 的
  `BluetoothUnavailableException`、`BlePermissionException`、`BleTimeoutException`、`DeviceDisconnectedException` 等。
  异常消息只含操作名与 D-Bus 错误名，不含完整 MAC 或带 MAC 的对象路径。
* 取消：`Task` 方法抛 `OperationCanceledException`；`IAsyncEnumerable` 流在取消时静默结束；释放时 best-effort 清理（独立 2 秒上限）。

## 结构

* `BluezBleAdapter`（公开）→ `Bluez/` 下全是 internal：`IBluezBus`（抽象）、`TmdsBluezBus`（真实实现）、
  `BluezScanner`、`BluezConnector`、`BluezConnection`、`BluezNotifyStream`、`BluezObjectModel`、`BluezErrorMapper`、`BluezVariantConverter` 等。
* 单元测试用内存里的 `FakeBluezBus`（`tests/ScaleBridge.Linux.Tests`）。
* `TmdsBluezBus` 另有集成测试：在一个私有 `dbus-daemon`（`--session` 配置、临时套接字）上挂一个用 Tmds.DBus.Protocol 手写的假 `org.bluez`，
  走真实的线上编码/解码。找不到 `dbus-daemon`（例如 Windows CI）时这些测试标记为**跳过**。

## 没有验证过的假设（真机上最可能出问题的地方）

见仓库根目录 `NOTES.md` 的「Linux 适配器与 D-Bus 库评估（时尚1）」一节。
