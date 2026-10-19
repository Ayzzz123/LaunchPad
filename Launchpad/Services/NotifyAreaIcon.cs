using Forms = System.Windows.Forms;

namespace Launchpad.Services;

internal sealed class NotifyAreaIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    public NotifyAreaIcon(Action open, Action manageApps, Action settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开启动台", null, (_, _) => open());
        menu.Items.Add("项目管理", null, (_, _) => manageApps());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Launchpad", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Text = "Launchpad",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) open();
        };
    }

    public void ShowNotice(string message) => _icon.ShowBalloonTip(3500, "Launchpad", message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _icon.ContextMenuStrip?.Dispose();
    }
}
