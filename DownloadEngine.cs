using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

using static DriveDownload.L;

namespace DriveDownload;

internal sealed class Transfer(RemoteFile remote, string relative, string? export = null, Phrase? problem = null)
{
    public RemoteFile Remote { get; } = remote;
    public string Relative { get; } = relative;
    public string? Export { get; } = export;
    public long Size => Export is null ? Math.Max(0, Remote.Length) : 0;
    public long Bytes;
    public volatile Phrase Status = PhraseFor("Queued");
    public volatile Phrase? Error = problem;
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
    public volatile Phrase Message = PhraseFor("ReadingFolder");
    private long received;
    public long Received => Interlocked.Read(ref received);

    public async Task Run(CancellationToken ct)
    {
        var link = FolderLink.Parse(job.Link);
        if (!Guid.TryParseExact(job.CacheId, "N", out _)) throw new LocalizedIOException(PhraseFor("CacheFailed"));
        var root = Path.GetFullPath(job.Destination);
        var selection = job.SelectedKeys?.ToHashSet(StringComparer.Ordinal);
        if (selection is { Count: 0 })
            throw new DriveFailure(PhraseFor("SelectSomething"));

        var catalog = await ReadCatalog(link, ct);
        var chosen = catalog.Flatten().Where(x => selection is null || selection.Contains(x.Key)).ToArray();
        var missing = selection is null ? 0 : selection.Count - chosen.Length;
        if (missing > 0 && chosen.Length == 0)
            throw new DriveFailure(PhraseFor("SelectionMissing"));

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
                downloads.Add(new(item.Remote, item.Relative, item.Export, item.Problem) { Status = PhraseFor("Error") });
            else if (!item.IsFolder)
                downloads.Add(new(item.Remote, item.Relative, item.Export));
            else if (item.Children.Length == 0)
            {
                try { EnsureDirectory(Path.Combine(LocalFolder, item.Relative)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    downloads.Add(new(item.Remote, item.Relative, problem: Phrase.From(e)) { Status = PhraseFor("Error") });
                }
            }
        }
        entries = [.. downloads];
        Message = PhraseFor("CheckingLocal");
        foreach (var item in entries.Where(x => x.Error is null))
        {
            ct.ThrowIfCancellationRequested();
            try { await Prepare(item, ct); }
            catch (IOException e) { Fail(item, Phrase.From(e)); }
            catch (UnauthorizedAccessException e) { Fail(item, Phrase.From(e)); }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = entries.Where(x => !x.Done && x.Error is null).ToArray();
            if (pending.Length == 0)
            {
                Message = missing > 0
                    ? PhraseFor("CompletedMissing").Format(missing)
                    : entries.Any(x => x.Error is not null)
                    ? PhraseFor("CompletedWithErrors")
                    : PhraseFor("DownloadComplete");
                return;
            }
            var ready = pending.Where(x => x.RetryAt <= DateTimeOffset.Now).ToArray();
            if (ready.Length == 0)
            {
                var next = pending.MinBy(x => x.RetryAt)!;
                Message = PhraseFor("RetryAt").Format(next.Status, next.RetryAt.LocalDateTime);
                var wait = next.RetryAt - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                continue;
            }

            Message = PhraseFor("ReceivingFiles");
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
                        Fail(item, Phrase.From(e));
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
                Message = PhraseFor("ReadingFolder");
                return await source.Read(link, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && IsTransient(e))
            {
                var pause = RetryDelay(e, ++tries);
                Message = PhraseFor("RetryAt").Format(Describe(e), DateTime.Now.Add(pause));
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
                item.Status = PhraseFor("AlreadyLocal");
                item.Done = true;
                return;
            }
            Fail(item, PhraseFor("LocalConflict"));
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
            item.Status = offset > 0 ? PhraseFor("ResumingAt").Format(new Phrase(FormatBytes(offset, english: false), FormatBytes(offset, english: true))) : PhraseFor("Downloading");
            using var response = await drive.Content(item.Remote, item.Export, offset, ct);
            var content = response.Content;
            if (content.Headers.TryGetValues("Content-Encoding", out var encodings) &&
                encodings.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new DriveFailure(PhraseFor("UnknownEncoding"));
            if (string.Equals(content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) &&
                (item.Export is not null || !item.Remote.MimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
                throw new DriveFailure(PhraseFor("PageInsteadOfFile"), retry: true);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = content.Headers.ContentRange;
                if (item.Export is not null || range?.From != offset || range.Length != item.Size ||
                    range.To is not long last || last < offset || last >= item.Size ||
                    content.Headers.ContentLength is long declared && declared != last - offset + 1)
                    throw new DriveFailure(PhraseFor("BadRange"), retry: true);
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (item.Export is null && content.Headers.ContentLength is long declared && declared != item.Size)
                    throw new DriveFailure(PhraseFor("WrongResponseSize"));
                offset = 0;
            }
            else
            {
                throw new DriveFailure(PhraseFor("OffsetRejected"));
            }
            item.Bytes = offset;
            var length = item.Export is not null ? content.Headers.ContentLength :
                response.StatusCode == HttpStatusCode.PartialContent ? content.Headers.ContentRange!.To!.Value - offset + 1 : item.Size;
            await Receive(item, response, part, offset, length, ct);
            if (item.Export is null && new FileInfo(part).Length < item.Size)
                throw new DriveFailure(PhraseFor("PartialTransfer"), retry: true);
        }

        item.Status = PhraseFor("CheckingFile");
        if (item.Export is null && !await Matches(item, part, ct))
        {
            File.Delete(part);
            item.Bytes = 0;
            var retry = ++item.InvalidChecksums < 3;
            throw new DriveFailure(PhraseFor("FileCheckFailed"), retry: retry);
        }
        if (item.Export is not null)
        {
            CheckLinkIfPresent(receipt);
            await File.WriteAllTextAsync(receipt, await Hash(part, ct), ct);
        }
        CheckLinkIfPresent(target);
        File.Move(part, target);
        item.Status = PhraseFor("Ready");
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
                catch (IOException) { throw new DriveFailure(PhraseFor("DriveInterrupted"), retry: true); }
                if (read == 0) break;
                if (count is long max && total + read > max)
                {
                    file.SetLength(offset);
                    item.Bytes = offset;
                    throw new DriveFailure(PhraseFor("TooManyBytes"));
                }
                await file.WriteAsync(rented.AsMemory(0, read), ct);
                total += read;
                Interlocked.Add(ref received, read);
                Interlocked.Exchange(ref item.Bytes, offset + total);
            }
            if (count is long required && required != total)
                throw new DriveFailure(PhraseFor("DriveInterrupted"), retry: true);
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
            throw new LocalizedIOException(PhraseFor("ReparsePoint").Format(path));
    }

    private static void Fail(Transfer item, Phrase message)
    {
        item.Error = message;
        item.Status = PhraseFor("Error");
    }

    private static bool IsTransient(Exception e) => e is DriveFailure { Retry: true } or HttpRequestException
        or OperationCanceledException or System.Text.Json.JsonException;

    private static TimeSpan RetryDelay(Exception e, int attempt)
    {
        var delay = TimeSpan.FromSeconds(e is DriveFailure { Quota: true } ? 180 : Math.Min(60, 4 * (1 << Math.Min(4, attempt - 1))));
        return e is DriveFailure { Delay: TimeSpan server } && server > delay ? server : delay;
    }

    private static Phrase Describe(Exception e) => e is DriveFailure driveError ? driveError.Text :
        PhraseFor("ConnectionInterrupted");

    private static readonly string[] RussianUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
    private static readonly string[] EnglishUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string BytesFormat(long value) => FormatBytes(value, IsEnglish);

    private static string FormatBytes(long value, bool english)
    {
        var units = english ? EnglishUnits : RussianUnits;
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        var format = unit == 0 ? "0" : "0.#";
        return $"{size.ToString(format, english ? EnglishCulture : RussianCulture)} {units[unit]}";
    }
}
