using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Launchpad.Core;
using Launchpad.Services;
using IOPath = System.IO.Path;

namespace Launchpad;

public partial class MainWindow : Window, IDisposable
{
    internal const string HostWindowTitle = "Launchpad Host";
    private const int MaxColumns = 7;
    private const int MaxRows = 4;
    private const double DefaultWindowWidth = 1080;
    private const double DefaultWindowHeight = 720;

    private readonly LayoutStore _store;
    private readonly AppLauncher _launcher = new();
    private readonly ShellIconCache _icons;
    private GlobalHotkeyHost? _hotkey;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _folderHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _edgeHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _folderTitleSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<string, LaunchableApp> _appsById = new(StringComparer.Ordinal);
    private readonly List<TileView> _visibleTiles = [];
    private HwndSource? _windowSource;

    private LayoutDocument _layout;
    private readonly LauncherState _state = new();
    private string? _openFolderId { get => _state.FolderId; set => _state.FolderId = value; }
    private string? _dragSourceId;
    private string? _hoverTargetId;
    private string? _makeFolderTargetId;
    private Action<string>? _trayNotification;
    private string? _pendingNotice;
    private string _searchText { get => _state.SearchText; set => _state.SearchText = value; }
    private int _page;
    private int _columns = MaxColumns;
    private int _rows = MaxRows;
    private int _selectedIndex = -1;
    private int _edgeDirection;
    private bool _updatingFolderName;
    private bool _isImeComposing;
    private int _rootPage;
    private double _iconSize = 48;
    private bool _modalOperation;
    private bool _dragging;
    private bool _suppressClick;
    private bool _contextMenuOpen;
    private Button? _longPressButton;
    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private UniformGrid ActiveTilesGrid => _openFolderId is null ? TilesGrid : FolderTilesGrid;
    private bool _disposed;
    private long _animationToken;
    private Point _mouseDown;

