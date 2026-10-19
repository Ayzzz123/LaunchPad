namespace Launchpad.Core;

public enum LauncherPage { Home, Apps, Hotkey }
public enum BackAction { Overlay, Folder, Editing, Search, Settings, Hide }

public sealed class LauncherState
{
    public LauncherPage Page { get; set; }
    public string? FolderId { get; set; }
    public string SearchText { get; set; } = "";
    public bool IsEditing { get; private set; }
    public bool HasOverlay { get; set; }
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public void SetEditing(bool value) => IsEditing = value && !IsSearching && Page == LauncherPage.Home;

    public BackAction Back()
    {
        if (HasOverlay) { HasOverlay = false; return BackAction.Overlay; }
        if (FolderId is not null) { FolderId = null; return BackAction.Folder; }
        if (IsEditing) { IsEditing = false; return BackAction.Editing; }
        if (IsSearching) { SearchText = ""; return BackAction.Search; }
        if (Page != LauncherPage.Home) { Page = LauncherPage.Home; return BackAction.Settings; }
        return BackAction.Hide;
    }
}
