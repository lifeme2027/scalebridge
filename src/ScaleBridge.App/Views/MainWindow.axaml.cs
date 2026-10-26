using Avalonia.Controls;
using Avalonia.Input;
using ScaleBridge.App.ViewModels;

namespace ScaleBridge.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>键盘操作：F5 扫描，Esc 停止扫描，Ctrl+1..4 切换标签页。其余控件都可用 Tab / 方向键 / 空格 / 回车操作。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Key == Key.F5) { _ = vm.StartScanAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.StopScan(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D4)
        {
            vm.SelectedTab = e.Key - Key.D1;
            e.Handled = true;
        }
    }
}
