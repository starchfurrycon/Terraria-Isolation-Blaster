using System.Globalization;
using System.Text;

namespace ZhaDai.Manager;

/// <summary>One row of the world picker.</summary>
internal sealed record WorldEntry(
    string Path,
    string FileName,
    long Size,
    DateTime ModifiedUtc,
    string? Title);

/// <summary>
/// Lists the vanilla save directory and reads only the cheap part of each world header. The title
/// lives immediately after the section pointer table, so it can be shown without decoding tiles.
/// </summary>
internal static class WorldCatalog
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games",
        "Terraria",
        "Worlds");

    public static IReadOnlyList<WorldEntry> List(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<WorldEntry> entries = [];
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.wld");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string path in files)
        {
            try
            {
                FileInfo info = new(path);
                entries.Add(new WorldEntry(
                    info.FullName,
                    info.Name,
                    info.Length,
                    info.LastWriteTimeUtc,
                    TryReadTitle(info.FullName)));
            }
            catch (IOException)
            {
                // A world being rewritten by the game right now: skip it rather than fail the list.
            }
            catch (UnauthorizedAccessException)
            {
                // Not readable: skip it.
            }
        }

        entries.Sort((left, right) => right.ModifiedUtc.CompareTo(left.ModifiedUtc));
        return entries;
    }

    /// <summary>"12.3 MB" style size, or "812 KB" for small worlds.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        return Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
    }

    public static string FormatModified(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the world title out of the header's string table: version, signature, section pointer
    /// table, the packed frame-important flags, and then the title. Nothing else is decoded.
    /// </summary>
    public static string? TryReadTitle(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

            if (stream.Length < 32)
            {
                return null;
            }

            uint version = reader.ReadUInt32();
            if (version is < 200 or > 400)
            {
                return null;
            }

            byte[] signature = reader.ReadBytes(7);
            if (signature.Length != 7 || Encoding.ASCII.GetString(signature) is not ("relogic" or "xindong"))
            {
                return null;
            }

            if (reader.ReadByte() != 2)
            {
                return null;
            }

            _ = reader.ReadUInt32(); // file revision
            _ = reader.ReadUInt64(); // favourite and future flags

            short sectionCount = reader.ReadInt16();
            if (sectionCount is < 3 or > 32)
            {
                return null;
            }

            int[] sections = new int[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                sections[i] = reader.ReadInt32();
            }

            if (sections[0] <= 0 || sections[0] > stream.Length)
            {
                return null;
            }

            short bitCount = reader.ReadInt16();
            if (bitCount <= 0 || bitCount > 4096)
            {
                return null;
            }

            long packedBytes = (bitCount + 7) / 8;
            stream.Position = sections[0];

            // ReadString gives the length-prefixed title; the seed and everything else are skipped.
            string title = reader.ReadString();
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch (IOException)
        {
            // Unreadable or truncated header (EndOfStreamException is an IOException): the row
            // still appears in the list, just without a parsed title.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
