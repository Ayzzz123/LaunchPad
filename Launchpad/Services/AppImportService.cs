using Launchpad.Core;

namespace Launchpad.Services;

internal static class AppImportService
{
    public static LaunchableApp? CreateAppFromPath(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            return null;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".lnk" && File.Exists(filePath))
        {
            return CreateAppFromShortcut(filePath) is { } shortcut
                ? shortcut with { IconSource = Path.GetFullPath(filePath) } : null;
        }

        return CreateFromFileOrDirectory(filePath);
    }

    private static LaunchableApp CreateFromFileOrDirectory(string path)
    {
        var isDirectory = Directory.Exists(path);
        var fullPath = Path.GetFullPath(path);
        if (isDirectory) fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        var name = isDirectory ? new DirectoryInfo(fullPath).Name
            : Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(fullPath) : Path.GetFileName(fullPath);
        var workingDirectory = Path.GetDirectoryName(fullPath) ?? (isDirectory ? fullPath : "");
        var id = AppIdGenerator.CreateId("manual", $"{fullPath.ToUpperInvariant()}\n\n{workingDirectory.ToUpperInvariant()}");

        return new LaunchableApp(id, name, fullPath, "", workingDirectory);
    }

    private static LaunchableApp? CreateAppFromShortcut(string shortcutPath)
    {
        dynamic? shell = null;
        object? shortcut = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }
            shortcut = shell.CreateShortcut(shortcutPath);
            dynamic props = shortcut;

            var target = (string?)props.TargetPath ?? "";
            var arguments = (string?)props.Arguments ?? "";
            var workingDirectory = (string?)props.WorkingDirectory ?? "";

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            target = Environment.ExpandEnvironmentVariables(target);
            workingDirectory = Environment.ExpandEnvironmentVariables(workingDirectory);

            // 处理 shell: 协议
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                || target.Contains("AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                var parsingName = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                    ? target
                    : $"shell:AppsFolder\\{target[(target.IndexOf("AppsFolder\\", StringComparison.OrdinalIgnoreCase) + "AppsFolder\\".Length)..]}";
                var shellArgs = arguments.Trim();
                var shellDir = string.IsNullOrWhiteSpace(workingDirectory) ? "" : Path.GetFullPath(workingDirectory);
                var id = AppIdGenerator.CreateId("manual", $"{parsingName.ToUpperInvariant()}\n{shellArgs}\n{shellDir.ToUpperInvariant()}");
                return new LaunchableApp(id, Path.GetFileNameWithoutExtension(shortcutPath), parsingName, shellArgs, shellDir);
            }

            // 处理普通文件路径
            if (!Path.IsPathRooted(target) && !string.IsNullOrWhiteSpace(workingDirectory))
            {
                target = Path.GetFullPath(Path.Combine(workingDirectory, target));
            }
            else if (Path.IsPathRooted(target))
            {
                target = Path.GetFullPath(target);
            }

            if (!File.Exists(target) && !Directory.Exists(target))
            {
                return null;
            }

            var normalizedArgs = arguments.Trim();
            var normalizedDir = string.IsNullOrWhiteSpace(workingDirectory) ? "" : Path.GetFullPath(workingDirectory);
            var id2 = AppIdGenerator.CreateId("manual", $"{target.ToUpperInvariant()}\n{normalizedArgs}\n{normalizedDir.ToUpperInvariant()}");

            return new LaunchableApp(id2, Path.GetFileNameWithoutExtension(shortcutPath), target, normalizedArgs, normalizedDir);
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }
}
