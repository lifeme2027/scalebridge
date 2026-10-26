using ScaleBridge.Core.Ble;
using ScaleBridge.Core.Models;

namespace ScaleBridge.App.Services;

public interface IDialogService
{
    /// <summary>确认对话框；true = 用户确认。</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText);

    /// <summary>选择保存位置；用户取消返回 null。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);
}

public interface IUiDispatcher
{
    /// <summary>把工作排到 UI 线程（顺序执行）。</summary>
    void Post(Action action);
}

public interface IAdapterFactory
{
    /// <summary>本机可选的适配器 id（Fake 永远在第一位；Windows 版额外提供 "windows" 真实蓝牙）。</summary>
    IReadOnlyList<string> AvailableAdapterIds { get; }

    /// <summary>当前设置对应的适配器；本机不可用的适配器（例如 Linux 版没有真实适配器）返回 null。</summary>
    IBleAdapter? Create(AppSettings settings);
}