    public MainWindow(LayoutStore store, string dataDirectory)
    {
        InitializeComponent();
        _isDark = SystemTheme.Apply(this);
        _store = store;
        _layout = _store.Load();

        // 升级旧版本布局
        var upgradedLayout = _layout.Version < 2;
        if (upgradedLayout)
        {
            BackupAndUpgradeLayout();
        }

        _layout.Items = (_layout.Items ?? [])
            .Where(item => item is not null
                && !string.IsNullOrWhiteSpace(item.Id)
                && item.Kind is LayoutItemKind.App or LayoutItemKind.Folder)
            .ToList();
        _layout.Hotkey ??= "Alt+Space";
        _layout.ManualApps ??= [];
        var storedItemIds = LayoutEditor.AppIds(_layout).ToArray();
        LayoutEditor.SyncManualApps(_layout, _layout.ManualApps);
        var layoutWasSynced = !storedItemIds.SequenceEqual(LayoutEditor.AppIds(_layout));

        for (var index = 0; index < _layout.Items.Count; index++)
        {
            var item = _layout.Items[index];
            if (item.Kind != LayoutItemKind.Folder)
            {
                continue;
            }

            item.Children = (item.Children ?? [])
                .Where(child => child is not null
                    && child.Kind == LayoutItemKind.App
                    && !string.IsNullOrWhiteSpace(child.Id))
                .ToList();
            if (item.Children.Count == 1)
            {
                _layout.Items[index] = item.Children[0];
            }
            else if (item.Children.Count == 0)
            {
                _layout.Items.RemoveAt(index--);
            }
        }

        // 加载手动添加的应用到内存字典
        foreach (var app in _layout.ManualApps)
        {
            _appsById.TryAdd(app.Id, app);
        }

        _icons = new ShellIconCache(dataDirectory);
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticePanel.Visibility = Visibility.Collapsed;
        };
        _folderHoverTimer.Tick += (_, _) =>
        {
            _folderHoverTimer.Stop();
            _makeFolderTargetId = _hoverTargetId;
            HighlightFolderTarget();
        };
        _edgeHoverTimer.Tick += (_, _) =>
        {
            _edgeHoverTimer.Stop();
            var direction = _edgeDirection;
            StopDragEdge();
            ResetFolderHover();
            ChangePage(_page + direction);
        };
        _folderTitleSaveTimer.Tick += (_, _) =>
        {
            _folderTitleSaveTimer.Stop();
            SaveLayout();
        };

        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_longPressButton is not null && Mouse.LeftButton == MouseButtonState.Pressed && !_state.IsSearching)
            {
                _state.SetEditing(true);
                _suppressClick = true;
                // Keep the pressed tile alive so capture and a following drag remain valid.
                UpdateEditControls();
            }
        };
        Root.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _longPressTimer.Stop();
            _longPressButton = null;
            if (_state.IsEditing) Dispatcher.BeginInvoke(() => { _suppressClick = false; RenderTiles(); });
        };
        Root.AllowDrop = true;
        Root.AddHandler(DragDrop.DragOverEvent, new DragEventHandler(Root_DragOver), handledEventsToo: true);
        Root.DragLeave += Root_DragLeave;
        Root.Drop += Root_Drop;
        SearchBox.Text = "";
        SearchPlaceholder.Visibility = Visibility.Visible;
        RenderTiles();
        if (upgradedLayout || layoutWasSynced)
        {
            SaveLayout();
        }
    }

    public string? StartupHotkeyNotice { get; private set; }

    public void Attach(IntPtr handle)
    {
        var host = new GlobalHotkeyHost(handle, ToggleLauncher, ShowLauncher);
        var error = host.TryChange(_layout.Hotkey);
        _hotkey = host;
        StartupHotkeyNotice = error;

        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowSourceHook);
    }

    public void SetTrayNotification(Action<string> showNotification)
    {
        _trayNotification = showNotification;
        if (!string.IsNullOrWhiteSpace(_pendingNotice))
        {
            showNotification(_pendingNotice);
            _pendingNotice = null;
        }
    }

    public void ShowSettings() => OpenSettings(LauncherPage.Hotkey);

    public void ShowAppManagement() => OpenSettings(LauncherPage.Apps);

    public void ShowNotice(string message)
    {
        if (!IsVisible)
        {
            if (_trayNotification is null)
            {
                _pendingNotice = message;
            }
            else
            {
                _trayNotification(message);
            }
            return;
        }

        NoticeText.Text = message;
        NoticePanel.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void ShowLauncher()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowLauncher);
            return;
        }

        _animationToken++;
        _iconSize = DesktopIconSize.Read();
        var fadeIn = !IsVisible;
        var monitor = Forms.Screen.FromPoint(Forms.Control.MousePosition);
        var bounds = monitor.WorkingArea;
        var dpiScale = GetMonitorDpiScale(monitor);
        var rectangle = LauncherGeometry.Center(bounds.X, bounds.Y, bounds.Width, bounds.Height, dpiScale.X, dpiScale.Y);
        Width = rectangle.Width / dpiScale.X;
        Height = rectangle.Height / dpiScale.Y;
        ShowActivated = true;
        if (fadeIn) Show();
        var handle = new WindowInteropHelper(this).Handle;
        // SetWindowPos uses physical pixels, including negative monitor origins.
        NativeMethods.SetWindowPos(handle, new IntPtr(-1), rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 0x0040 | 0x0010);
        ApplyBackdrop();
        NativeMethods.SetForegroundWindow(handle);
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        RenderTiles();
        AnimateOpen(fadeIn);

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey?.Dispose();
        _windowSource?.RemoveHook(WindowSourceHook);
        _windowSource = null;
        _noticeTimer.Stop();
        _folderHoverTimer.Stop();
        _edgeHoverTimer.Stop();
        _folderTitleSaveTimer.Stop();
        _longPressTimer.Stop();
    }

    private void ToggleLauncher()
    {
        if (IsVisible)
        {
            HideLauncher();
        }
        else
        {
            ShowLauncher();
        }
    }

    private void BackupAndUpgradeLayout()
    {
        try
        {
            // 备份旧版本布局
            var dataDir = IOPath.GetDirectoryName(_store.GetType().GetField("_directory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_store) as string ?? "");
            if (!string.IsNullOrEmpty(dataDir))
            {
                var primaryPath = IOPath.Combine(dataDir, "layout.json");
                var backupPath = IOPath.Combine(dataDir, $"layout.v{_layout.Version}.backup.json");
                if (File.Exists(primaryPath))
                {
                    File.Copy(primaryPath, backupPath, overwrite: true);
                }
            }

            // 清空旧版本自动扫描的应用，保留布局结构
            _layout.Version = 2;
            _layout.ManualApps = [];
            _layout.Items.Clear();
            ShowNotice("已升级到新版本，应用列表已清空。请在应用管理中手动添加应用。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 静默失败，继续使用默认空列表
        }
    }

    private void RenderTiles(bool resetPage = false)
    {
        if (!IsInitialized || TilesGrid is null) return;
        if (resetPage) { _page = 0; _selectedIndex = -1; }
        if (_openFolderId is not null && FindFolder(_openFolderId) is null) _openFolderId = null;
        var isSearching = _state.IsSearching;
        var entries = isSearching
            ? AppSearch.Filter(LayoutEditor.AppIds(_layout).Distinct(StringComparer.Ordinal)
                .Where(_appsById.ContainsKey).Select(id => _appsById[id]), _searchText)
                .Select(app => LayoutItem.ForApp(app.Id, app.Name)).ToList()
            : _openFolderId is null ? _layout.Items : FindFolder(_openFolderId)!.Children;
        var capacity = _columns * _rows;
        var pageCount = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)capacity));
        _page = Math.Clamp(_page, 0, pageCount - 1);
        _visibleTiles.Clear();
        _visibleTiles.AddRange(entries.Skip(_page * capacity).Take(capacity).Select(CreateTileView));
        var grid = ActiveTilesGrid;
        grid.Rows = _rows;
        grid.Columns = _columns;
        grid.Children.Clear();
        for (var i = 0; i < _visibleTiles.Count; i++) grid.Children.Add(CreateTileButton(_visibleTiles[i], i));
        if (_openFolderId is not null)
        {
            var rootMetrics = LauncherGeometry.Grid(Math.Max(132, GridArea.ActualWidth), Math.Max(124, GridArea.ActualHeight));
            TilesGrid.Columns = rootMetrics.Columns;
            TilesGrid.Rows = rootMetrics.Rows;
            TilesGrid.Children.Clear();
            foreach (var item in _layout.Items.Skip(_rootPage * rootMetrics.Columns * rootMetrics.Rows).Take(rootMetrics.Columns * rootMetrics.Rows))
                TilesGrid.Children.Add(new Button { Content = CreateTileContent(CreateTileView(item)), Style = (Style)FindResource("TileButtonStyle"), IsHitTestVisible = false });
        }
        FolderOverlay.Visibility = _openFolderId is null ? Visibility.Collapsed : Visibility.Visible;
        var needsApps = _layout.ManualApps.Count == 0 && !isSearching;
        EmptyState.Text = needsApps ? "把常用的项目放在这里" : "没有找到项目";
        EmptyHint.Text = needsApps ? "添加应用、文件或磁盘文件夹，集中管理常用入口。" : "换个名称试试，或清空搜索查看全部项目。";
        EmptyManageButton.Visibility = needsApps ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = entries.Count == 0 && _openFolderId is null ? Visibility.Visible : Visibility.Collapsed;
        TilesGrid.Visibility = entries.Count > 0 || _openFolderId is not null ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = isSearching ? $"搜索结果 · {entries.Count}" : "";
        SectionTitle.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        SectionHint.Text = _state.IsEditing ? "拖动调整顺序 · 停留在图标中央创建文件夹" : "";
        ClearSearchButton.Visibility = string.IsNullOrEmpty(_searchText) ? Visibility.Collapsed : Visibility.Visible;
        UpdateEditControls();
        RenderPageDots(pageCount);
    }

    private void UpdateEditControls()
    {
        EditButton.Content = FolderEditButton.Content = _state.IsEditing ? "完成" : "整理";
        EditButton.IsEnabled = !_state.IsSearching && _layout.ManualApps.Count > 0;
        SearchBox.IsReadOnly = _state.IsEditing;
    }

    private TileView CreateTileView(LayoutItem item)
    {
        if (item.Kind == LayoutItemKind.App)
        {
            _appsById.TryGetValue(item.Id, out var app);
            return new TileView(item, app, []);
        }

        var children = item.Children
            .Select(child => _appsById.TryGetValue(child.Id, out var app) ? app : null)
            .OfType<LaunchableApp>()
            .ToArray();
        return new TileView(item, null, children);
    }

    private Button CreateTileButton(TileView tile, int index)
    {
        var button = new Button
        {
            Tag = tile,
            Content = CreateTileContent(tile),
            Style = (Style)FindResource("TileButtonStyle"),
            AllowDrop = !_state.IsSearching,
            Focusable = false,
            Opacity = tile.Item.Id == _activeDragItemId ? .35 : 1,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tile.Item.Title);
        void FadeHover(bool visible)
        {
            if (button.Template.FindName("TileHoverSurface", button) is Border surface)
                surface.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, GetAnimationDuration(120)));
        }
        button.MouseEnter += (_, _) => FadeHover(true);
        button.MouseLeave += (_, _) => FadeHover(false);
        button.Click += Tile_Click;
        button.ContextMenu = CreateAppMenu(tile);
        button.ContextMenuOpening += (_, _) => _contextMenuOpen = true;
        button.ContextMenuClosing += (_, _) => _contextMenuOpen = false;
        if (button.AllowDrop)
        {
            button.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
            button.PreviewMouseMove += Tile_PreviewMouseMove;
            button.DragEnter += Tile_DragEnter;
            button.DragLeave += Tile_DragLeave;
            button.DragOver += Tile_DragOver;
            button.Drop += Tile_Drop;
        }

        if (_selectedIndex == _page * _columns * _rows + index)
        {
            button.BorderBrush = TryFindResource("Accent") as Brush;
            button.BorderThickness = new Thickness(2);
        }
        return button;
    }

    private UIElement CreateTileContent(TileView tile)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Border
        {
            Width = _iconSize + 4, Height = _iconSize + 4, CornerRadius = new CornerRadius(13),
            Background = tile.Item.Kind == LayoutItemKind.Folder ? (Brush)FindResource("FolderIconSurface") : Brushes.Transparent,
            BorderBrush = tile.Item.Kind == LayoutItemKind.Folder ? (Brush)FindResource("ControlBorder") : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = tile.Item.Kind == LayoutItemKind.Folder ? CreateFolderIcon(tile.FolderApps) : CreateAppIcon(tile.App),
        };
        var iconLayer = new Grid { Width = _iconSize + 18, Height = _iconSize + 8 };
        iconLayer.Children.Add(icon);
        if (_state.IsEditing)
        {
            var remove = new Button
            {
                Content = new Border
                {
                    Width = 10, Height = 2, CornerRadius = new CornerRadius(1),
                    Background = (Brush)FindResource("TextPrimary"), IsHitTestVisible = false,
                },
                Width = 23, Height = 23,
                Padding = new Thickness(0), Style = (Style)FindResource("QuietButton"),
                Background = (Brush)FindResource("SelectedControl"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                ToolTip = tile.Item.Kind == LayoutItemKind.Folder ? "解散文件夹" : "从启动台移除",
            };
            remove.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            remove.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; BeginRemove(tile.Item); };
            iconLayer.Children.Add(remove);
        }
        stack.Children.Add(iconLayer);
        stack.Children.Add(new TextBlock
        {
            Text = tile.Item.Title,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 13,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 7, 0, 0),
            MaxWidth = 120,
            MaxHeight = 36,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return stack;
    }

    private UIElement CreateAppIcon(LaunchableApp? app)
    {
        var image = new Image { Width = _iconSize, Height = _iconSize, Stretch = Stretch.Uniform, Tag = app?.Id, Visibility = Visibility.Collapsed };
        var layers = new Grid();
        layers.Children.Add(CreatePlaceholderIcon(10, _iconSize, _iconSize));
        layers.Children.Add(image);
        if (app is not null)
        {
            _ = LoadIconAsync(app, image);
        }

        return layers;
    }

    private UIElement CreateFolderIcon(IReadOnlyList<LaunchableApp> apps)
    {
        var icons = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(5) };
        foreach (var app in apps.Take(4))
        {
            var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), Tag = app.Id, Visibility = Visibility.Collapsed };
            var layers = new Grid();
            layers.Children.Add(CreatePlaceholderIcon(6, 22, 20));
            layers.Children.Add(image);
            icons.Children.Add(layers);
            _ = LoadIconAsync(app, image);
        }
        return icons;
    }

    private UIElement CreatePlaceholderIcon(double fontSize, double width = 50, double height = 50) => new Border
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(8),
        Background = (Brush)FindResource("PlaceholderSurface"),
        Child = new TextBlock
        {
            Text = "APP",
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private void RenderPageDots(int pageCount)
    {
        var dots = _openFolderId is null ? PageDots : FolderPageDots;
        dots.Children.Clear();
        dots.Visibility = pageCount < 2 ? Visibility.Collapsed : Visibility.Visible;
        if (pageCount < 2)
        {
            return;
        }

        for (var index = 0; index < pageCount; index++)
        {
            var selected = index == _page;
            var dot = new Border
            {
                Width = selected ? 20 : 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Margin = new Thickness(3, 0, 3, 0),
                Background = (Brush)FindResource("TextPrimary"),
                Opacity = selected ? .85 : .25,
            };
            dots.Children.Add(dot);
        }
    }

    private async void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileView tile })
        {
            return;
        }

        if (_suppressClick) { _suppressClick = false; return; }
        if (_state.IsEditing && tile.Item.Kind != LayoutItemKind.Folder) return;
        _selectedIndex = _page * _columns * _rows + ActiveTilesGrid.Children.IndexOf((Button)sender);
        if (tile.Item.Kind == LayoutItemKind.Folder)
        {
            _rootPage = _page;
            _openFolderId = tile.Item.Id;
            _searchText = "";
            _page = 0;
            _selectedIndex = -1;
            _updatingFolderName = true;
            FolderNameInput.Text = tile.Item.Title;
            _updatingFolderName = false;
            UpdateGridSize();
            RenderTiles();
            FolderNameInput.Focus();
            FolderNameInput.SelectAll();
            return;
        }

        if (tile.App is null)
        {
            ShowNotice("应用信息不可用，请在应用管理中重新添加。");
            return;
        }

        try
        {
            await _launcher.LaunchAsync(tile.App);
            HideLauncher();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowNotice(exception.Message);
        }
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: TileView tile } && string.IsNullOrEmpty(_searchText))
        {
            _dragSourceId = tile.Item.Id;
            _mouseDown = e.GetPosition(Root);
            _longPressButton = (Button)sender;
            _longPressTimer.Start();
        }
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourceId is null || e.LeftButton != MouseButtonState.Pressed || sender is not Button button)
        {
            return;
        }

        if (!_state.IsEditing) return;
        var current = e.GetPosition(Root);
        if (Math.Abs(current.X - _mouseDown.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _mouseDown.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _longPressTimer.Stop();
        var sourceId = _dragSourceId;
        _dragSourceId = null;
        e.Handled = true;
        var item = ((TileView)button.Tag).Item;
        _dragging = true;
        _activeDragItemId = sourceId;
        button.Opacity = .35;
        ShowDragPreview((TileView)button.Tag, current);
        try { DragDrop.DoDragDrop(Root, new DragPayload(sourceId, item.Kind), DragDropEffects.Move); }
        finally
        {
            _dragging = false;
            _suppressClick = true;
            Dispatcher.BeginInvoke(() => _suppressClick = false);
            ResetDragFeedback();
        }
        ClearDragHighlight();
        RenderTiles();
        _hoverTargetId = null;
        _makeFolderTargetId = null;
        _folderHoverTimer.Stop();
    }

    private void Tile_DragEnter(object sender, DragEventArgs e) => UpdateDragTarget(sender, e);

    private void Tile_DragOver(object sender, DragEventArgs e) => UpdateDragTarget(sender, e);

    private void Tile_DragLeave(object sender, DragEventArgs e)
    {
        ClearDragHighlight();
        if (sender is Button { Tag: TileView tile } && string.Equals(_hoverTargetId, tile.Item.Id, StringComparison.Ordinal))
        {
            _hoverTargetId = null;
            _makeFolderTargetId = null;
            _folderHoverTimer.Stop();
        }
    }

    private void Tile_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Button { Tag: TileView tile } || e.Data.GetData(typeof(DragPayload)) is not DragPayload payload)
        {
            return;
        }

        if (payload.ItemId == tile.Item.Id)
        {
            return;
        }

        if (!_state.IsEditing || _state.IsSearching) return;
        var overAppForLongEnough = string.Equals(_makeFolderTargetId, tile.Item.Id, StringComparison.Ordinal);
        var intoFolder = payload.Kind == LayoutItemKind.App && IsFolderDropZone((Button)sender, e.GetPosition((Button)sender))
            && (tile.Item.Kind == LayoutItemKind.Folder || overAppForLongEnough);
        var point = e.GetPosition((IInputElement)sender);
        var insertAfter = point.X > ((Button)sender).ActualWidth / 2;
        var moved = payload.Kind == LayoutItemKind.Folder
            ? LayoutEditor.MoveFolder(_layout, payload.ItemId, tile.Item.Id, insertAfter)
            : LayoutEditor.MoveApp(_layout, payload.ItemId, tile.Item.Id, intoFolder, insertAfter);
        if (moved)
        {
            SaveLayout();
            _selectedIndex = -1;
            RenderTiles();
        }
        e.Handled = true;
    }

    private void FolderReturnButton_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(DragPayload)) is DragPayload { Kind: LayoutItemKind.App }
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderReturnButton_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DragPayload)) is DragPayload { Kind: LayoutItemKind.App } payload
            && LayoutEditor.MoveOutOfFolder(_layout, payload.ItemId))
        {
            _openFolderId = null;
            SaveLayout();
            UpdateGridSize();
            RenderTiles();
        }
        e.Handled = true;
    }

    private void FolderReturnButton_Click(object sender, RoutedEventArgs e)
    {
        _openFolderId = null;
        _searchText = "";
        SearchBox.Text = "";
        _page = _rootPage;
        UpdateGridSize();
        RenderTiles();
        SearchBox.Focus();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DragPayload)) is not DragPayload || !string.IsNullOrEmpty(_searchText))
        {
            return;
        }

        if (!_state.IsEditing) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var point = e.GetPosition(Root);
        MoveDragPreview(point);
        if (UpdateDragEdge(point)) return;
        if (!e.Handled) HighlightGridInsertion(e.GetPosition(ActiveTilesGrid));
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        ResetDragEdgeIfOutside(e.GetPosition(Root));
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        StopDragEdge();
        if (!_state.IsEditing || _state.IsSearching || e.Data.GetData(typeof(DragPayload)) is not DragPayload payload) return;
        var destination = GetGridDropTarget(e.GetPosition(ActiveTilesGrid));
        if (destination is not { } placement) return;
        var target = placement.Item;
        var moved = payload.Kind == LayoutItemKind.Folder
            ? LayoutEditor.MoveFolder(_layout, payload.ItemId, target.Id, placement.InsertAfter)
            : LayoutEditor.MoveApp(_layout, payload.ItemId, target.Id, insertAfter: placement.InsertAfter);
        if (moved) { SaveLayout(); RenderTiles(); }
        e.Handled = true;
    }

    private void UpdateDragTarget(object sender, DragEventArgs e)
    {
        if (!_state.IsEditing || sender is not Button { Tag: TileView tile } || e.Data.GetData(typeof(DragPayload)) is not DragPayload payload
            || string.Equals(payload.ItemId, tile.Item.Id, StringComparison.Ordinal))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ClearDragHighlight();
        var targetButton = (Button)sender;
        targetButton.BorderBrush = (Brush)FindResource("Accent");
        targetButton.BorderThickness = e.GetPosition(targetButton).X < targetButton.ActualWidth / 2
            ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0);
        var isCenter = IsFolderDropZone(targetButton, e.GetPosition(targetButton));
        if (tile.Item.Kind == LayoutItemKind.Folder || payload.Kind == LayoutItemKind.Folder)
        {
            if (tile.Item.Kind == LayoutItemKind.Folder && payload.Kind == LayoutItemKind.App && isCenter) targetButton.BorderThickness = new Thickness(2);
            ResetFolderHover();
            return;
        }
        if (!isCenter)
        {
            _folderHoverTimer.Stop();
            _makeFolderTargetId = null;
            _hoverTargetId = null;
            return;
        }

        if (string.Equals(_makeFolderTargetId, tile.Item.Id, StringComparison.Ordinal)) targetButton.BorderThickness = new Thickness(2);
        if (!string.Equals(_hoverTargetId, tile.Item.Id, StringComparison.Ordinal))
        {
            _hoverTargetId = tile.Item.Id;
            _makeFolderTargetId = null;
            _folderHoverTimer.Stop();
            _folderHoverTimer.Start();
        }
    }

    private void SearchPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Focus();
        e.Handled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        if (_state.IsSearching) _state.SetEditing(false);
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderTiles(resetPage: true);
    }


    private void FolderNameInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFolderName || _openFolderId is null)
        {
            return;
        }

        var folder = FindFolder(_openFolderId);
        if (folder is null)
        {
            return;
        }

        var title = FolderNameInput.Text.Trim();
        folder.Title = string.IsNullOrWhiteSpace(title) ? "文件夹" : title;
        _folderTitleSaveTimer.Stop();
        _folderTitleSaveTimer.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isImeComposing)
        {
            return;
        }

        if (e.Key == Key.Down && string.IsNullOrWhiteSpace(_searchText) && _visibleTiles.Count > 0)
        {
            MoveSelection(1);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isImeComposing || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        if (_capturingHotkey) { CaptureHotkey(e); return; }
        if (e.Key == Key.Escape) { GoBack(); e.Handled = true; return; }
        if (_state.HasOverlay)
        {
            if (e.Key == Key.Enter) { ConfirmAction_Click(this, new RoutedEventArgs()); e.Handled = true; }
            return;
        }
        if (_state.Page != LauncherPage.Home || Keyboard.FocusedElement == FolderNameInput) return;
        switch (e.Key)
        {
            case Key.Down: MoveSelection(_columns); e.Handled = true; break;
            case Key.Up: MoveSelection(-_columns); e.Handled = true; break;
            case Key.Left when Keyboard.FocusedElement != SearchBox:
                MoveSelection(-1); e.Handled = true; break;
            case Key.Right when Keyboard.FocusedElement != SearchBox:
                MoveSelection(1); e.Handled = true; break;
            case Key.PageDown: ChangePage(_page + 1); e.Handled = true; break;
            case Key.PageUp: ChangePage(_page - 1); e.Handled = true; break;
            case Key.Enter when !_state.IsEditing: LaunchSelected(); e.Handled = true; break;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && !IsActive && !_modalOperation && !_dragging && !_contextMenuOpen) HideLauncher();
        });
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Page != LauncherPage.Home || _state.HasOverlay || _dragging) return;
        if (ReferenceEquals(e.OriginalSource, Root) || ReferenceEquals(e.OriginalSource, GridArea)
            || ReferenceEquals(e.OriginalSource, TilesGrid) || ReferenceEquals(e.OriginalSource, HomePage)
            || ReferenceEquals(e.OriginalSource, SearchHeader) || ReferenceEquals(e.OriginalSource, FolderTilesGrid)
            || ReferenceEquals(e.OriginalSource, FolderPanel))
        {
            if (_state.IsEditing)
            {
                FinishEditing();
                e.Handled = true;
                return;
            }
            if (_openFolderId is not null) return;
            if (SearchBox.IsKeyboardFocusWithin)
            {
                Root.Focus();
                e.Handled = true;
                return;
            }
            HideLauncher();
        }
    }

    private void Root_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state.Page != LauncherPage.Home || _state.HasOverlay || _dragging) return;
        ChangePage(_page + (e.Delta < 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGridSize();
    private void GridArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGridSize();

    private void UpdateGridSize()
    {
        if (!IsInitialized || GridArea is null) return;
        var width = ActualWidth > 0 ? ActualWidth : DefaultWindowWidth;
        var height = ActualHeight > 0 ? ActualHeight : DefaultWindowHeight;
        Brand.Visibility = width >= 820 ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.HorizontalAlignment = width >= 820 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SearchPanel.Width = Math.Max(160, Math.Min(360, width - (width >= 820 ? 520 : 290)));
        FolderPanel.Width = Math.Min(720, Math.Max(240, width - 80));
        FolderPanel.Height = Math.Min(510, Math.Max(240, height - 80));
        FolderTilesGrid.Height = Math.Max(124, FolderPanel.Height - 150);
        var rootGridWidth = GridArea.ActualWidth > 0 ? GridArea.ActualWidth : width - 68;
        var rootDimensions = LauncherGeometry.Grid(rootGridWidth,
            GridArea.ActualHeight > 0 ? GridArea.ActualHeight : height - 186);
        Brand.Width = rootGridWidth / rootDimensions.Columns;
        var dimensions = _openFolderId is null
            ? rootDimensions
            : (Math.Clamp((int)((FolderPanel.Width - 56) / 126), 1, 5),
               Math.Clamp((int)(FolderTilesGrid.Height / 120), 1, 3));
        if (dimensions.Item1 == _columns && dimensions.Item2 == _rows) return;
        _columns = dimensions.Item1;
        _rows = dimensions.Item2;
        _selectedIndex = -1;
        RenderTiles();
    }

    private void ChangePage(int page)
    {
        var itemCount = !string.IsNullOrWhiteSpace(_searchText)
            ? GetVisibleEntryCount()
            : _openFolderId is null ? _layout.Items.Count : FindFolder(_openFolderId)?.Children.Count ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(itemCount / (double)(_columns * _rows)));
        var nextPage = Math.Clamp(page, 0, pageCount - 1);
        if (nextPage == _page)
        {
            return;
        }

        var direction = nextPage > _page ? 1 : -1;
        _page = nextPage;
        _selectedIndex = -1;
        RenderTiles();
        ActiveTilesGrid.RenderTransform = new TranslateTransform(30 * direction, 0);
        ActiveTilesGrid.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30 * direction, 0, GetAnimationDuration(180))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
    }

    private void MoveSelection(int offset)
    {
        var count = GetVisibleEntryCount();
        if (count == 0)
        {
            return;
        }

        var next = _selectedIndex < 0 ? (offset < 0 ? count - 1 : 0) : Math.Clamp(_selectedIndex + offset, 0, count - 1);
        _selectedIndex = next;
        _page = next / (_columns * _rows);
        RenderTiles();
    }

    private int GetVisibleEntryCount()
    {
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            return AppSearch.Filter(LayoutEditor.AppIds(_layout).Distinct(StringComparer.Ordinal)
                .Where(_appsById.ContainsKey).Select(id => _appsById[id]), _searchText).Count;
        }

        return _openFolderId is null ? _layout.Items.Count : FindFolder(_openFolderId)?.Children.Count ?? 0;
    }

    private void LaunchSelected()
    {
        if (_visibleTiles.Count == 0)
        {
            return;
        }

        var index = _selectedIndex < 0 ? 0 : _selectedIndex % (_columns * _rows);
        index = Math.Clamp(index, 0, _visibleTiles.Count - 1);
        var tile = _visibleTiles[index];
        if (tile.Item.Kind == LayoutItemKind.Folder)
        {
            Tile_Click(ActiveTilesGrid.Children[index], new RoutedEventArgs());
        }
        else
        {
            Tile_Click(ActiveTilesGrid.Children[index], new RoutedEventArgs());
        }
    }

    private void HideLauncher()
    {
        if (!IsVisible)
        {
            return;
        }

        var token = ++_animationToken;
        var scale = ContentScale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.97, GetAnimationDuration(180))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.97, GetAnimationDuration(180))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
        var fade = new DoubleAnimation(AnimatedContent.Opacity, 0, GetAnimationDuration(180));
        fade.Completed += (_, _) =>
        {
            if (token == _animationToken)
            {
                Hide();
                AnimatedContent.Opacity = 1;
                scale.ScaleX = 0.97;
                scale.ScaleY = 0.97;
                CancelHotkeyCapture();
                _state.Page = LauncherPage.Home;
                _state.SetEditing(false);
                _state.HasOverlay = false;
                ActionOverlay.Visibility = Visibility.Collapsed;
                SettingsPage.Visibility = Visibility.Collapsed;
                HomePage.Visibility = Visibility.Visible;
                _openFolderId = null;
                _searchText = "";
                if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();
                RenderTiles(resetPage: true);
            }
        };
        AnimatedContent.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateOpen(bool fadeIn)
    {
        AnimatedContent.BeginAnimation(OpacityProperty, null);
        var scale = ContentScale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (fadeIn)
        {
            AnimatedContent.Opacity = 0;
            AnimatedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, GetAnimationDuration(210)));
        }
        else
        {
            AnimatedContent.Opacity = 1;
        }

        if (scale.ScaleX >= 1)
        {
            return;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, GetAnimationDuration(210))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, GetAnimationDuration(210))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
    }

    private void SaveLayout()
    {
        try
        {
            _store.Save(_layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowNotice($"无法保存启动台布局：{exception.Message}");
        }
    }

    private string? SaveHotkey(string binding)
    {
        var error = _hotkey?.TryChange(binding);
        if (_hotkey is null)
        {
            return "全局快捷键尚未准备好，请稍后再试。";
        }
        if (error is not null)
        {
            return error;
        }

        var previous = _layout.Hotkey;
        _layout.Hotkey = binding;
        try
        {
            _store.Save(_layout);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _hotkey.TryChange(previous);
            _layout.Hotkey = previous;
            return $"无法保存设置：{exception.Message}";
        }
    }

    private LayoutItem? FindFolder(string id) => _layout.Items.FirstOrDefault(item =>
        item.Kind == LayoutItemKind.Folder && string.Equals(item.Id, id, StringComparison.Ordinal));

    private static TimeSpan GetAnimationDuration(int milliseconds) =>
        SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;

    private ScaleTransform ContentScale => (ScaleTransform)AnimatedContent.RenderTransform;

    private static (double X, double Y) GetMonitorDpiScale(Forms.Screen screen)
    {
        try
        {
            var bounds = screen.Bounds;
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.NativePoint
            {
                X = bounds.Left + bounds.Width / 2,
                Y = bounds.Top + bounds.Height / 2,
            }, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
            {
                return (dpiX / 96d, dpiY / 96d);
            }
        }
        catch (DllNotFoundException)
        {
        }
        return (1, 1);
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private async Task LoadIconAsync(LaunchableApp app, Image image)
    {
        try
        {
            var source = await _icons.GetAsync(app);
            if (!_disposed && source is not null && string.Equals(image.Tag as string, app.Id, StringComparison.Ordinal))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    image.Source = source;
                    if (image.Parent is Grid layers && layers.Children.Count > 1) layers.Children[0].Visibility = Visibility.Collapsed;
                    image.Visibility = Visibility.Visible;
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or FormatException)
        {
        }
    }

    private IntPtr WindowSourceHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x031A) ApplyBackdrop();
        if (message == 0x010D)
        {
            _isImeComposing = true;
        }
        else if (message == 0x010E)
        {
            _isImeComposing = false;
        }

        return IntPtr.Zero;
    }

    private sealed record TileView(LayoutItem Item, LaunchableApp? App, IReadOnlyList<LaunchableApp> FolderApps);
    private sealed record DragPayload(string ItemId, LayoutItemKind Kind);
}
