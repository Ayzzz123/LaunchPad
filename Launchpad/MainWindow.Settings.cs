using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Launchpad.Core;
using Launchpad.Services;
using Microsoft.Win32;

namespace Launchpad;

public partial class MainWindow
{
    private bool _isDark;
    private bool _capturingHotkey;
    private string _hotkeyDraft = "";
    private string _hotkeyBeforeCapture = "";
    private LayoutItem? _actionItem;
    private bool _removeAction;

    private void ApplyBackdrop()
    {
        _isDark = SystemTheme.Apply(this);
        var acrylic = WindowBackdrop.Apply(this, _isDark);
        GlassSurface.Background = new SolidColorBrush(_isDark
            ? acrylic ? Color.FromArgb(24, 8, 9, 12) : Color.FromRgb(16, 17, 20)
            : acrylic ? Color.FromArgb(46, 247, 249, 253) : Color.FromRgb(237, 241, 247));
        if (AppearanceHint is not null) AppearanceHint.Text = _isDark ? "跟随系统 · 深色" : "跟随系统 · 浅色";
        RenderTiles();
        if (_state.Page == LauncherPage.Apps) RefreshManagedApps();
    }

    private void OpenSettings(LauncherPage page)
    {
        if (!IsVisible) ShowLauncher();
        CancelHotkeyCapture();
        _state.Page = page;
        _state.FolderId = null;
        _state.SetEditing(false);
        _state.HasOverlay = false;
        ActionOverlay.Visibility = FolderOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        HomePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        AppsSettingsCard.Visibility = page == LauncherPage.Apps ? Visibility.Visible : Visibility.Collapsed;
        HotkeySettings.Visibility = page == LauncherPage.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        AppsTab.Background = page == LauncherPage.Apps ? (Brush)FindResource("SelectedControl") : Brushes.Transparent;
        HotkeyTab.Background = page == LauncherPage.Hotkey ? (Brush)FindResource("SelectedControl") : Brushes.Transparent;
        _hotkeyDraft = string.IsNullOrEmpty(_layout.Hotkey) ? "Alt+Space" : _layout.Hotkey;
        EnableHotkey.IsChecked = !string.IsNullOrEmpty(_layout.Hotkey);
        HotkeyCapture.Content = _hotkeyDraft;
        HotkeyError.Text = "";
        RefreshManagedApps();
        UpdateGridSize();
    }

