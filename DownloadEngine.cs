using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace DriveDownload;

internal sealed class Transfer(RemoteFile remote, string relative, string? export = null, string? problem = null)
{
    public RemoteFile Remote { get; } = remote;
    public string Relative { get; } = relative;
    public string? Export { get; } = export;
    public long Size => Export is null ? Math.Max(0, Remote.Length) : 0;
    public long Bytes;
    public volatile string Status = "В очереди";
    public volatile string? Error = problem;
    public volatile bool Done;
    public DateTimeOffset RetryAt;
    public int Attempts;
    public int InvalidChecksums;
}

internal sealed class DownloadEngine(GoogleDrive drive, Job job)
{
    private volatile Transfer[] entries = [];
    private string cache = "";
    public Transfer[] Entries => entries;
    public string? LocalFolder { get; private set; }
    public volatile string Message = "Читаем папку Google Drive…";
    private long received;
    public long Received => Interlocked.Read(ref received);

    public async Task Run(CancellationToken ct)
    {
        var link = FolderLink.Parse(job.Link);
        if (!Guid.TryParseExact(job.CacheId, "N", out _)) throw new IOException("Не удалось открыть кэш задания.");
        var root = Path.GetFullPath(job.Destination);
        var selection = job.SelectedKeys?.ToHashSet(StringComparer.Ordinal);
        if (selection is { Count: 0 })
            throw new DriveFailure("Отметьте файлы или папки для скачивания.");

        var catalog = await ReadCatalog(link, ct);
        var chosen = catalog.Flatten().Where(x => selection is null || selection.Contains(x.Key)).ToArray();
        var missing = selection is null ? 0 : selection.Count - chosen.Length;
        if (missing > 0 && chosen.Length == 0)
            throw new DriveFailure("Отмеченных объектов больше нет в папке Google Drive.");

        EnsureDirectory(root);
        cache = Path.Combine(root, DriveCatalog.CacheDirectory, job.CacheId);
        EnsureDirectory(cache);
        LocalFolder = Path.Combine(root, catalog.LocalName);
        EnsureDirectory(LocalFolder);

        var downloads = new List<Transfer>();
        foreach (var item in chosen)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Problem is not null)
                downloads.Add(new(item.Remote, item.Relative, item.Export, item.Problem) { Status = "Ошибка" });
            else if (!item.IsFolder)
                downloads.Add(new(item.Remote, item.Relative, item.Export));
            else if (item.Children.Length == 0)
            {
                try { EnsureDirectory(Path.Combine(LocalFolder, item.Relative)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    downloads.Add(new(item.Remote, item.Relative, problem: e.Message) { Status = "Ошибка" });
                }
            }
        }
        entries = [.. downloads];
        Message = "Сверяем локальные файлы…";
        foreach (var item in entries.Where(x => x.Error is null))
        {
            ct.ThrowIfCancellationRequested();
            try { await Prepare(item, ct); }
            catch (IOException e) { Fail(item, e.Message); }
            catch (UnauthorizedAccessException e) { Fail(item, e.Message); }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = entries.Where(x => !x.Done && x.Error is null).ToArray();
            if (pending.Length == 0)
            {
                Message = missing > 0
                    ? $"Завершено. Отмеченных объектов, которых больше нет в папке: {missing}."
                    : entries.Any(x => x.Error is not null)
                    ? "Завершено с ошибками. Подробности в списке."
                    : "Загрузка завершена.";
                return;
            }
            var ready = pending.Where(x => x.RetryAt <= DateTimeOffset.Now).ToArray();
            if (ready.Length == 0)
            {
                var next = pending.MinBy(x => x.RetryAt)!;
                Message = $"{next.Status} Повтор в {next.RetryAt.LocalDateTime:HH:mm:ss}.";
                var wait = next.RetryAt - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                continue;
            }

            Message = "Получаем файлы с Google Drive";
            await Parallel.ForEachAsync(ready, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (item, token) =>
                {
                    try { await Download(item, token); }
                    catch (DriveFailure e) when (e.Reconnect) { throw; }
                    catch (Exception e) when (!token.IsCancellationRequested && IsTransient(e))
                    {
                        item.RetryAt = DateTimeOffset.Now + RetryDelay(e, ++item.Attempts);
                        item.Status = Describe(e);
                    }
                    catch (Exception e) when (!token.IsCancellationRequested &&
                        e is IOException or UnauthorizedAccessException or DriveFailure)
                    {
                        Fail(item, e.Message);
                    }
                });
        }
    }

    private async Task<FolderCatalog> ReadCatalog(FolderLink link, CancellationToken ct)
    {
        var source = new DriveCatalog(drive);
        var tries = 0;
        while (true)
        {
            try
            {
                Message = "Читаем папку Google Drive…";
                return await source.Read(link, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && IsTransient(e))
            {
                var pause = RetryDelay(e, ++tries);
                Message = $"{Describe(e)} Повтор в {DateTime.Now.Add(pause):HH:mm:ss}.";
                await Task.Delay(pause, ct);
            }
        }
    }
    private async Task Prepare(Transfer item, CancellationToken ct)
    {
        var (target, part, receipt) = Paths(item);
        EnsureDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            CheckLink(target);
            CheckLinkIfPresent(receipt);
            if (item.Export is not null && File.Exists(receipt) &&
                (await File.ReadAllTextAsync(receipt, ct)).Equals(await Hash(target, ct), StringComparison.OrdinalIgnoreCase) ||
                item.Export is null && await Matches(item, target, ct))
            {
                item.Bytes = item.Size;
                item.Status = "Уже на диске";
                item.Done = true;
                return;
            }
            Fail(item, "В папке есть файл с этим именем и другим содержимым. Выберите другую папку или переместите локальный файл.");
            return;
        }

        if (File.Exists(part))
        {
            CheckLink(part);
            if (item.Export is null)
            {
                var length = new FileInfo(part).Length;
                if (length <= item.Size) item.Bytes = length;
            }
        }
    }

    private async Task Download(Transfer item, CancellationToken ct)
    {
        var (target, part, receipt) = Paths(item);
        EnsureDirectory(Path.GetDirectoryName(target)!);
        CheckLinkIfPresent(part);
        if (File.Exists(target))
        {
            await Prepare(item, ct);
            return;
        }
        var offset = item.Export is null && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > item.Size) offset = 0;

        if (item.Export is not null || offset != item.Size || !File.Exists(part))
        {
            item.Status = offset > 0 ? "Продолжаем с " + BytesFormat(offset) : "Скачиваем";
            using var response = await drive.Content(item.Remote, item.Export, offset, ct);
            var content = response.Content;
            if (content.Headers.TryGetValues("Content-Encoding", out var encodings) &&
                encodings.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new DriveFailure("Google вернул неожиданное кодирование файла.");
            if (string.Equals(content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) &&
                (item.Export is not null || !item.Remote.MimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
                throw new DriveFailure("Drive вернул веб-страницу вместо файла.", retry: true);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = content.Headers.ContentRange;
                if (item.Export is not null || range?.From != offset || range.Length != item.Size ||
                    range.To is not long last || last < offset || last >= item.Size ||
                    content.Headers.ContentLength is long declared && declared != last - offset + 1)
                    throw new DriveFailure("Google вернул неверный диапазон байтов.", retry: true);
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (item.Export is null && content.Headers.ContentLength is long declared && declared != item.Size)
                    throw new DriveFailure("Размер ответа Drive отличается от размера файла.");
                offset = 0;
            }
            else
            {
                throw new DriveFailure("Drive больше не принимает сохранённое смещение. Обновите список папки.");
            }
            item.Bytes = offset;
            var length = item.Export is not null ? content.Headers.ContentLength :
                response.StatusCode == HttpStatusCode.PartialContent ? content.Headers.ContentRange!.To!.Value - offset + 1 : item.Size;
            await Receive(item, response, part, offset, length, ct);
            if (item.Export is null && new FileInfo(part).Length < item.Size)
                throw new DriveFailure("Drive передал часть файла. Продолжаем с сохранённого места.", retry: true);
        }

        item.Status = "Проверяем файл";
        if (item.Export is null && !await Matches(item, part, ct))
        {
            File.Delete(part);
            item.Bytes = 0;
            var retry = ++item.InvalidChecksums < 3;
            throw new DriveFailure("Проверка файла не прошла. Drive мог изменить исходный файл.", retry: retry);
        }
        if (item.Export is not null)
        {
            CheckLinkIfPresent(receipt);
            await File.WriteAllTextAsync(receipt, await Hash(part, ct), ct);
        }
        CheckLinkIfPresent(target);
        File.Move(part, target);
        item.Status = "Готово";
        item.Done = true;
    }

    private async Task Receive(Transfer item, HttpResponseMessage response, string part, long offset, long? count,
        CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(part, offset > 0 ? FileMode.Open : FileMode.Create,
            FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        file.Position = offset;
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                int read;
                try { read = await body.ReadAsync(rented.AsMemory(), timeout.Token); }
                catch (IOException) { throw new DriveFailure("Связь с Drive прервалась.", retry: true); }
                if (read == 0) break;
                if (count is long max && total + read > max)
                {
                    file.SetLength(offset);
                    item.Bytes = offset;
                    throw new DriveFailure("Drive прислал больше данных, чем ожидалось.");
                }
                await file.WriteAsync(rented.AsMemory(0, read), ct);
                total += read;
                Interlocked.Add(ref received, read);
                Interlocked.Exchange(ref item.Bytes, offset + total);
            }
            if (count is long required && required != total)
                throw new DriveFailure("Связь с Drive прервалась.", retry: true);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    private (string Target, string Partial, string Receipt) Paths(Transfer item)
    {
        var target = Path.Combine(LocalFolder!, item.Relative);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', item.Remote.Id, item.Remote.Version, item.Remote.Md5Checksum, item.Remote.Size,
                item.Relative, item.Export))));
        return (target, Path.Combine(cache, token + ".part"), Path.Combine(cache, token + ".hash"));
    }

    private static async Task<bool> Matches(Transfer item, string path, CancellationToken ct) =>
        new FileInfo(path).Length == item.Size && (string.IsNullOrEmpty(item.Remote.Md5Checksum) ||
            item.Remote.Md5Checksum.Equals(await Hash(path, ct), StringComparison.OrdinalIgnoreCase));

    private static async Task<string> Hash(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await MD5.HashDataAsync(file, ct));
    }

    private static void EnsureDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) EnsureDirectory(parent);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        CheckLink(path);
    }

    private static void CheckLinkIfPresent(string path)
    {
        if (Path.Exists(path)) CheckLink(path);
    }

    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Путь содержит ссылку или точку подключения: {path}");
    }

    private static void Fail(Transfer item, string message)
    {
        item.Error = message;
        item.Status = "Ошибка";
    }

    private static bool IsTransient(Exception e) => e is DriveFailure { Retry: true } or HttpRequestException
        or OperationCanceledException or System.Text.Json.JsonException;

    private static TimeSpan RetryDelay(Exception e, int attempt)
    {
        var delay = TimeSpan.FromSeconds(e is DriveFailure { Quota: true } ? 180 : Math.Min(60, 4 * (1 << Math.Min(4, attempt - 1))));
        return e is DriveFailure { Delay: TimeSpan server } && server > delay ? server : delay;
    }

    private static string Describe(Exception e) => e is DriveFailure ? e.Message :
        "Связь с Google Drive прервана.";

    public static string BytesFormat(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo("ru-RU"))} {units[unit]}";
    }
}