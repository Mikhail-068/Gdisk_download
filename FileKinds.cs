using System.IO;

namespace DriveDownload;

internal static class FileKinds
{
    internal static string Classify(string mime, string path)
    {
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "Video";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "Image";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".avi" or ".mp4" or ".mkv" or ".mov" or ".webm" or ".m4v" => "Video",
            ".pdf" => "Pdf",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".heic" => "Image",
            ".xls" or ".xlsx" or ".ods" or ".csv" => "Spreadsheet",
            ".doc" or ".docx" or ".odt" or ".txt" => "Document",
            ".ppt" or ".pptx" or ".odp" => "Presentation",
            ".gz" or ".zip" or ".7z" or ".rar" or ".tar" => "Archive",
            _ => "Other"
        };
    }
}