    private void RefreshManagedApps()
    {
        ManagedApps.Children.Clear();
        ManageCount.Text = _layout.ManualApps.Count == 0 ? "尚未添加项目；点击右侧按钮开始。" : $"共 {_layout.ManualApps.Count} 个项目 · 在启动台中拖动整理";
        foreach (var app in _layout.ManualApps)
        {
            var item = LayoutItem.ForApp(app.Id, app.Name);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Viewbox { Width = 32, Height = 32, Child = CreateAppIcon(app), HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Add(icon);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 8, 8, 8) };
            label.Children.Add(new TextBlock { Text = app.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = app.ParsingName, FontSize = 10, Foreground = (Brush)FindResource("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = app.ParsingName });
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var more = new Button { Content = "•••", Padding = new Thickness(12, 6, 12, 6), Style = (Style)FindResource("QuietButton"), VerticalAlignment = VerticalAlignment.Center, ToolTip = $"管理 {app.Name}" };
            more.ContextMenu = CreateAppMenu(new TileView(item, app, []));
            ConfigureMenuPlacement(more.ContextMenu, more);
            more.ContextMenu.Opened += (_, _) => _contextMenuOpen = true;
            more.ContextMenu.Closed += (_, _) => _contextMenuOpen = false;
            more.Click += (_, _) => { more.ContextMenu.PlacementTarget = more; more.ContextMenu.IsOpen = true; };
            Grid.SetColumn(more, 2);
            row.Children.Add(more);
            ManagedApps.Children.Add(new Border { Child = row, BorderBrush = (Brush)FindResource("Divider"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private ContextMenu CreateAppMenu(TileView tile)
    {
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)) };
        var rename = new MenuItem { Header = "重命名", Style = (Style)FindResource(typeof(MenuItem)) };
        rename.Click += (_, _) => BeginRename(tile.Item);
        var remove = new MenuItem { Header = tile.Item.Kind == LayoutItemKind.Folder ? "解散文件夹" : "从启动台移除", Style = (Style)FindResource(typeof(MenuItem)) };
        remove.Click += (_, _) => BeginRemove(tile.Item);
        menu.Items.Add(rename);
        menu.Items.Add(remove);
        return menu;
    }

    private void ConfigureMenuPlacement(ContextMenu menu, FrameworkElement target)
    {
        MenuInteraction.Attach(menu);
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            var origin = target.TranslatePoint(new Point(), this);
            var x = Math.Clamp(targetSize.Width - popupSize.Width, 12 - origin.X,
                Math.Max(12 - origin.X, ActualWidth - origin.X - popupSize.Width - 12));
            var y = origin.Y + targetSize.Height + popupSize.Height + 6 <= ActualHeight - 12
                ? targetSize.Height + 6 : -popupSize.Height - 6;
            y = Math.Clamp(y, 12 - origin.Y, Math.Max(12 - origin.Y, ActualHeight - origin.Y - popupSize.Height - 12));
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        };
    }

    private void AddApps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.ContextMenu is { } existingMenu)
        {
            existingMenu.IsOpen = !existingMenu.IsOpen;
            return;
        }
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)) };
        var files = new MenuItem { Header = "添加应用或文件", Style = (Style)FindResource(typeof(MenuItem)) };
        files.Click += async (_, _) => await PickAndImportAsync(false);
        var folders = new MenuItem { Header = "添加磁盘文件夹", Style = (Style)FindResource(typeof(MenuItem)) };
        folders.Click += async (_, _) => await PickAndImportAsync(true);
        menu.Items.Add(files);
        menu.Items.Add(folders);
        ConfigureMenuPlacement(menu, button);
        void CloseOnOwnerPress(object inputSender, PreProcessInputEventArgs input)
        {
            if (menu.IsOpen && input.StagingItem.Input is MouseButtonEventArgs mouse
                && mouse.RoutedEvent == Mouse.PreviewMouseDownEvent && mouse.ChangedButton == MouseButton.Left
                && new Rect(button.RenderSize).Contains(mouse.GetPosition(button)))
            {
                // Consume the owner press before the captured menu closes and replays it to the button.
                menu.IsOpen = false;
                input.Cancel();
            }
        }
        menu.Opened += (_, _) =>
        {
            _contextMenuOpen = true;
            InputManager.Current.PreProcessInput += CloseOnOwnerPress;
        };
        menu.Closed += (_, _) =>
        {
            InputManager.Current.PreProcessInput -= CloseOnOwnerPress;
            _contextMenuOpen = false;
        };
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task PickAndImportAsync(bool folders)
    {
        _modalOperation = true;
        try
        {
            string[] files;
            if (folders)
            {
                var picker = new OpenFolderDialog { Title = "添加磁盘文件夹到 Launchpad", Multiselect = true };
                if (picker.ShowDialog(this) != true) return;
                files = picker.FolderNames;
            }
            else
            {
                var picker = new OpenFileDialog { Title = "添加应用或文件到 Launchpad", Filter = "所有文件 (*.*)|*.*|应用与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk", Multiselect = true };
                if (picker.ShowDialog(this) != true) return;
                files = picker.FileNames;
            }
            // Resolve Shell links off the UI thread, on an STA for COM.
            var completion = new TaskCompletionSource<(List<LaunchableApp> Apps, List<string> Errors)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var apps = new List<LaunchableApp>();
                var errors = new List<string>();
                foreach (var file in files)
                {
                    try
                    {
                        var app = AppImportService.CreateAppFromPath(file);
                        if (app is not null) apps.Add(app);
                        else errors.Add($"无法读取 {System.IO.Path.GetFileName(file)} 的启动目标");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or ArgumentException)
                    { errors.Add($"{System.IO.Path.GetFileName(file)}：{ex.Message}"); }
                }
                completion.SetResult((apps, errors));
            }) { IsBackground = true, Name = "Launchpad import" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var result = await completion.Task;
            var count = _layout.ManualApps.Count;
            CommitApps(_layout.ManualApps.Concat(result.Apps).ToList());
            ShowNotice(result.Errors.Count > 0 ? string.Join("；", result.Errors.Take(3))
                : _layout.ManualApps.Count > count ? $"已添加 {_layout.ManualApps.Count - count} 个项目" : "这些项目已在启动台中");
        }
        finally
        {
            _modalOperation = false;
            Activate();
        }
    }

    private void CommitApps(List<LaunchableApp> apps)
    {
        LayoutEditor.SyncManualApps(_layout, apps);
        _appsById.Clear();
        foreach (var app in _layout.ManualApps) _appsById.TryAdd(app.Id, app);
        if (_openFolderId is not null && FindFolder(_openFolderId) is null) _openFolderId = null;
        SaveLayout();
        UpdateGridSize();
        RenderTiles();
        if (_state.Page == LauncherPage.Apps) RefreshManagedApps();
    }

    private void BeginRename(LayoutItem item)
    {
        _actionItem = item;
        _removeAction = false;
        _state.HasOverlay = true;
        ActionTitle.Text = "重命名";
        ActionDescription.Text = "只修改启动台中的显示名称。";
        ActionInput.Visibility = Visibility.Visible;
        ActionInput.Text = item.Title;
        ConfirmActionButton.Content = "保存";
        ActionOverlay.Visibility = Visibility.Visible;
        ActionInput.Focus();
        ActionInput.SelectAll();
    }

    private void BeginRemove(LayoutItem item)
    {
        _actionItem = item;
        _removeAction = true;
        _state.HasOverlay = true;
        ActionTitle.Text = item.Kind == LayoutItemKind.Folder ? "解散文件夹？" : "从启动台移除？";
        ActionDescription.Text = item.Kind == LayoutItemKind.Folder ? $"“{item.Title}”中的项目将回到主网格。" : $"移除“{item.Title}”的入口，不会卸载应用，也不会删除原文件或文件夹。";
        ActionInput.Visibility = Visibility.Collapsed;
        ConfirmActionButton.Content = item.Kind == LayoutItemKind.Folder ? "解散" : "移除";
        ActionOverlay.Visibility = Visibility.Visible;
        ConfirmActionButton.Focus();
    }

    private void ConfirmAction_Click(object sender, RoutedEventArgs e)
    {
        if (_actionItem is not { } item) return;
        var name = ActionInput.Text.Trim();
        if (!_removeAction && string.IsNullOrEmpty(name)) { ActionInput.Focus(); return; }
        if (item.Kind == LayoutItemKind.Folder)
        {
            var folder = FindFolder(item.Id);
            if (folder is not null)
            {
                if (_removeAction)
                {
                    var index = _layout.Items.IndexOf(folder);
                    _layout.Items.RemoveAt(index);
                    _layout.Items.InsertRange(index, folder.Children);
                    _openFolderId = null;
                }
                else
                {
                    folder.Title = name;
                    if (_openFolderId == folder.Id) FolderNameInput.Text = name;
                }
            }
            SaveLayout();
            UpdateGridSize();
            RenderTiles();
        }
        else CommitApps(_removeAction ? _layout.ManualApps.Where(app => app.Id != item.Id).ToList()
            : _layout.ManualApps.Select(app => app.Id == item.Id ? app with { Name = name } : app).ToList());
        CloseAction();
    }

    private void CloseAction()
    {
        _state.HasOverlay = false;
        _actionItem = null;
        ActionOverlay.Visibility = Visibility.Collapsed;
    }

    private void GoBack()
    {
        switch (_state.Back())
        {
            case BackAction.Overlay: CloseAction(); break;
            case BackAction.Folder: _page = _rootPage; UpdateGridSize(); RenderTiles(); break;
            case BackAction.Editing: RenderTiles(); break;
            case BackAction.Search: SearchBox.Clear(); SearchBox.Focus(); break;
            case BackAction.Settings: BackHome_Click(this, new RoutedEventArgs()); break;
            case BackAction.Hide: HideLauncher(); break;
        }
    }

    private void BackHome_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyCapture();
        _state.Page = LauncherPage.Home;
        SettingsPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
        UpdateGridSize();
        RenderTiles();
        SearchBox.Focus();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        _state.SetEditing(!_state.IsEditing);
        RenderTiles();
    }

    private void FinishEditing()
    {
        _state.SetEditing(false);
        Root.Focus();
        RenderTiles();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(LauncherPage.Apps);
    private void AppsTab_Click(object sender, RoutedEventArgs e) => OpenSettings(LauncherPage.Apps);
    private void HotkeyTab_Click(object sender, RoutedEventArgs e) => OpenSettings(LauncherPage.Hotkey);
    private void CancelAction_Click(object sender, RoutedEventArgs e) => CloseAction();
    private void FolderBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (_state.IsEditing) FinishEditing();
        else FolderReturnButton_Click(sender, e);
        e.Handled = true;
    }

    private void CaptureHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (!EnableHotkey.IsChecked.GetValueOrDefault()) return;
        _hotkeyBeforeCapture = _hotkeyDraft;
        _hotkey?.TryChange("");
        _capturingHotkey = true;
        HotkeyCapture.Content = "请按下组合键…";
        HotkeyError.Text = "";
    }

    private void CaptureHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { CancelHotkeyCapture(); return; }
        if (GlobalHotkeyHost.TryFormat(e, out var binding))
        {
            _hotkeyDraft = binding;
            _capturingHotkey = false;
            HotkeyCapture.Content = binding;
            _hotkey?.TryChange(_layout.Hotkey);
            HotkeyError.Text = "尚未保存";
        }
        else HotkeyError.Text = "按住 Ctrl、Alt、Shift 或 Win，再按一个普通按键。";
    }

    private void CancelHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        _hotkeyDraft = _hotkeyBeforeCapture;
        HotkeyCapture.Content = _hotkeyDraft;
        _hotkey?.TryChange(_layout.Hotkey);
    }

    private void EnableHotkey_Changed(object sender, RoutedEventArgs e)
    {
        if (HotkeyCapture is null) return;
        CancelHotkeyCapture();
        HotkeyCapture.IsEnabled = EnableHotkey.IsChecked.GetValueOrDefault();
    }

    private void SaveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) { HotkeyError.Text = "请先完成组合键录入。"; return; }
        var error = SaveHotkey(EnableHotkey.IsChecked.GetValueOrDefault() ? _hotkeyDraft : "");
        HotkeyError.Text = error ?? "";
        if (error is null) { UpdateEditControls(); ShowNotice("快捷键设置已保存"); }
    }

    private void ClearDragHighlight()
    {
        foreach (var button in ActiveTilesGrid.Children.OfType<Button>())
        {
            button.BorderBrush = Brushes.Transparent;
            button.BorderThickness = new Thickness(2);
        }
    }

    private void HighlightFolderTarget()
    {
        if (_makeFolderTargetId is null) return;
        var button = ActiveTilesGrid.Children.OfType<Button>().FirstOrDefault(b => b.Tag is TileView tile && tile.Item.Id == _makeFolderTargetId);
        if (button is null) return;
        button.BorderBrush = (Brush)FindResource("Accent");
        button.BorderThickness = new Thickness(2);
    }
}
