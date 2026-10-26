namespace ScaleBridge.Windows;

/// <summary>Windows 适配器的超时与容量参数。默认值取自任务约定的 8–10 秒看门狗。</summary>
public sealed record WindowsBleOptions
{
    /// <summary>ConnectAsync 未指定超时时的默认值（含打开设备）。</summary>
    public TimeSpan DefaultConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>单个 GATT 步骤（发现服务/特征、读、写 CCCD）的看门狗。</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(9);

    /// <summary>读取蓝牙无线电状态的看门狗。</summary>
    public TimeSpan AvailabilityTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>释放连接时，等待进行中的操作/订阅收尾的上限（与 Linux 适配器一致，2 秒）。</summary>
    public TimeSpan DisposeGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>订阅流的缓冲容量；消费者太慢时丢弃最旧的通知，不阻塞 WinRT 回调线程。</summary>
    public int NotificationBuffer { get; init; } = 256;

    /// <summary>扫描结果缓冲容量；满时丢最旧，不阻塞回调线程。</summary>
    public int ScanBuffer { get; init; } = 512;

    /// <summary>广播缓存最多记住的设备数。</summary>
    public int AdvertisementCacheCapacity { get; init; } = 1024;
}
