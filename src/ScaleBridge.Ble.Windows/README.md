# ScaleBridge.Ble.Windows（阶段 2 占位，尚未实现）

这里还没有代码，也没有 `.csproj`：阶段 1 只交付演示数据适配器（`ScaleBridge.Ble.Fake`）。

计划（未开工、未验证）：

* 目标框架 `net8.0-windows10.0.19041`，用 WinRT `Windows.Devices.Bluetooth` / `GenericAttributeProfile` 实现
  `ScaleBridge.Core.Ble.IBleAdapter`。
* 扫描产出 `IBleAdapter` 约定的设备 id、名称、RSSI、服务 UUID、厂商数据；0x2A9D / 0x2A9C 是 Indicate，要订阅而不是读取。
* 错误映射到 `BleErrorKind`：蓝牙不可用、权限、超时、设备断开。
* 完成后仍然标「实验性（仅按规范实现，未真机验证）」，直到有人在真实体重秤上验证并记录结果。

界面「设置」里选择 Windows 适配器目前只会显示「阶段 2，尚未实现」。
Linux 一侧见 `src/ScaleBridge.Linux`（他人加入的 BlueZ 探索性脚手架，同样未接入界面、未真机验证）。
