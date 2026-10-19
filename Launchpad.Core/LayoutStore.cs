using System.Text.Json;

namespace Launchpad.Core;

public sealed class LayoutStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory = directory;
    private string PrimaryPath => Path.Combine(_directory, "layout.json");
    private string BackupPath => Path.Combine(_directory, "layout.backup.json");
    public string DirectoryPath => _directory;

    public static LayoutStore ForUser(string userProfile, string legacyLocalAppData)
    {
        // AppData can be virtualized by a packaged parent process; the user-profile location is shared.
        var store = new LayoutStore(Path.Combine(userProfile, ".launchpad"));
        if (File.Exists(store.PrimaryPath) || File.Exists(store.BackupPath)) return store;

        var legacy = new LayoutStore(Path.Combine(legacyLocalAppData, "Launchpad"));
        if (legacy.TryLoad(out var layout))
        {
            store.Save(layout);
            store.Save(layout); // Seed an independent valid backup before the first edit.
        }
        return store;
    }

    public LayoutDocument Load() => TryLoad(out var layout) ? layout : new LayoutDocument();

    private bool TryLoad(out LayoutDocument layout)
    {
        try
        {
            layout = Read(PrimaryPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            try
            {
                layout = Read(BackupPath);
                return true;
            }
            catch (Exception backupException) when (backupException is IOException or JsonException or NotSupportedException)
            {
                layout = new LayoutDocument();
                return false;
            }
        }
    }

    public void Save(LayoutDocument layout)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = Path.Combine(_directory, $"layout.{Guid.NewGuid():N}.tmp");
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(layout, JsonOptions);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(PrimaryPath))
            {
                File.Replace(tempPath, PrimaryPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, PrimaryPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static LayoutDocument Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LayoutDocument>(stream, JsonOptions) ?? new LayoutDocument();
    }
}
