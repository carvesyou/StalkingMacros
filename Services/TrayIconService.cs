using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace D2MacroNative.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public TrayIconService(Action show, Action panic, Action exit)
    {
        _icon = LoadApplicationIcon() ?? ExtractShellIcon(283) ?? (Icon)SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(14, 18, 24),
            ForeColor = Color.Gainsboro,
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable())
        };
        menu.Items.Add("Open /stalking macro", null, (_, _) => show());
        menu.Items.Add("Panic / release all", null, (_, _) => panic());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.BackColor = Color.FromArgb(14, 18, 24);
            item.ForeColor = Color.Gainsboro;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "/stalking macro",
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executablePath) ? null : Icon.ExtractAssociatedIcon(executablePath);
        }
        catch
        {
            return null;
        }
    }

    private static Icon? ExtractShellIcon(int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx("shell32.dll", index, large, small, 1) == 0) return null;
        var handle = small[0] != IntPtr.Zero ? small[0] : large[0];
        if (handle == IntPtr.Zero) return null;
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]);
        }
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(14, 18, 24);
        public override Color ImageMarginGradientBegin => Color.FromArgb(14, 18, 24);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(14, 18, 24);
        public override Color ImageMarginGradientEnd => Color.FromArgb(14, 18, 24);
        public override Color MenuItemSelected => Color.FromArgb(31, 38, 51);
        public override Color MenuItemBorder => Color.FromArgb(57, 68, 87);
        public override Color MenuBorder => Color.FromArgb(42, 51, 65);
        public override Color SeparatorDark => Color.FromArgb(42, 51, 65);
        public override Color SeparatorLight => Color.FromArgb(42, 51, 65);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
