using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ScaleBridge.App.Localization;

/// <summary>
/// 运行时可切换的界面文字。界面用 <c>{Binding [Key], Source={x:Static loc:Loc.Instance}}</c>，
/// 切换语言时发出 <c>Item</c> / <c>Item[]</c> 变更通知，所有绑定立即刷新。词典是嵌入的 zh.json / en.json（键一致由测试保证）。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private readonly IReadOnlyDictionary<string, string> _zh;
    private readonly IReadOnlyDictionary<string, string> _en;
    private bool _chinese;

    public Loc()
    {
        _zh = Load("zh");
        _en = Load("en");
        _chinese = IsSystemChinese();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsChinese => _chinese;
    public string LanguageCode => _chinese ? "zh" : "en";
    public IReadOnlyDictionary<string, string> Table(bool chinese) => chinese ? _zh : _en;

    public string this[string key] => Table(_chinese).TryGetValue(key, out var v) ? v : key;

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, this[key], args);

    public void SetChinese(bool chinese)
    {
        if (_chinese == chinese) return;
        _chinese = chinese;
        // Avalonia 的索引器绑定按「属性名 = 索引器所在属性 Item」判断是否刷新，WPF/其它按 Item[]，两个都发。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChinese)));
    }

    public static bool IsSystemChinese() =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var s = asm.GetManifestResourceStream($"Localization/{code}.json")
            ?? throw new InvalidOperationException($"Missing embedded resource Localization/{code}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s)
            ?? throw new InvalidOperationException($"Localization/{code}.json is empty");
    }
}
