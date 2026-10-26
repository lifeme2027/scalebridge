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
    /// <summary>当前设置对应的适配器；尚未实现（阶段 2 的 Windows / Linux）返回 null。</summary>
    IBleAdapter? Create(AppSettings settings);
}
