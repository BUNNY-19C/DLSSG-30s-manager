using System.IO.Compression;

namespace DLSSGManager;

/// <summary>
/// Expands a release archive into a staging folder, treating the archive as hostile: entries may not
/// leave the folder or address alternate data streams, and extraction stops at a ceiling counted from
/// the bytes actually decompressed, whatever sizes the archive declares.
/// </summary>
internal static class ArchiveExtractor
{
    /// <summary>Largest archive accepted, downloaded or imported.</summary>
    public const long MaxArchiveBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Ceiling on what an archive may expand to. The full 0.4.1 package expands to about 220 MB;
    /// a zip bomb would not stop there.
    /// </summary>
    public const long MaxExtractedBytes = 1024L * 1024 * 1024;

    public const int MaxEntries = 4096;

    /// <summary>
    /// Expands <paramref name="archivePath"/> into <paramref name="staging"/>. A single folder wrapping
    /// the payload (a zipped folder, a source archive) is flattened so staging looks like the payload root.
    /// </summary>
    public static void Extract(string archivePath, string staging, CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooManyEntries", MaxEntries));

        long extracted = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // A colon would address an alternate data stream; nothing in a payload needs one.
            if (entry.FullName.Contains(':')) throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                extracted += read;
                if (extracted > MaxExtractedBytes)
                    throw new InvalidOperationException(Loc.T("Fetch.ExtractTooLarge", MaxExtractedBytes / 1024 / 1024));
                output.Write(buffer, 0, read);
            }
        }

        FlattenWrapper(staging);
    }

    /// <summary>
    /// Lifts the payload out of the one folder that wraps it. Folders without the INI — such as the
    /// <c>__MACOSX</c> metadata macOS adds to a zip — are not candidates.
    /// </summary>
    private static void FlattenWrapper(string staging)
    {
        if (File.Exists(Path.Combine(staging, ModSource.IniName))) return;

        var wrappers = Directory.GetDirectories(staging)
            .Where(d => File.Exists(Path.Combine(d, ModSource.IniName)))
            .ToArray();
        if (wrappers.Length != 1) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));

        var inner = wrappers[0];
        foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
        {
            var target = Path.Combine(staging, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(target)) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));
                Directory.Move(entry, target);
            }
            else File.Move(entry, target, overwrite: true);
        }

        try { Directory.Delete(inner, recursive: true); }
        catch { /* an empty wrapper left in staging is removed with it */ }
    }
}
