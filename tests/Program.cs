using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DriveDownload;

var root = Path.Combine(Path.GetTempPath(), "drive-download-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var token = timeout.Token;
    var bytes = new byte[100_000];
    new Random(900).NextBytes(bytes);
    string hash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
    var mediaRequests = 0;
    var rangeSeen = false;
    var keepOffline = false;
    var keySeen = false;
    var selectionFixture = false;
    var selectedMedia = new List<string>();
    const int split = 8192;

    using var server = new TcpListener(IPAddress.Loopback, 0);
    server.Start();
    var port = ((IPEndPoint)server.LocalEndpoint).Port;
    var job = new Job("https://drive.google.com/drive/folders/folder_123?resourcekey=key_12345",
        root, Guid.NewGuid().ToString("N"), true);
    var serve = Task.Run(async () =>
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var peer = await server.AcceptTcpClientAsync(token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(token) ?? "";
                var fields = new List<string>();
                while (await reader.ReadLineAsync(token) is { Length: > 0 } line) fields.Add(line);
                if (selectionFixture)
                {
                    var query = Uri.UnescapeDataString(first);
                    string? result = query switch
                    {
                        var q when q.Contains("alt=media") => null,
                        var q when q.Contains("'chosen_dir' in parents") =>
                            $$"""{"files":[{"id":"chosen_file","name":"Выбранное.avi","mimeType":"video/avi","size":"{{bytes.Length}}","md5Checksum":"{{hash}}"},{"id":"other_file","name":"Второе.avi","mimeType":"video/avi","size":"{{bytes.Length}}"}]}""",
                        var q when q.Contains("'folder_123' in parents") =>
                            """{"files":[{"id":"chosen_dir","name":"Камера 1","mimeType":"application/vnd.google-apps.folder"},{"id":"empty_dir","name":"Пустая","mimeType":"application/vnd.google-apps.folder"},{"id":"skipped_dir","name":"Без выбора","mimeType":"application/vnd.google-apps.folder"}]}""",
                        var q when q.Contains("/files?") => "{\"files\":[]}",
                        _ => """{"id":"folder_123","name":"Выбор","mimeType":"application/vnd.google-apps.folder"}"""
                    };
                    if (result is null)
                    {
                        selectedMedia.Add(first.Split('?')[0].Split('/')[^1]);
                        await Write("200 OK", "application/octet-stream", bytes, bytes.Length, bytes.Length);
                    }
                    else
                    {
                        var json = Encoding.UTF8.GetBytes(result);
                        await Write("200 OK", "application/json", json, json.Length, json.Length);
                    }
                }
                else if (first.Contains("alt=media"))
                {
                    mediaRequests++;
                    keySeen |= fields.Contains("X-Goog-Drive-Resource-Keys: file_123/shared_key", StringComparer.OrdinalIgnoreCase);
                    if (mediaRequests == 1 || mediaRequests == 3)
                    {
                        await Write("200 OK", "application/octet-stream", bytes, bytes.Length, split);
                    }
                    else
                    {
                        if (keepOffline)
                        {
                            var error = Encoding.UTF8.GetBytes("{}");
                            await Write("503 Service Unavailable", "application/json", error, error.Length, error.Length);
                            continue;
                        }
                        rangeSeen = fields.Contains($"Range: bytes={split}-", StringComparer.OrdinalIgnoreCase);
                        await Write("206 Partial Content", "application/octet-stream", bytes[split..],
                            bytes.Length - split, bytes.Length - split,
                            $"Content-Range: bytes {split}-{bytes.Length - 1}/{bytes.Length}\r\n");
                    }
                }
                else
                {
                    var data = first.Contains("pageToken=more") ?
                        $$"""{"files":[{"id":"file_123","name":"CH05.avi","mimeType":"video/avi","size":"{{bytes.Length}}","md5Checksum":"{{hash}}","version":"1","resourceKey":"shared_key"}]}""" :
                        first.Contains("/files?") ? """{"files":[],"nextPageToken":"more"}""" :
                        """{"id":"folder_123","name":"20-11-2026","mimeType":"application/vnd.google-apps.folder"}""";
                    var json = Encoding.UTF8.GetBytes(data);
                    await Write("200 OK", "application/json", json, json.Length, json.Length);
                }

                async Task Write(string status, string mime, byte[] payload, int total, int sent, string extra = "")
                {
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {total}\r\n{extra}Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, token);
                    await stream.WriteAsync(payload.AsMemory(0, sent), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { } catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
    });
    using var transport = new HttpClient(new LoopbackHandler(port));
    var creds = new Credentials(null, null, null, "fixture");
    using var drive = new GoogleDrive(creds, transport);
    var engine = new DownloadEngine(drive, job);
    await engine.Run(token);
    var path = Path.Combine(root, "20-11-2026", "CH05.avi");
    Assert(File.ReadAllBytes(path).SequenceEqual(bytes), "downloaded bytes");
    Assert(rangeSeen && mediaRequests == 2, "automatic resumption from the byte after the interrupted response");
    Assert(keySeen, "resource key applied to media");
    Console.WriteLine("PASS paged listing, real interrupted HTTP response, Range retry, and final checksum");

    using var reopened = new GoogleDrive(creds, transport);
    await new DownloadEngine(reopened, job).Run(token);
    Assert(mediaRequests == 2, "existing matching file skipped");
    var changed = bytes.ToArray();
    changed[42] ^= 1;
    File.WriteAllBytes(path, changed);
    using var conflicting = new GoogleDrive(creds, transport);
    var conflict = new DownloadEngine(conflicting, job);
    await conflict.Run(token);
    Assert(conflict.Entries.Single().Error is not null && File.ReadAllBytes(path).SequenceEqual(changed),
        "existing same-size file with a different checksum is preserved");
    Console.WriteLine("PASS existing matching file skipped and same-size conflict preserved");

    File.Delete(path);
    keepOffline = true;
    using (var paused = CancellationTokenSource.CreateLinkedTokenSource(token))
    using (var interruptedDrive = new GoogleDrive(creds, transport))
    {
        var interrupted = new DownloadEngine(interruptedDrive, job with { SelectedKeys = ["file_123"] });
        var interruptedTask = interrupted.Run(paused.Token);
        while (interrupted.Entries.Sum(x => x.Bytes) < split) await Task.Delay(20, token);
        await paused.CancelAsync();
        try { await interruptedTask; } catch (OperationCanceledException) when (paused.IsCancellationRequested) { }
    }
    keepOffline = false;
    using (var restarted = new GoogleDrive(creds, transport))
        await new DownloadEngine(restarted, job with { SelectedKeys = ["file_123"] }).Run(token);
    Assert(rangeSeen && mediaRequests == 4 && File.ReadAllBytes(path).SequenceEqual(bytes),
        "resumption in a fresh engine using the persisted partial");
    Console.WriteLine("PASS cancellation and resumption in a new engine");

    using var quotaResponse = new HttpResponseMessage(HttpStatusCode.Forbidden);
    quotaResponse.Content = new StringContent("""{"error":{"errors":[{"reason":"downloadQuotaExceeded"}]}}""");
    quotaResponse.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
    var failure = await GoogleDrive.ParseFailure(quotaResponse, token);
    Assert(failure.Quota && failure.Retry && failure.Delay == TimeSpan.FromMinutes(5), "quota classification");
    Assert(FolderLink.Parse(job.Link).ResourceKey == "key_12345", "link parsing");
    try
    {
        FolderLink.Parse("https://drive.google.com.example.org/drive/folders/folder_123");
        throw new InvalidOperationException("Unexpected acceptance of a non-Drive URL.");
    }
    catch (ArgumentException) { }
    Console.WriteLine("PASS quota classification, Retry-After, and folder URL validation");

    var retryText = L.PhraseFor("RetryAt").Format(failure.Text, new DateTime(2026, 1, 1, 8, 30, 0));
    Assert(retryText.Current.Contains("Повтор в 08:30:00") && DownloadEngine.BytesFormat(1536) == "1,5 КБ",
        "Russian retry status and number format");
    L.IsEnglish = true;
    Assert(retryText.Current.Contains("Retrying at 08:30:00") &&
        failure.Message.StartsWith("Google Drive limited downloads") && DownloadEngine.BytesFormat(1536) == "1.5 KB",
        "already captured errors and retry times can be displayed in English");
    L.IsEnglish = false;
    Console.WriteLine("PASS live language selection for existing retry statuses and units");

    selectionFixture = true;
    var selectedFolder = Path.Combine(root, "выборочно");
    using (var preview = new GoogleDrive(creds, transport))
    {
        var catalog = await new DriveCatalog(preview).Read(FolderLink.Parse(job.Link), token);
        Assert(catalog.Items.Length == 3 && catalog.Flatten().Count(x => !x.IsFolder) == 2, "nested preview");
        Assert(selectedMedia.Count == 0 && !Directory.Exists(selectedFolder), "metadata-only preview");
    }
    var narrowed = job with
    {
        Destination = selectedFolder,
        SelectedKeys = ["chosen_dir/chosen_file", "empty_dir"]
    };
    var jsonJob = System.Text.Json.JsonSerializer.Serialize(narrowed);
    var restored = System.Text.Json.JsonSerializer.Deserialize<Job>(jsonJob)!;
    Assert(restored.SelectedKeys!.SequenceEqual(narrowed.SelectedKeys!), "persisted selection");
    using (var selective = new GoogleDrive(creds, transport))
        await new DownloadEngine(selective, restored).Run(token);

    var landing = Path.Combine(selectedFolder, "Выбор");
    Assert(selectedMedia.SequenceEqual(["chosen_file"]), "media fetched for selected file only");
    Assert(File.ReadAllBytes(Path.Combine(landing, "Камера 1", "Выбранное.avi")).SequenceEqual(bytes),
        "selected file keeps nesting");
    Assert(!File.Exists(Path.Combine(landing, "Камера 1", "Второе.avi")) && !Directory.Exists(Path.Combine(landing, "Без выбора")),
        "unchecked files and folders not created");
    Assert(Directory.Exists(Path.Combine(landing, "Пустая")), "selected empty folder preserved");
    var oldJob = System.Text.Json.Nodes.JsonNode.Parse(jsonJob)!.AsObject();
    oldJob.Remove(nameof(Job.SelectedKeys));
    Assert(System.Text.Json.JsonSerializer.Deserialize<Job>(oldJob.ToJsonString())!.SelectedKeys is null, "legacy job compatibility");
    Console.WriteLine("PASS metadata-only preview, persistent selective transfer, empty folder, and legacy jobs");

    await timeout.CancelAsync();
    await serve;
}
finally
{
    var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), temporary, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("drive-download-check-", StringComparison.Ordinal))
        Directory.Delete(root, recursive: true);
}

static void Assert(bool value, string name)
{
    if (!value) throw new InvalidOperationException("Check failed: " + name);
}

sealed class LoopbackHandler(int port) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.RequestUri = new Uri($"http://127.0.0.1:{port}" + request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, ct);
    }
}