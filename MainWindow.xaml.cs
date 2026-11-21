using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DriveDownload;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Row> rows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Credentials? credentials;
    private Job? job;
    private CancellationTokenSource? operation;
    private DownloadEngine? engine;
    private Transfer[]? displayedEntries;
    private long lastReceived;
    private DateTimeOffset lastTick;
    private double avgSpeed;
    private bool closing;
    private bool connecting;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
        Queue.ItemsSource = rows;
        Link.TextChanged += (_, _) => UpdateStartLabel();
        Destination.TextChanged += (_, _) => UpdateStartLabel();
        timer.Tick += (_, _) => UpdateProgress();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            credentials = State.ReadCredentials();
            job = State.ReadJob();
        }
        catch (Exception)
        {
            StatusBar.Text = "Сохранённые настройки не открылись. Проверьте подключение и папку.";
        }
        Link.Text = job?.Link ?? "";
        Destination.Text = job?.Destination ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        SetAccount();
        if (job is { RunOnOpen: true } && credentials?.Ready == true) await StartJob(job);
        else if (job is { RunOnOpen: true })
            StatusBar.Text = "Подключите Google, чтобы продолжить сохранённую загрузку.";
    }

    private void SetAccount()
    {
        Account.Content = credentials?.RefreshToken is not null ? "Google подключён" :
            credentials?.ApiKey is not null ? "Доступ по ключу API" : "Подключить Google";
        Account.ToolTip = "Параметры доступа к Google Drive";
        Account.Tag = FindResource(credentials?.Ready == true ? "CheckIcon" : "LinkIcon");
    }

    private async void AccountClick(object sender, RoutedEventArgs e) => await Configure();

    private async Task<bool> Configure()
    {
        if (operation is not null || connecting) return false;
        var dialog = new ConnectionWindow { Owner = this };
        if (dialog.ShowDialog() != true) return false;
        using var stop = new CancellationTokenSource();
        operation = stop;
        connecting = true;
        SetBusy(true);
        StatusBar.Text = dialog.CredentialFile is null ?
            "Сохраняем ключ API…" : "Завершите вход в Google в браузере.";
        try
        {
            var value = dialog.CredentialFile is string file
                ? await GoogleLogin.Connect(file, stop.Token)
                : new Credentials(null, null, null, dialog.ApiKey);
            State.WriteCredentials(value);
            credentials = value;
            SetAccount();
            StatusBar.Text = "Подключение сохранено.";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusBar.Text = "Подключение отменено или время входа истекло.";
        }
        catch (DriveFailure ex) { StatusBar.Text = ex.Message; }
        catch (ArgumentException ex) { StatusBar.Text = ex.Message; }
        catch (Exception) { StatusBar.Text = "Не удалось прочитать настройки или связаться с Google."; }
        finally
        {
            operation = null;
            connecting = false;
            SetBusy(false);
            if (closing) Close();
        }
        return false;
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для загрузок", Multiselect = false };
        if (Directory.Exists(Destination.Text)) dialog.InitialDirectory = Destination.Text;
        if (dialog.ShowDialog(this) == true) Destination.Text = dialog.FolderName;
    }

    private async void StartClick(object sender, RoutedEventArgs e) => await ChooseAndStart(preview: false);

    private async void ChooseFilesClick(object sender, RoutedEventArgs e) => await ChooseAndStart(preview: true);

    private async Task ChooseAndStart(bool preview)
    {
        if (operation is not null || closing) return;
        var link = Link.Text.Trim();
        FolderLink source;
        try { source = FolderLink.Parse(link); }
        catch (ArgumentException ex) { StatusBar.Text = ex.Message; return; }

        if (credentials?.Ready != true && !await Configure()) return;
        if (closing) return;
        string[]? selected = null;
        if (preview)
        {
            var previous = job is not null && SameFolder(job.Link, link) ? job.SelectedKeys : null;
            var picker = new SelectionWindow(source, credentials!, previous) { Owner = this };
            if (picker.ShowDialog() != true || closing) return;
            selected = picker.SelectedKeys;
        }

        if (!TryDestination(out var path))
        {
            StatusBar.Text = "Выберите папку назначения по полному пути.";
            Destination.Focus();
            return;
        }
        var resume = IsSavedLocation(link, path);
        if (!preview && resume) selected = job!.SelectedKeys;
        var next = new Job(link, path, resume ? job!.CacheId : Guid.NewGuid().ToString("N"), true, selected);
        try { State.WriteJob(next); }
        catch (Exception) { StatusBar.Text = "Windows не смог сохранить задание."; return; }
        job = next;
        await StartJob(next);
    }

    private bool TryDestination(out string path)
    {
        path = "";
        var value = Destination.Text.Trim();
        if (!Path.IsPathFullyQualified(value)) return false;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); return true; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private bool IsSavedLocation(string link, string destination) =>
        job is not null && SameFolder(job.Link, link) &&
        string.Equals(Path.TrimEndingDirectorySeparator(job.Destination), destination, StringComparison.OrdinalIgnoreCase);

    private void UpdateStartLabel()
    {
        var resume = TryDestination(out var path) && IsSavedLocation(Link.Text, path);
        Start.Content = !resume ? "Скачать всё" : job!.SelectedKeys is null ?
            "Скачать / продолжить" : "Продолжить выбранное";
    }
    private static bool SameFolder(string previous, string current)
    {
        try { return FolderLink.Parse(previous).Id == FolderLink.Parse(current).Id; }
        catch (ArgumentException) { return false; }
    }

    private async Task StartJob(Job next)
    {
        using var stop = new CancellationTokenSource();
        using var drive = new GoogleDrive(credentials!);
        operation = stop;
        var task = new DownloadEngine(drive, next);
        engine = task;
        displayedEntries = null;
        rows.Clear();
        lastReceived = 0;
        lastTick = DateTimeOffset.Now;
        avgSpeed = 0;
        SetBusy(true);
        UpdateProgress();
        timer.Start();
        string? result = null;
        try
        {
            await Task.Run(() => task.Run(stop.Token), stop.Token);
            StorePaused();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            result = "Пауза. Полученные данные сохранены.";
        }
        catch (DriveFailure ex)
        {
            result = ex.Message;
            StorePaused();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = ex.Message;
            StorePaused();
        }
        catch (Exception)
        {
            result = "Ошибка загрузчика. Сохранённые данные можно использовать при повторном запуске.";
            StorePaused();
        }
        finally
        {
            timer.Stop();
            operation = null;
            UpdateProgress();
            if (result is not null) StatusBar.Text = result;
            SetBusy(false);
            if (closing) Close();
        }
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (operation is null) return;
        if (!connecting) StorePaused();
        operation.Cancel();
        Pause.IsEnabled = false;
    }

    private void StorePaused()
    {
        if (job is null) return;
        var saved = job with { RunOnOpen = false };
        try { State.WriteJob(saved); job = saved; }
        catch (Exception) { }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (operation is null) return;
        closing = true;
        e.Cancel = true;
        operation.Cancel();
    }

    private void SetBusy(bool busy)
    {
        var showActive = busy && !connecting;
        SetupPanel.Visibility = showActive ? Visibility.Collapsed : Visibility.Visible;
        ActivePanel.Visibility = showActive ? Visibility.Visible : Visibility.Collapsed;
        Start.IsEnabled = !busy;
        ChooseFiles.IsEnabled = !busy;
        UpdateStartLabel();
        Pause.IsEnabled = busy;
        Pause.Content = connecting ? "Отменить вход" : "Пауза";
        Pause.Tag = FindResource(connecting ? "CloseIcon" : "PauseIcon");
        Browse.IsEnabled = !busy;
        Link.IsReadOnly = busy;
        Destination.IsReadOnly = busy;
        Account.IsEnabled = !busy;
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        var path = engine?.LocalFolder ?? Destination.Text;
        if (Directory.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception) { StatusBar.Text = "Windows не смог открыть папку."; }
        }
    }

    private void UpdateProgress()
    {
        if (engine is null) return;
        var files = engine.Entries;
        if (!ReferenceEquals(files, displayedEntries))
        {
            rows.Clear();
            foreach (var file in files) rows.Add(new Row(file));
            displayedEntries = files;
        }
        foreach (var row in rows) row.Update();
        var finished = files.Count(x => x.Done);
        var errors = files.Count(x => x.Error is not null);
        var total = files.Sum(x => x.Size);
        var completed = files.Sum(x => Math.Min(x.Size, Interlocked.Read(ref x.Bytes)));
        var now = DateTimeOffset.Now;
        var bytes = engine.Received;
        var seconds = Math.Max(.1, (now - lastTick).TotalSeconds);
        avgSpeed = ((bytes - lastReceived) / seconds + avgSpeed) / 2;
        lastReceived = bytes;
        lastTick = now;

        var allDone = files.Length > 0 && finished == files.Length;
        var progress = total > 0 ? (double)completed / total * 100 :
            files.Length > 0 ? (double)finished / files.Length * 100 : 0;
        Overall.Value = allDone ? 100 : Math.Clamp(progress, 0, 99);
        var format = Overall.Value is > 0 and < 10 ? "0.0" : "0";
        Percent.Text = Overall.Value.ToString(format, CultureInfo.GetCultureInfo("ru-RU")) + "%";
        Summary.Text = $"{DownloadEngine.BytesFormat(completed)} из {DownloadEngine.BytesFormat(total)}";
        if (operation is not null && bytes > 0 && avgSpeed >= 1024)
        {
            var speed = $"{DownloadEngine.BytesFormat((long)avgSpeed)}/с";
            var remainingSeconds = Math.Max(0, total - completed) / avgSpeed;
            TransferRate.Text = remainingSeconds >= 1 && remainingSeconds < TimeSpan.FromDays(7).TotalSeconds
                ? $"{speed}  ·  осталось {FormatRemaining(remainingSeconds)}"
                : speed;
        }
        else
        {
            TransferRate.Text = allDone ? "Передача завершена" : "Скорость появится при загрузке";
        }
        QueueSummary.Text = files.Length > 0
            ? $"{finished} из {files.Length} файлов" + (errors > 0 ? $"  ·  Ошибок: {errors}" : "")
            : "Очередь пуста";
        Empty.Visibility = files.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivePath.Text = engine.LocalFolder ?? job?.Destination;
        OpenFolder.IsEnabled = Directory.Exists(engine.LocalFolder);
        StatusBar.Text = engine.Message;
    }

    private static string FormatRemaining(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} ч {time.Minutes:D2} мин" :
            time.TotalMinutes >= 1 ? $"{time.Minutes} мин {time.Seconds:D2} с" : $"{time.Seconds} с";
    }
    private sealed class Row(Transfer file) : INotifyPropertyChanged
    {
        public string Path => file.Relative;
        public string FileKind { get; } = FileKinds.Classify(file.Remote.MimeType, file.Relative);
        public string Amount => file.Export is null
            ? $"{DownloadEngine.BytesFormat(Interlocked.Read(ref file.Bytes))} / {DownloadEngine.BytesFormat(file.Size)}"
            : DownloadEngine.BytesFormat(Interlocked.Read(ref file.Bytes));
        public string Status => file.Error ?? file.Status;
        public string Details => Status;
        public double Progress => file.Done ? 100 : file.Size > 0
            ? Math.Min(99, Interlocked.Read(ref file.Bytes) * 100.0 / file.Size)
            : 0;
        public Brush Tone => BrushFor("Muted");
        public Brush ProgressTone => BrushFor("Accent");

        private Brush BrushFor(string normal) => (Brush)Application.Current.FindResource(
            file.Error is not null ? "ErrorBrush" :
            file.Done ? "SuccessBrush" :
            Status.StartsWith("Google Drive", StringComparison.Ordinal) ||
            Status.StartsWith("Связь", StringComparison.Ordinal) ? "WarningBrush" : normal);
        public event PropertyChangedEventHandler? PropertyChanged;
        private string? amount;
        private string? status;
        private double progress = -1;
        private Brush? tone;

        public void Update()
        {
            var nextAmount = Amount;
            var nextStatus = Status;
            if (amount != nextAmount)
            {
                amount = nextAmount;
                PropertyChanged?.Invoke(this, new(nameof(Amount)));
            }
            if (status != nextStatus)
            {
                status = nextStatus;
                PropertyChanged?.Invoke(this, new(nameof(Status)));
                PropertyChanged?.Invoke(this, new(nameof(Details)));
            }
            var nextProgress = Progress;
            if (nextProgress != progress)
            {
                progress = nextProgress;
                PropertyChanged?.Invoke(this, new(nameof(Progress)));
            }
            var nextTone = Tone;
            if (!ReferenceEquals(tone, nextTone))
            {
                tone = nextTone;
                PropertyChanged?.Invoke(this, new(nameof(Tone)));
                PropertyChanged?.Invoke(this, new(nameof(ProgressTone)));
            }
        }
    }
}

