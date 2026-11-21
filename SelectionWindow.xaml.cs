using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace DriveDownload;

public partial class SelectionWindow : Window
{
    private readonly FolderLink link;
    private readonly Credentials credentials;
    private SelectionNode[] roots = [];
    private string[] selection;
    private CancellationTokenSource? reading;
    private bool closed;

    internal SelectionWindow(FolderLink link, Credentials credentials, string[]? previous)
    {
        this.link = link;
        this.credentials = credentials;
        selection = previous ?? [];
        InitializeComponent();
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
        Loaded += async (_, _) => await Read();
        Closed += (_, _) => { closed = true; reading?.Cancel(); };
    }

    internal string[] SelectedKeys => selection;

    private async Task Read()
    {
        if (reading is not null || closed) return;
        if (roots.Length > 0) selection = Chosen().Select(x => x.Item.Key).ToArray();
        using var stop = new CancellationTokenSource();
        reading = stop;
        Reload.IsEnabled = false;
        SelectAll.IsEnabled = false;
        SelectNone.IsEnabled = false;
        Accept.IsEnabled = false;
        Choice.Text = "Читаем список…";
        ContentsTree.ItemsSource = null;
        roots = [];
        Notice.Text = "Читаем папку Google Drive…";
        Notice.Visibility = Visibility.Visible;
        try
        {
            var catalog = await Task.Run(async () =>
            {
                using var drive = new GoogleDrive(credentials);
                return await new DriveCatalog(drive).Read(link, stop.Token);
            }, stop.Token);
            if (closed) return;

            FolderTitle.Text = catalog.Name;
            FolderTitle.ToolTip = catalog.Name;
            var previous = selection.ToHashSet(StringComparer.Ordinal);
            roots = catalog.Items.Select(x => new SelectionNode(x, null, previous, UpdateChoice)).ToArray();
            ContentsTree.ItemsSource = roots;
            Notice.Text = "Папка пуста";
            Notice.Visibility = roots.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) when (closed) { }
        catch (DriveFailure e) { Notice.Text = e.Message; }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Notice.Text = "Не удалось прочитать папку. Проверьте связь и нажмите «Обновить».";
        }
        finally
        {
            reading = null;
            if (!closed)
            {
                Reload.IsEnabled = true;
                UpdateChoice();
            }
        }
    }

    private async void ReloadClick(object sender, RoutedEventArgs e) => await Read();

    private void SelectAllClick(object sender, RoutedEventArgs e) => SetAll(true);
    private void SelectNoneClick(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var root in roots) root.SetTree(value);
        UpdateChoice();
    }

    private SelectionNode[] Chosen() => roots.SelectMany(x => x.CheckedLeaves()).ToArray();

    private void UpdateChoice()
    {
        if (reading is not null) return;
        var chosen = Chosen();
        var folders = chosen.Count(x => x.Item.IsFolder);
        var size = DownloadEngine.BytesFormat(chosen.Sum(x => x.Item.Size));
        var hasExports = chosen.Any(x => x.Item.Export is not null);
        Choice.Text = chosen.Length == 0 ? "Ничего не выбрано" :
            $"Файлов: {chosen.Length - folders}" + (folders > 0 ? $"   ·   Пустых папок: {folders}" : "") +
            (hasExports ? $"   ·   Известный объём: {size}" : $"   ·   {size}");
        SelectAll.IsEnabled = roots.Any(x => x.Selectable);
        SelectNone.IsEnabled = chosen.Length > 0;
        Accept.IsEnabled = chosen.Length > 0;
    }

    private void AcceptClick(object sender, RoutedEventArgs e)
    {
        var keys = Chosen().Select(x => x.Item.Key).ToArray();
        if (keys.Length == 0) return;
        selection = keys;
        DialogResult = true;
    }
}

public sealed class CatalogCheckBox : CheckBox
{
    protected override void OnToggle() => SetCurrentValue(IsCheckedProperty, IsChecked != true);
}

public sealed class SelectionNode : INotifyPropertyChanged
{
    private readonly SelectionNode? parent;
    private readonly Action changed;
    private bool? check;

    internal SelectionNode(CatalogItem item, SelectionNode? parent, HashSet<string> previous, Action changed)
    {
        Item = item;
        this.parent = parent;
        this.changed = changed;
        Children = item.Children.Select(x => new SelectionNode(x, this, previous, changed)).ToArray();
        Selectable = item.Problem is null && (Children.Length == 0 || Children.Any(x => x.Selectable));
        check = Children.Length == 0 ? Selectable && previous.Contains(item.Key) : Rollup();
    }

    internal CatalogItem Item { get; }
    public SelectionNode[] Children { get; }
    public string Name => Item.Name;
    public string Details => Item.Problem is not null ? $"{Item.Relative}\n{Item.Problem}" : Item.Relative;
    public string FileKind => Item.IsFolder ? "Folder" : FileKinds.Classify(Item.Remote.MimeType, Item.Relative);
    public bool Selectable { get; }
    public string SizeLabel => Item.Problem is not null ? "Недоступно" :
        Item.IsFolder || Item.Export is not null ? "" : DownloadEngine.BytesFormat(Item.Size);

    public bool? Checked
    {
        get => check;
        set
        {
            if (value is not bool selected || !Selectable) return;
            SetTree(selected);
            for (var node = parent; node is not null; node = node.parent)
                node.SetCheck(node.Rollup());
            changed();
        }
    }

    internal void SetTree(bool selected)
    {
        if (!Selectable) return;
        foreach (var child in Children) child.SetTree(selected);
        SetCheck(selected);
    }

    internal IEnumerable<SelectionNode> CheckedLeaves()
    {
        if (!Selectable || check == false) yield break;
        if (Children.Length == 0) yield return this;
        else
            foreach (var child in Children.SelectMany(x => x.CheckedLeaves())) yield return child;
    }

    private bool? Rollup()
    {
        var eligible = Children.Where(x => x.Selectable).ToArray();
        if (eligible.All(x => x.check == false)) return false;
        return eligible.All(x => x.check == true) ? true : null;
    }

    private void SetCheck(bool? value)
    {
        if (check == value) return;
        check = value;
        PropertyChanged?.Invoke(this, new(nameof(Checked)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}