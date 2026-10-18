using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Vigil.Desktop;

public enum TrayState { Idle, Busy, Waiting, Listening }

/// <summary>The icon by the clock. Idle carries no badge - a badge that is always
/// there stops meaning anything.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Icon _base;
    readonly Dictionary<TrayState, Icon> _drawn = new();
    readonly List<IntPtr> _handles = new();
    readonly Forms.ContextMenuStrip _menu;

    public TrayState State { get; private set; } = TrayState.Idle;

    public event Action? Clicked;
    /// <summary>Raised just before the menu opens, so it can show current state.</summary>
    public event Action<Forms.ContextMenuStrip>? MenuOpening;

    public Tray(bool visible = true)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vigil.ico"))!.Stream;
        _base = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new Matte()),
            BackColor = Color.FromArgb(0x0E, 0x0E, 0x10),
            ForeColor = Color.FromArgb(0xF4, 0xF4, 0xF6),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f),
        };
        _menu.Opening += (_, _) => MenuOpening?.Invoke(_menu);
        _icon = new Forms.NotifyIcon { Icon = _base, Text = "Vigil", Visible = visible, ContextMenuStrip = _menu };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };
    }

    public static Forms.ToolStripMenuItem Item(string text, Action onClick, bool? check = null)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = Color.FromArgb(0xF4, 0xF4, 0xF6) };
        if (check is { } on) item.Checked = on;
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Set(TrayState state)
    {
        if (state == State) return;
        State = state;
        try { _icon.Icon = state == TrayState.Idle ? _base : Badged(state); }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentException or ObjectDisposedException) { return; }
        _icon.Text = state switch
        {
            TrayState.Busy => "Vigil - working",
            TrayState.Waiting => "Vigil - waiting for you",
            TrayState.Listening => "Vigil - listening",
            _ => "Vigil",
        };
    }

    public void Tell(string title, string text)
    {
        if (_icon.Visible) _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);
    }

    Icon Badged(TrayState state)
    {
        if (_drawn.TryGetValue(state, out var icon)) return icon;
        using var bitmap = _base.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var size = bitmap.Width * 0.42f;
            var x = bitmap.Width - size - 0.5f;
            var y = bitmap.Height - size - 0.5f;
            var colour = state switch
            {
                TrayState.Waiting => Color.FromArgb(0xCF, 0xAE, 0x6A),
                TrayState.Listening => Color.FromArgb(0x7F, 0xB2, 0x89),
                _ => Color.FromArgb(0xEC, 0xEC, 0xEF),
            };
            using var ring = new SolidBrush(Color.FromArgb(0x06, 0x06, 0x07));
            using var dot = new SolidBrush(colour);
            g.FillEllipse(ring, x - 1, y - 1, size + 2, size + 2);
            g.FillEllipse(dot, x, y, size, size);
        }
        // an icon made from a handle cannot be cloned safely; keep the handle alive instead
        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        icon = Icon.FromHandle(handle);
        _drawn[state] = icon;
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _drawn.Values) icon.Dispose();
        foreach (var handle in _handles) DestroyIcon(handle);
        _base.Dispose();
    }

    /// <summary>The menu in the same near-black as the bar.</summary>
    sealed class Matte : Forms.ProfessionalColorTable
    {
        static readonly Color Surface = Color.FromArgb(0x0E, 0x0E, 0x10);
        static readonly Color Hover = Color.FromArgb(0x24, 0x24, 0x28);
        static readonly Color Edge = Color.FromArgb(0x2A, 0x2A, 0x2E);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Edge;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Edge;
        public override Color SeparatorLight => Surface;
        public override Color CheckBackground => Surface;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
