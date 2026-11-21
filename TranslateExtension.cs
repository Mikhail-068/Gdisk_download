using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace DriveDownload;

public sealed class TranslateExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = LocalizedView.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}

internal sealed class LocalizedView : INotifyPropertyChanged
{
    internal static LocalizedView Instance { get; } = new();

    public string this[string key] => L.Tr(key);

    internal void Refresh() => PropertyChanged?.Invoke(this, new("Item[]"));

    public event PropertyChangedEventHandler? PropertyChanged;
}
