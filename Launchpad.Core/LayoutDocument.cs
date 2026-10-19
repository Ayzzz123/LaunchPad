namespace Launchpad.Core;

public sealed class LayoutDocument
{
    public int Version { get; set; } = 2;

    public string Hotkey { get; set; } = "Alt+Space";

    public List<LayoutItem> Items { get; set; } = [];

    public List<LaunchableApp> ManualApps { get; set; } = [];
}
