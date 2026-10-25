using Lanternly.Core;
using D = System.Drawing;
using F = System.Windows.Forms;
namespace Lanternly.Win;

/// <summary>Tray icon: left-click opens; menu has All on / All off, scenes, Quit.</summary>
public sealed class Tray : IDisposable
{
    readonly F.NotifyIcon _icon = new() { Text = "Lanternly" };
    public event Action? Open, Quit;
    public event Action<bool>? AllPower;
    public event Action<Scenes.Scene>? Scene;

    public Tray()
    {
        var m = new F.ContextMenuStrip();
        m.Items.Add("Open Lanternly", null, (_, _) => Open?.Invoke());
        m.Items.Add(new F.ToolStripSeparator());
        m.Items.Add("All on", null, (_, _) => AllPower?.Invoke(true));
        m.Items.Add("All off", null, (_, _) => AllPower?.Invoke(false));
        var sc = new F.ToolStripMenuItem("Scenes");
        foreach (var s in Scenes.All.Where(s => s.Name != "Off")) { var x = s; sc.DropDownItems.Add(s.Name, null, (_, _) => Scene?.Invoke(x)); }
        m.Items.Add(sc);
        m.Items.Add(new F.ToolStripSeparator());
        m.Items.Add("Quit", null, (_, _) => Quit?.Invoke());
        _icon.ContextMenuStrip = m;
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/lanternly.ico"));
            _icon.Icon = res != null ? new D.Icon(res.Stream, F.SystemInformation.SmallIconSize) : D.SystemIcons.Application;
        }
        catch { _icon.Icon = D.SystemIcons.Application; }
        _icon.MouseClick += (_, e) => { if (e.Button == F.MouseButtons.Left) Open?.Invoke(); };
        _icon.Visible = true;
    }
    public void Status(string s) => _icon.Text = s.Length > 63 ? s[..63] : s;
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
