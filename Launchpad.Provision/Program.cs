using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Launchpad.Core;
using Launchpad.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--apply"))
        {
            Console.Error.WriteLine("Usage: Launchpad.Provision <preview-json-path> [--apply]");
            return 2;
        }
        var paths = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        var apps = new List<LaunchableApp>();
        foreach (var file in paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path)))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".lnk" or ".exe")) continue;
            try
            {
                var app = AppImportService.CreateAppFromPath(file);
                if (app is null || (!app.ParsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(app.ParsingName).ToLowerInvariant() is not (".exe" or ".cmd" or ".bat" or ".com")))
                {
                    Console.WriteLine($"SKIP {Path.GetFileName(file)} (not an application target)");
                    continue;
                }
                apps.Add(app);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or ArgumentException)
            { Console.Error.WriteLine($"SKIP {Path.GetFileName(file)}: {ex.Message}"); }
        }
        if (apps.Count == 0) { Console.Error.WriteLine("No desktop applications found; configuration was not changed."); return 1; }
        apps = apps.OrderBy(app => app.Name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true)).ToList();
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".launchpad");
        var store = new LayoutStore(data);
        var layout = store.Load();
        var previousCount = layout.ManualApps?.Count ?? 0;
        layout.Items = [];
        layout.Version = 2;
        LayoutEditor.SyncManualApps(layout, apps);
        var preview = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
        File.WriteAllText(preview, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var app in layout.ManualApps!) Console.WriteLine("APP " + app.Name);
        Console.WriteLine($"Desktop apps: {layout.ManualApps.Count}; previous apps: {previousCount}; preview: {preview}");
        if (args.Length == 2)
        {
            var primary = Path.Combine(data, "layout.json");
            if (File.Exists(primary)) File.Copy(primary, Path.Combine(data, $"layout.before-desktop.{DateTime.Now:yyyyMMdd-HHmmss}.json"));
            store.Save(layout);
            Console.WriteLine("APPLIED " + primary);
        }
        return 0;
    }
}
