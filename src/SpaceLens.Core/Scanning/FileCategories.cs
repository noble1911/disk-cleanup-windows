namespace SpaceLens.Core.Scanning;

public enum FileCategory
{
    Other,
    Video,
    Image,
    Audio,
    Document,
    Archive,
    DiskImage,
    Executable,
    Code,
    Database,
    Log,
    System,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts)
        {
            foreach (var e in exts) map[e] = c;
        }

        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "m2ts", "3gp");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "md", "epub", "csv");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "nupkg", "jar", "whl");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd", "dmg");
        Add(FileCategory.Executable, "exe", "msi", "msix", "appx", "dll", "sys", "msp", "bat", "cmd", "ps1");
        Add(FileCategory.Code, "cs", "java", "kt", "js", "ts", "tsx", "jsx", "py", "go", "rs", "c", "cpp", "h", "hpp", "json", "xml", "yml", "yaml", "class", "pdb", "obj", "o", "lib", "a");
        Add(FileCategory.Database, "db", "sqlite", "mdf", "ldf", "ndf", "bak", "accdb", "mdb", "edb", "pst", "ost");
        Add(FileCategory.Log, "log", "etl", "dmp", "mdmp", "hdmp", "trace");
        Add(FileCategory.System, "pf", "dat", "bin", "tmp", "cache", "manifest", "mui", "cat");
        return map;
    }

    public static FileCategory Of(string extension) =>
        Map.TryGetValue(extension, out var c) ? c : FileCategory.Other;
}

public sealed class ExtensionStat
{
    public ExtensionStat(string extension) => Extension = extension;

    public string Extension { get; }
    public long Bytes { get; internal set; }
    public long Count { get; internal set; }
    public FileCategory Category => FileCategories.Of(Extension);
}