internal sealed class ConnectionWindow : Window
{
    public string? CredentialFile { get; private set; }
    public string? ApiKey { get; private set; }

    public ConnectionWindow()
    {
        Title = "Подключение к Google Drive";
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock
        {
            Text = "Вход в Google Drive", FontSize = 23, FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Для папок вашего аккаунта выберите JSON клиента OAuth Desktop app. Вход откроется в браузере. Приложение запросит доступ к Drive только для чтения.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 15)
        });
        var oauth = new Button { Content = "Выбрать JSON и войти", Tag = FindResource("FolderIcon"), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left };
        oauth.Click += (_, _) =>
        {
            var file = new OpenFileDialog { Title = "OAuth-клиент Google для Desktop app", Filter = "JSON (*.json)|*.json" };
            if (file.ShowDialog(this) != true) return;
            CredentialFile = file.FileName;
            DialogResult = true;
        };
        panel.Children.Add(oauth);
        panel.Children.Add(new TextBlock
        {
            Text = "Для папок с публичным доступом можно использовать ключ Google Drive API.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 23, 0, 8)
        });
        var key = new PasswordBox { Padding = new Thickness(8), MinHeight = 35 };
        System.Windows.Automation.AutomationProperties.SetName(key, "Ключ Google Drive API");
        panel.Children.Add(key);
        var apply = new Button { Content = "Использовать ключ API", Tag = FindResource("KeyIcon"), IsEnabled = false, Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        key.PasswordChanged += (_, _) => apply.IsEnabled = System.Text.RegularExpressions.Regex.IsMatch(
            key.Password.Trim(), "^[A-Za-z0-9_-]{20,100}$");
        apply.Click += (_, _) => { ApiKey = key.Password.Trim(); DialogResult = true; };
        panel.Children.Add(apply);
    }
}