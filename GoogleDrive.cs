using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace DriveDownload;

internal sealed class DriveFailure(string message, bool retry = false, bool quota = false,
    bool reconnect = false, TimeSpan? delay = null) : Exception(message)
{
    public bool Retry { get; } = retry;
    public bool Quota { get; } = quota;
    public bool Reconnect { get; } = reconnect;
    public TimeSpan? Delay { get; } = delay;
}

internal static class GoogleLogin
{
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(45) };
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Credentials> Connect(string file, CancellationToken ct)
    {
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(file, ct));
        if (!input.RootElement.TryGetProperty("installed", out var installed))
            throw new ArgumentException("Выберите JSON OAuth-клиента типа Desktop app.");
        var id = installed.TryGetProperty("client_id", out var client) ? client.GetString() : null;
        var secret = installed.TryGetProperty("client_secret", out var key) ? key.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new ArgumentException("В JSON отсутствует действительный client_id Google.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var verifier = RandomToken();
        var state = RandomToken();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/oauth";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = id, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/drive.readonly",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state
        };
        Process.Start(new ProcessStartInfo("https://accounts.google.com/o/oauth2/v2/auth?" +
            Query(parameters)) { UseShellExecute = true });

        string code;
        while (true)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            var request = new byte[8192];
            var used = 0;
            try
            {
                while (used < request.Length)
                {
                    var count = await stream.ReadAsync(request.AsMemory(used), attempt.Token);
                    if (count == 0) break;
                    used += count;
                    if (Encoding.ASCII.GetString(request, 0, used).Contains("\r\n\r\n")) break;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            var first = Encoding.ASCII.GetString(request, 0, used).Split("\r\n", 2)[0].Split(' ');
            if (first.Length != 3 || first[0] != "GET" ||
                !first[1].StartsWith("/oauth?", StringComparison.Ordinal)) continue;
            var query = HttpUtility.ParseQueryString(first[1][6..]);
            if (query["state"] != state) continue;
            var page = Encoding.UTF8.GetBytes("Вернитесь в приложение «Диск Загрузки».");
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {page.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token);
            await stream.WriteAsync(page, token);
            code = query["code"] ?? throw new DriveFailure("Вход Google не завершён. Повторите подключение.");
            break;
        }
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = id, ["code"] = code,
            ["redirect_uri"] = redirect, ["code_verifier"] = verifier
        };
        if (!string.IsNullOrEmpty(secret)) values["client_secret"] = secret;
        var response = await Token(values, token);
        if (string.IsNullOrEmpty(response.RefreshToken))
            throw new DriveFailure("Google не выдал разрешение для повторного входа. Подключите аккаунт ещё раз.");
        return new(id, secret, response.RefreshToken, null);
    }

    public static async Task<TokenResult> Token(Dictionary<string, string> values, CancellationToken ct)
    {
        using var payload = new FormUrlEncodedContent(values);
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                throw new DriveFailure("Google временно не обновляет подключение.", retry: true);
            throw new DriveFailure("Google отклонил подключение. Проверьте OAuth-клиент и войдите снова.",
                reconnect: true);
        }
        var result = await response.Content.ReadFromJsonAsync<TokenResult>(Json, ct);
        return !string.IsNullOrEmpty(result?.AccessToken) ? result :
            throw new DriveFailure("Google не вернул токен доступа.", reconnect: true);
    }

    public static string Query(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record TokenResult(
    [property:System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
    [property:System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
    [property:System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);

internal sealed record RemoteFile(string Id, string Name, string MimeType, string? Size, string? Md5Checksum,
    string? Version, string? ResourceKey, string? DriveId, DownloadCapabilities? Capabilities, ShortcutInfo? ShortcutDetails)
{
    public const string Folder = "application/vnd.google-apps.folder";
    public const string Shortcut = "application/vnd.google-apps.shortcut";
    public long Length => long.TryParse(Size, out var result) ? result : -1;
}

internal sealed record DownloadCapabilities(bool CanDownload);
internal sealed record ShortcutInfo(string TargetId, string? TargetResourceKey);
internal sealed record DrivePage(RemoteFile[]? Files, string? NextPageToken, bool IncompleteSearch);

internal sealed class GoogleDrive(Credentials credentials, HttpClient? transport = null) : IDisposable
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string Fields = "id,name,mimeType,size,md5Checksum,version,resourceKey,driveId,capabilities(canDownload),shortcutDetails(targetId,targetResourceKey)";
    private readonly HttpClient http = transport ?? GoogleLogin.Http;
    private readonly SemaphoreSlim tokenGate = new(1);
    private readonly Dictionary<string, string> resourceKeys = new(StringComparer.Ordinal);
    private string? bearer;
    private DateTimeOffset expiry;

    internal void AddResource(string id, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) resourceKeys[id] = key;
    }

    public async Task<RemoteFile> Get(string id, CancellationToken ct)
    {
        using var r = await Request("files/" + Uri.EscapeDataString(id), new()
        {
            ["fields"] = Fields, ["supportsAllDrives"] = "true"
        }, null, id, ct);
        return await r.Content.ReadFromJsonAsync<RemoteFile>(GoogleLogin.Json, ct)
            ?? throw new IOException("Drive вернул пустые сведения о файле.");
    }

    public async Task<List<RemoteFile>> Children(RemoteFile folder, CancellationToken ct)
    {
        List<RemoteFile> files = [];
        var query = new Dictionary<string, string>
        {
            ["q"] = $"'{folder.Id}' in parents and trashed = false", ["pageSize"] = "1000",
            ["fields"] = $"nextPageToken,incompleteSearch,files({Fields})",
            ["supportsAllDrives"] = "true", ["includeItemsFromAllDrives"] = "true"
        };
        if (folder.DriveId is not null)
        {
            query["corpora"] = "drive";
            query["driveId"] = folder.DriveId;
        }

        while (true)
        {
            using var r = await Request("files", query, null, folder.Id, ct);
            var page = await r.Content.ReadFromJsonAsync<DrivePage>(GoogleLogin.Json, ct)
                ?? throw new IOException("Drive вернул пустой список файлов.");
            if (page.IncompleteSearch) throw new DriveFailure("Google вернул неполный список папки. Повторяем запрос.", retry: true);
            files.AddRange(page.Files ?? []);
            if (string.IsNullOrEmpty(page.NextPageToken)) return files;
            query["pageToken"] = page.NextPageToken;
        }
    }

    public Task<HttpResponseMessage> Content(RemoteFile file, string? exportType, long offset, CancellationToken ct) =>
        exportType is null
            ? Request("files/" + Uri.EscapeDataString(file.Id), new() { ["alt"] = "media", ["supportsAllDrives"] = "true" }, offset > 0 ? offset : null, file.Id, ct)
            : Request("files/" + Uri.EscapeDataString(file.Id) + "/export", new() { ["mimeType"] = exportType }, null, file.Id, ct);

    private async Task<HttpResponseMessage> Request(string path, Dictionary<string, string> query, long? offset, string resourceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken) && !string.IsNullOrEmpty(credentials.ApiKey))
            query["key"] = credentials.ApiKey;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api + path + "?" + GoogleLogin.Query(query));
            var access = await AccessToken(ct);
            if (access is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (offset is long start) request.Headers.Range = new RangeHeaderValue(start, null);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (resourceKeys.TryGetValue(resourceId, out var key))
                request.Headers.TryAddWithoutValidation("X-Goog-Drive-Resource-Keys", resourceId + "/" + key);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode || (offset is not null && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                return response;
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && access is not null && attempt == 0)
                {
                    await tokenGate.WaitAsync(ct);
                    try { if (bearer == access) expiry = DateTimeOffset.MinValue; }
                    finally { tokenGate.Release(); }
                    continue;
                }

                throw await ParseFailure(response, ct);
            }
        }
    }

    internal static async Task<DriveFailure> ParseFailure(HttpResponseMessage response, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
            delay = date - DateTimeOffset.UtcNow;
        var code = (int)response.StatusCode;
        if (code == 429 || body.Contains("downloadQuotaExceeded", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase))
            return new("Google Drive ограничил скачивание. Ожидаем следующей попытки.", retry: true, quota: true, delay: delay);
        if (code >= 500 || code == 408)
            return new($"Google Drive временно недоступен (HTTP {code}).", retry: true, delay: delay);
        if (code == 401)
            return new("Подключите аккаунт Google заново.", reconnect: true);
        if (body.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase))
            return new("В проекте Google Cloud нужно включить Google Drive API.", reconnect: true);
        if (code == 404) return new("Папка или файл недоступны. Проверьте ссылку и аккаунт Google.");
        if (code == 403) return new("Google Drive запретил скачивание этого объекта. Проверьте доступ.");
        return new($"Google Drive отклонил запрос (HTTP {code}).");
    }

    private async Task<string?> AccessToken(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken)) return null;
        await tokenGate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow.AddMinutes(2) < expiry) return bearer;
            var values = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = credentials.ClientId!,
                ["refresh_token"] = credentials.RefreshToken
            };
            if (!string.IsNullOrEmpty(credentials.ClientSecret)) values["client_secret"] = credentials.ClientSecret;
            var refreshed = await GoogleLogin.Token(values, ct);
            expiry = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
            return bearer = refreshed.AccessToken;
        }
        finally { tokenGate.Release(); }
    }

    public void Dispose() => tokenGate.Dispose();
}