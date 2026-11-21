using System.IO;
using System.Text.RegularExpressions;

using static DriveDownload.L;

namespace DriveDownload;

internal sealed record CatalogItem(string Key, string Name, string Relative, RemoteFile Remote,
    CatalogItem[] Children, string? Export = null, Phrase? Problem = null)
{
    public bool IsFolder => Remote.MimeType == RemoteFile.Folder;
    public long Size => IsFolder || Export is not null ? 0 : Math.Max(0, Remote.Length);
}

internal sealed record FolderCatalog(string Name, string LocalName, CatalogItem[] Items)
{
    public IEnumerable<CatalogItem> Flatten() => Items.SelectMany(Flatten);

    private static IEnumerable<CatalogItem> Flatten(CatalogItem item)
    {
        yield return item;
        foreach (var child in item.Children.SelectMany(Flatten)) yield return child;
    }
}

internal sealed class DriveCatalog(GoogleDrive drive)
{
    private static readonly Dictionary<string, (string Mime, string Suffix)> Exports = new()
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        ["application/vnd.google-apps.drawing"] = ("application/pdf", ".pdf")
    };
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)(\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal const string CacheDirectory = ".drive-download";

    public async Task<FolderCatalog> Read(FolderLink link, CancellationToken ct)
    {
        drive.AddResource(link.Id, link.ResourceKey);
        var folder = await drive.Get(link.Id, ct).ConfigureAwait(false);
        if (folder.MimeType != RemoteFile.Folder)
            throw new DriveFailure(PhraseFor("MustBeFolder"));

        var rootName = Unique(SafeName(folder.Name), null, new(StringComparer.OrdinalIgnoreCase));
        if (rootName.Equals(CacheDirectory, StringComparison.OrdinalIgnoreCase)) rootName += " (Drive)";
        var items = await Walk(folder, "", "", new(StringComparer.Ordinal), ct).ConfigureAwait(false);
        return new(folder.Name, rootName, items);
    }

    private async Task<CatalogItem[]> Walk(RemoteFile folder, string prefix, string parentKey,
        HashSet<string> ancestors, CancellationToken ct)
    {
        if (!ancestors.Add(folder.Id))
            throw new DriveFailure(PhraseFor("ShortcutLoop"));
        try
        {
            drive.AddResource(folder.Id, folder.ResourceKey);
            var children = await drive.Children(folder, ct).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<CatalogItem>(children.Count);
            // Allocate local names by ID so repeated scans resolve collisions in the same order.
            foreach (var alias in children.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var key = parentKey.Length == 0 ? alias.Id : parentKey + "/" + alias.Id;
                var child = alias;
                drive.AddResource(alias.Id, alias.ResourceKey);
                if (alias.MimeType == RemoteFile.Shortcut)
                {
                    try
                    {
                        var shortcut = alias.ShortcutDetails ?? throw new DriveFailure(PhraseFor("ShortcutUnavailable"));
                        drive.AddResource(shortcut.TargetId, shortcut.TargetResourceKey);
                        child = await drive.Get(shortcut.TargetId, ct).ConfigureAwait(false);
                        drive.AddResource(child.Id, child.ResourceKey);
                    }
                    catch (DriveFailure e) when (!e.Retry && !e.Reconnect)
                    {
                        var failedName = Unique(SafeName(alias.Name), null, names);
                        items.Add(new(key, failedName, Path.Combine(prefix, failedName), alias, [], Problem: e.Text));
                        continue;
                    }
                }

                Exports.TryGetValue(child.MimeType, out var export);
                var name = Unique(SafeName(alias.Name), export.Suffix, names);
                var relative = Path.Combine(prefix, name);
                if (child.MimeType == RemoteFile.Folder)
                {
                    try
                    {
                        var nested = await Walk(child, relative, key, ancestors, ct).ConfigureAwait(false);
                        items.Add(new(key, name, relative, child, nested));
                    }
                    catch (DriveFailure e) when (!e.Retry && !e.Reconnect)
                    {
                        items.Add(new(key, name, relative, child, [], Problem: e.Text));
                    }
                }
                else
                {
                    var problem = child.Capabilities?.CanDownload == false ? PhraseFor("OwnerBlocked") :
                        export.Mime is null && child.Length < 0 ? PhraseFor("ExportUnsupported") : null;
                    items.Add(new(key, name, relative, child, [], export.Mime, problem));
                }
            }
            return items.OrderByDescending(x => x.IsFolder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            ancestors.Remove(folder.Id);
        }
    }

    private static string SafeName(string value)
    {
        var illegal = Path.GetInvalidFileNameChars();
        var name = new string(value.Select(c => illegal.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0) return "_";
        if (Reserved.IsMatch(name)) name = "_" + name;
        return name;
    }

    private static string Unique(string name, string? extension, HashSet<string> used)
    {
        var suffix = extension ?? Path.GetExtension(name);
        if (suffix.Length > 28) suffix = "";
        var stem = extension is not null || suffix.Length == 0 ? name : name[..^suffix.Length];
        if (stem.Length > 145)
        {
            var cut = char.IsHighSurrogate(stem[144]) ? 144 : 145;
            stem = stem[..cut];
        }
        for (var i = 1; ; i++)
        {
            var value = stem + (i == 1 ? "" : $" ({i})") + suffix;
            if (used.Add(value)) return value;
        }
    }
}