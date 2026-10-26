using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace ScaleBridge.App.Localization;

/// <summary>XAML 写法：<c>Text="{loc:T Tab.Devices}"</c>，展开为对 <see cref="Loc.Instance"/> 索引器的单向绑定，语言切换时自动刷新。</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key) => Key = key;

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
