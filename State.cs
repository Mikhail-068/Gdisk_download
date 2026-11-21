using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriveDownload;

internal sealed record Job(string Link, string Destination, string CacheId, bool RunOnOpen, string[]? SelectedKeys = null);
internal sealed record Credentials(string? ClientId, string? ClientSecret, string? RefreshToken, string? ApiKey)
{
    public bool Ready => !string.IsNullOrEmpty(RefreshToken) || !string.IsNullOrEmpty(ApiKey);
}

internal static class State
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "DriveDownload");
    private static readonly string JobPath = Path.Combine(Root, "job.json");
    private static readonly string CredentialsPath = Path.Combine(Root, "connection.bin");

    internal static Job? ReadJob() => File.Exists(JobPath)
        ? JsonSerializer.Deserialize<Job>(File.ReadAllBytes(JobPath)) : null;

    internal static Credentials? ReadCredentials() => File.Exists(CredentialsPath)
        ? JsonSerializer.Deserialize<Credentials>(Protect(File.ReadAllBytes(CredentialsPath), decrypt: true)) : null;

    internal static void WriteJob(Job job) => WriteAtomic(JobPath, JsonSerializer.SerializeToUtf8Bytes(job));

    internal static void WriteCredentials(Credentials credentials) =>
        WriteAtomic(CredentialsPath, Protect(JsonSerializer.SerializeToUtf8Bytes(credentials), decrypt: false));

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Root);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var input = new Blob { Size = bytes.Length, Data = ptr };
            var ok = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows не смог открыть защищённые данные подключения.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed record FolderLink(string Id, string? ResourceKey)
{
    private static readonly Regex Token = new("^[A-Za-z0-9_-]{6,256}$");

    public static FolderLink Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0)
            throw new ArgumentException("Вставьте HTTPS-ссылку на папку Google Drive.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, "folders");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var id = index >= 0 && index + 1 < segments.Length ? segments[index + 1] :
            uri.AbsolutePath == "/open" ? query["id"] : null;
        var resourceKey = query["resourcekey"];
        if (id is null || !Token.IsMatch(id) || (resourceKey is not null && !Token.IsMatch(resourceKey)))
            throw new ArgumentException("В ссылке не найден идентификатор папки Google Drive.");
        return new(id, resourceKey);
    }
}