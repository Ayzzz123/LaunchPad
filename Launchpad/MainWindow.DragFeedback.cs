using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Launchpad.Core;

namespace Launchpad;

public partial class MainWindow
{
    private string? _activeDragItemId;

    private bool UpdateDragEdge(Point point)
    {
        var edgeWidth = Math.Clamp(Root.ActualWidth * .09, 76, 108);
        var direction = point.X < edgeWidth ? -1 : point.X > Root.ActualWidth - edgeWidth ? 1 : 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(GetVisibleEntryCount() / (double)(_columns * _rows)));
        if (point.Y < 0 || point.Y > Root.ActualHeight || direction == 0
            || _page + direction < 0 || _page + direction >= pageCount)
        {
            StopDragEdge();
            return false;
        }

        ResetFolderHover();
        ClearDragHighlight();
        if (_edgeDirection == direction && _edgeHoverTimer.IsEnabled) return true;
        StopDragEdge();
        _edgeDirection = direction;
        var panel = direction < 0 ? DragPreviousPage : DragNextPage;
        var progress = direction < 0 ? PreviousDwellBar : NextDwellBar;
        panel.Visibility = Visibility.Visible;
        progress.BeginAnimation(WidthProperty, new DoubleAnimation(0, 30,
            SystemParameters.ClientAreaAnimation ? _edgeHoverTimer.Interval : TimeSpan.Zero));
        _edgeHoverTimer.Start();
        return true;
    }

    private void StopDragEdge()
    {
        _edgeHoverTimer.Stop();
        _edgeDirection = 0;
        DragPreviousPage.Visibility = DragNextPage.Visibility = Visibility.Collapsed;
        PreviousDwellBar.BeginAnimation(WidthProperty, null);
        NextDwellBar.BeginAnimation(WidthProperty, null);
    }

    private void ResetDragEdgeIfOutside(Point point)
    {
        // DragLeave also bubbles when moving between tiles inside the window.
        if (point.X >= 0 && point.Y >= 0 && point.X <= Root.ActualWidth && point.Y <= Root.ActualHeight) return;
        StopDragEdge();
        ResetFolderHover();
        ClearDragHighlight();
        DragPreview.Visibility = Visibility.Collapsed;
    }

    private void ResetFolderHover()
    {
        _folderHoverTimer.Stop();
        _hoverTargetId = _makeFolderTargetId = null;
    }

    private void ShowDragPreview(TileView tile, Point point)
    {
        DragPreviewContent.Children.Clear();
        DragPreviewContent.Children.Add(new Border
        {
            Width = _iconSize, Height = _iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = tile.Item.Kind == LayoutItemKind.Folder ? CreateFolderIcon(tile.FolderApps) : CreateAppIcon(tile.App),
        });
        DragPreviewContent.Children.Add(new TextBlock
        {
            Text = tile.Item.Title, FontSize = 11, Margin = new Thickness(0, 7, 0, 0),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        MoveDragPreview(point);
    }

    private void MoveDragPreview(Point point)
    {
        if (_activeDragItemId is null) return;
        DragPreview.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragPreview, Math.Clamp(point.X + 18, 4, Math.Max(4, Root.ActualWidth - 104)));
        Canvas.SetTop(DragPreview, Math.Clamp(point.Y + 14, 4, Math.Max(4, Root.ActualHeight - _iconSize - 56)));
    }

    private void ResetDragFeedback()
    {
        StopDragEdge();
        ResetFolderHover();
        _activeDragItemId = null;
        DragPreview.Visibility = Visibility.Collapsed;
        DragPreviewContent.Children.Clear();
        ClearDragHighlight();
    }

    private bool IsFolderDropZone(Button button, Point point)
    {
        if (button.Content is not StackPanel stack || stack.Children[0] is not Grid iconLayer) return false;
        var center = iconLayer.TranslatePoint(new Point(iconLayer.ActualWidth / 2, iconLayer.ActualHeight / 2), button);
        return Math.Abs(point.X - center.X) <= _iconSize * .35 && Math.Abs(point.Y - center.Y) <= _iconSize * .35;
    }

    private (LayoutItem Item, bool InsertAfter)? GetGridDropTarget(Point point)
    {
        var grid = ActiveTilesGrid;
        if (grid.ActualWidth <= 0 || grid.ActualHeight <= 0 || point.X < 0 || point.Y < 0
            || point.X > grid.ActualWidth || point.Y > grid.ActualHeight) return null;
        var entries = _openFolderId is null ? _layout.Items : FindFolder(_openFolderId)?.Children ?? [];
        if (entries.Count == 0) return null;
        var cellWidth = grid.ActualWidth / _columns;
        var column = Math.Min(_columns - 1, (int)(point.X / cellWidth));
        var row = Math.Min(_rows - 1, (int)(point.Y / (grid.ActualHeight / _rows)));
        var slot = _page * _columns * _rows + row * _columns + column;
        return (entries[Math.Min(slot, entries.Count - 1)], slot >= entries.Count || point.X - column * cellWidth > cellWidth / 2);
    }

    private void HighlightGridInsertion(Point point)
    {
        ResetFolderHover();
        ClearDragHighlight();
        if (GetGridDropTarget(point) is not { } placement || placement.Item.Id == _activeDragItemId) return;
        var button = ActiveTilesGrid.Children.OfType<Button>().FirstOrDefault(b => b.Tag is TileView tile && tile.Item.Id == placement.Item.Id);
        if (button is null) return;
        button.BorderBrush = (Brush)FindResource("Accent");
        button.BorderThickness = placement.InsertAfter ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
    }
}
