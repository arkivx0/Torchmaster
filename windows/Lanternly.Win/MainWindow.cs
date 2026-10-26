using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lanternly.Core;
namespace Lanternly.Win;

public sealed class MainWindow : Window
{
    readonly bool _demo;
    Vault _vault; readonly Settings _set;
    readonly Tray _tray = new();
    readonly TextBlock _status = new() { Foreground = Br("Dim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ContentControl _body = new();
    readonly ComboBox _homePick = new() { MinWidth = 120, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
    BleSession? _ble; DemoLink? _demoLink;
    Router _router = new([]);
    readonly Dictionary<Bulb, BulbRow> _rows = [];
    bool _settingsOpen, _quitting;
    /// <summary>Active "find" blinks, at window level so they survive row rebuilds and are stopped on quit.</summary>
    readonly Blinks<Bulb> _blinks = new();
    readonly Dictionary<Bulb, Home> _blinkHome = [];
    /// <summary>Schedules run by an in-app timer while Lanternly runs (window or tray).</summary>
    List<Schedule> _schedules;
    readonly Dictionary<long, DateTimeOffset> _due = [];
    readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    bool _schedulesOpen;
    readonly SemaphoreSlim _scheduleGate = new(1, 1); // one schedule run at a time

    Home? CurrentHome => _vault.Homes.Count == 0 ? null : _vault.Homes[Math.Clamp(_set.HomeIndex, 0, _vault.Homes.Count - 1)];

    public MainWindow(bool demo)
    {
        _demo = demo;
        _set = demo ? new Settings() : Store.LoadSettings();
        _vault = demo ? DemoLink.SampleVault() : Store.LoadVault();
        _schedules = demo ? [] : Store.LoadSchedules();
        Title = "Lanternly"; Width = 440; Height = 640; MinWidth = 360; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/lanternly.ico")); } catch (Exception ex) { Program.Log(ex, "icon"); }
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        var top = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        var gear = new Button { Content = "⚙", ToolTip = "Settings", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0) };
        gear.Click += (_, _) => { _settingsOpen = !_settingsOpen; _schedulesOpen = false; Render(); };
        DockPanel.SetDock(gear, Dock.Right); top.Children.Add(gear);
        var clock = new Button { Content = "⏰", ToolTip = "Schedules", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(clock, "Schedules");
        clock.Click += (_, _) => { _schedulesOpen = !_schedulesOpen; _settingsOpen = false; Render(); };
        DockPanel.SetDock(clock, Dock.Right); top.Children.Add(clock);
        DockPanel.SetDock(_homePick, Dock.Right); top.Children.Add(_homePick);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Lanternly", FontSize = 18, FontWeight = FontWeights.SemiBold });
        title.Children.Add(_status);
        top.Children.Add(title);
        _homePick.SelectionChanged += async (_, _) => { if (_homePick.SelectedIndex >= 0 && _homePick.SelectedIndex != _set.HomeIndex) { await StopAllBlinks(); _set.HomeIndex = _homePick.SelectedIndex; Save(); await Reconnect(); } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 0, 14, 14) });
        Content = root;

        _tray.Open += ShowFromTray;
        _tray.Quit += () => { _quitting = true; Close(); };
        _tray.AllPower += on => RunBatch(async () => CurrentHome is { } h ? await _router.SendManyAsync(h, h.Bulbs, new Cmd.Power(on)) : new BatchResult(0, 0, null));
        _tray.Scene += s => RunBatch(async () => CurrentHome is { } h ? await Scenes.ApplyAsync(_router, h, s) : new BatchResult(0, 0, null));
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        Closed += async (_, _) => { _clock.Stop(); Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged; _tray.Dispose(); await StopAllBlinks(); await Teardown(); Application.Current.Shutdown(); };
        Loaded += async (_, _) =>
        {
            if (Store.Quarantined is { } q)
                MessageBox.Show(this, $"Lanternly could not read its stored keys, so it moved them aside to:\n{q}\n\nNothing will be saved until you import a key file (⚙ → Import key file…) or choose Forget everything. Your bulbs are not affected.", "Lanternly: stored keys unreadable", MessageBoxButton.OK, MessageBoxImage.Error);
            await Reconnect();
        };
        Replan();
        _clock.Tick += (_, _) => CheckSchedules();
        _clock.Start();
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
        Render();
    }

    // ---------------- schedules ----------------
    void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { TimeZoneInfo.ClearCachedData(); Replan(); });

    /// <summary>Recomputes every next run from now, so nothing that was due earlier fires late.</summary>
    void Replan()
    {
        _due.Clear();
        var now = DateTimeOffset.Now;
        foreach (var s in _schedules.Where(s => s.Enabled)) if (Schedules.NextFire(s, now, TimeZoneInfo.Local) is { } t) _due[s.Id] = t;
    }

    /// <summary>Runs schedules that are due. A run more than a few minutes late (PC asleep, clock changed) is skipped.</summary>
    void CheckSchedules()
    {
        var now = DateTimeOffset.Now;
        foreach (var s in _schedules.Where(s => s.Enabled).ToList())
        {
            if (!_due.TryGetValue(s.Id, out var due) || due > now) continue;
            if (Schedules.OnTime(due, now)) RunSchedule(s);
            else Program.Log(null, "schedule skipped (missed)");
            if (Schedules.NextFire(s, now, TimeZoneInfo.Local) is { } t) _due[s.Id] = t; else _due.Remove(s.Id);
        }
    }

    async void RunSchedule(Schedule s)
    {
        var label = $"Schedule {s.Hour:00}:{s.Minute:00} ({Schedules.ActionText(s.Action)})";
        var h = _vault.Homes.FirstOrDefault(x => x.Id == s.HomeId);
        if (h == null) { SetStatus(label + ": its home was removed"); return; }
        if (h != CurrentHome) { SetStatus(label + ": skipped, it is for another home"); return; }
        await _scheduleGate.WaitAsync();
        try
        {
            if (!_demo && _ble?.LoggedIn != true) await Reconnect();
            var r = await Schedules.RunAsync(_router, h, s);
            SetStatus($"{label}: {r}");
        }
        catch (Exception ex) { Program.Log(ex, "schedule"); SetStatus($"{label}: {ex.Message}"); }
        finally { _scheduleGate.Release(); UpdateRows(); }
    }

    void SaveSchedules()
    {
        if (!_demo) try { Store.SaveSchedules(_schedules); } catch (Exception ex) { Program.Log(ex, "schedules save"); SetStatus("Could not save schedules: " + ex.Message); }
        Replan();
    }

    UIElement SchedulesPanel()
    {
        var p = new StackPanel();
        var c = new StackPanel();
        c.Children.Add(new TextBlock { Text = "Schedules", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 4) });
        c.Children.Add(new TextBlock { Text = "Schedules run on this PC only while Lanternly is running (closing the window keeps it in the tray), the PC is awake, Bluetooth is on and a bulb is in range. A run missed by more than 5 minutes is skipped, not run late.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 11, Margin = new Thickness(0, 2, 0, 8) });
        if (_schedules.Count == 0) c.Children.Add(new TextBlock { Text = "No schedules yet.", Foreground = Br("Dim"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var s in _schedules)
        {
            var sc = s; var home = _vault.Homes.FirstOrDefault(x => x.Id == s.HomeId);
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var del = new Button { Content = "Delete", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(del, "Delete schedule " + Schedules.Describe(home, s));
            del.Click += (_, _) => { if (MessageBox.Show(this, "Delete this schedule?\n" + Schedules.Describe(home, sc), "Lanternly", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; _schedules.Remove(sc); SaveSchedules(); Render(); };
            DockPanel.SetDock(del, Dock.Right); line.Children.Add(del);
            var on = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Enabled" };
            System.Windows.Automation.AutomationProperties.SetName(on, "Enabled: " + Schedules.Describe(home, s));
            on.Click += (_, _) => { sc.Enabled = on.IsChecked == true; SaveSchedules(); };
            DockPanel.SetDock(on, Dock.Right); line.Children.Add(on);
            line.Children.Add(new TextBlock { Text = Schedules.Describe(home, s) + (_vault.Homes.Count > 1 && home != null ? $" ({home.Name})" : ""), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            c.Children.Add(line);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = new Button { Content = "Add schedule…", Margin = new Thickness(0, 0, 6, 0), IsEnabled = CurrentHome != null };
        add.Click += (_, _) =>
        {
            if (CurrentHome is not { } h) return;
            if (Prompts.NewSchedule(this, h) is { } ns) { ns.Id = DateTime.UtcNow.Ticks; ns.HomeId = h.Id; _schedules.Add(ns); SaveSchedules(); Render(); }
        };
        var done = new Button { Content = "Done", Style = (Style)FindResource("AccentButton") };
        done.Click += (_, _) => { _schedulesOpen = false; Render(); };
        row.Children.Add(add); row.Children.Add(done);
        c.Children.Add(row);
        if (CurrentHome == null) c.Children.Add(new TextBlock { Text = "Add or import bulbs first.", Foreground = Br("Dim"), FontSize = 11 });
        p.Children.Add(Card(c));
        return p;
    }

    void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    /// <summary>Saves settings and vault. Returns false (and says why) when saving is blocked after a vault read failure.</summary>
    bool Save()
    {
        if (_demo) return true;
        try { Store.SaveSettings(_set); }
        catch (Exception ex) { Program.Log(ex, "settings save"); SetStatus("Could not save settings: " + ex.Message); }
        if (Store.Quarantined != null) { SetStatus("Not saved: stored keys were unreadable. Import a key file or Forget everything."); return false; }
        try { Store.SaveVault(_vault); return true; }
        catch (Exception ex) { Program.Log(ex, "vault save"); MessageBox.Show(this, "Could not save keys: " + ex.Message, "Lanternly", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    static Brush Br(string key) => (Brush)Application.Current.Resources[key];

    async void Run(Func<Task> f) { try { await f(); } catch (Exception ex) { Program.Log(ex, "action"); SetStatus(ex.Message); } UpdateRows(); }
    async void RunBatch(Func<Task<BatchResult>> f)
    {
        try { var r = await f(); if (r.Total > 1 || !r.AllOk) SetStatus(r.ToString()); }
        catch (Exception ex) { Program.Log(ex, "batch"); SetStatus(ex.Message); }
        UpdateRows();
    }

    // ---------------- find this bulb ----------------
    /// <summary>Sends a stop to every bulb still blinking (used before quitting).</summary>
    async Task StopAllBlinks()
    {
        foreach (var b in _blinks.Drain())
        {
            if (!_blinkHome.Remove(b, out var h)) continue;
            try { await Send(h, b, new Cmd.Identify(false)).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { Program.Log(ex, "blink stop on quit"); }
        }
    }

    void ToggleBlink(Home h, Bulb b)
    {
        if (_blinks.Stop(b))
        {
            _blinkHome.Remove(b); UpdateRows();
            Run(async () => { await Send(h, b, new Cmd.Identify(false)); SetStatus($"Stopped blinking {b.Name}"); });
            return;
        }
        var token = _blinks.Start(b); _blinkHome[b] = h; UpdateRows();
        Run(async () =>
        {
            try { await Send(h, b, new Cmd.Identify(true)); }
            catch (Exception ex)
            {
                if (_blinks.IsActive(b) && !token.IsCancellationRequested) { _blinks.Stop(b); _blinkHome.Remove(b); }
                throw new IOException($"{b.Name}: not reachable ({ex.Message})", ex);
            }
            if (!token.IsCancellationRequested) SetStatus($"Blinking {b.Name}… click ◎ again to stop");
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
            if (_blinks.Expire(b, token)) { _blinkHome.Remove(b); UpdateRows(); await Send(h, b, new Cmd.Identify(false)); SetStatus($"Stopped blinking {b.Name}"); }
        });
    }

    // ---------------- connections ----------------
    async Task Teardown()
    {
        if (_ble != null) { await _ble.DisposeAsync(); _ble = null; }
    }

    async Task Reconnect()
    {
        await Teardown();
        var h = CurrentHome;
        if (_demo) { _demoLink ??= new DemoLink(); _demoLink.StateChanged += OnState; _router = new([_demoLink]); Render(); SetStatus("Demo · BLE (sample data)"); return; }
        if (h == null) { _router = new([]); SetStatus("No bulbs yet: open ⚙ to add a reset bulb or import keys"); Render(); return; }
        var links = new List<ILink>();
        _router = new(links);
        Render(); UpdateStatus();
        if (_set.Ble)
        {
            try { _ble = await WinGatt.ConnectMeshAsync(h, s => Dispatcher.BeginInvoke(() => SetStatus(s))); _ble.StateChanged += OnState; links.Add(_ble); }
            catch (Exception ex) { SetStatus("Bluetooth: " + ex.Message); }
        }
        Render();
        _router = new(links);
        foreach (var b in h.Bulbs) b.Via = _router.Pick(h, b)?.Kind ?? Via.None;
        UpdateStatus(); UpdateRows();
    }

    void UpdateStatus()
    {
        var parts = new List<string>();
        if (_set.Ble) parts.Add(_ble?.LoggedIn == true ? "BLE ✓" : "BLE –");
        var s = parts.Count == 0 ? "Bluetooth is off" : string.Join(" · ", parts) + " · offline only";
        if (Store.Quarantined != null) s = "⚠ Stored keys unreadable, saving blocked · " + s;
        else if (!_set.BackedUp && !_demo) s = "⚠ Export a key file backup (⚙) · " + s;
        SetStatus(s);
        if (CurrentHome is { } h) foreach (var b in h.Bulbs) b.Via = _router.Pick(h, b)?.Kind ?? Via.None;
        UpdateRows();
    }
    void SetStatus(string s) { _status.Text = s; _tray.Status("Lanternly · " + s); }

    void OnState(Home? h, BulbState s) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var b in (h ?? CurrentHome)?.Bulbs.Where(b => b.MeshId == s.MeshId) ?? []) b.State = s;
        UpdateRows();
    });

    async Task Send(Home h, Bulb b, Cmd c)
    {
        await _router.SendAsync(h, b, c);
        if (c is Cmd.Identify) return; // identify does not change on/brightness/color
        var st = b.State ?? new BulbState(b.MeshId, true, false, 50, 50, (0, 0, 0));
        b.State = c switch
        {
            Cmd.Power p => st with { On = p.On },
            Cmd.Brightness x => st with { Brightness = x.Pct, On = x.Pct > 0 },
            Cmd.Temp t => st with { Mode = t.Ct },
            Cmd.Rgb r => st with { Mode = 0xfe, Rgb = (r.R, r.G, r.B) },
            _ => st,
        };
    }

    // ---------------- UI ----------------
    void Render()
    {
        _rows.Clear();
        _homePick.ItemsSource = _vault.Homes.Select(h => h.Name).ToList();
        _homePick.SelectedIndex = _vault.Homes.Count == 0 ? -1 : Math.Clamp(_set.HomeIndex, 0, _vault.Homes.Count - 1);
        _homePick.Visibility = _vault.Homes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _body.Content = _settingsOpen ? SettingsPanel() : _schedulesOpen ? SchedulesPanel() : CurrentHome is { } h ? HomePanel(h) : EmptyPanel();
    }

    UIElement EmptyPanel()
    {
        var p = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };
        p.Children.Add(new TextBlock { Text = "No bulbs yet", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "Lanternly works only over Bluetooth and never contacts GE or any other server.\nTwo ways to start (open settings):\n• Add a reset bulb: bulbs already paired with the Cync app must be factory reset first. A reset loses the current color; set it again right after adding.\n• Import a Lanternly key file from another PC or phone.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), TextAlignment = TextAlignment.Center, Margin = new Thickness(10, 8, 10, 14) });
        var b = new Button { Content = "Open settings", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => { _settingsOpen = true; Render(); };
        p.Children.Add(b);
        return p;
    }

    static Border Card(UIElement child) => new() { Background = Br("Card"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = child };

    UIElement HomePanel(Home h)
    {
        var p = new StackPanel();
        var scenes = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var s in Scenes.All)
        {
            var sc = s; var btn = new Button { Content = s.Name, Margin = new Thickness(0, 0, 6, 6) };
            btn.Click += (_, _) => RunBatch(() => Scenes.ApplyAsync(_router, h, sc));
            scenes.Children.Add(btn);
        }
        p.Children.Add(scenes);
        var assigned = new HashSet<Bulb>();
        foreach (var r in h.Rooms)
        {
            var bulbs = h.Bulbs.Where(b => r.MeshIds.Contains(b.MeshId)).ToList();
            if (bulbs.Count == 0) continue;
            assigned.UnionWith(bulbs);
            p.Children.Add(Group(h, r.Name, bulbs, r));
        }
        var other = h.Bulbs.Where(b => !assigned.Contains(b)).ToList();
        if (other.Count > 0) p.Children.Add(Group(h, h.Rooms.Count > 0 ? "Other" : "All bulbs", other, null));
        UpdateRows();
        return p;
    }

    UIElement Group(Home h, string name, List<Bulb> bulbs, Room? room)
    {
        var st = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = bulbs.Any(b => b.State?.On == true), VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => { var on = sw.IsChecked == true; Run(async () => { foreach (var b in bulbs) await Send(h, b, new Cmd.Power(on)); }); };
        DockPanel.SetDock(sw, Dock.Right); head.Children.Add(sw);
        head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        st.Children.Add(head);
        if (bulbs.Any(b => b.Caps.HasFlag(Caps.Dim)))
        {
            var sl = Slider(1, 100, bulbs.Select(b => b.State?.Brightness ?? 50).DefaultIfEmpty(50).Max(), "Room brightness");
            Debounce(sl, v => Run(async () => { foreach (var b in bulbs.Where(b => b.Caps.HasFlag(Caps.Dim))) await Send(h, b, new Cmd.Brightness(v)); }));
            st.Children.Add(sl);
        }
        st.Children.Add(new Border { Height = 1, Background = Br("Line"), Margin = new Thickness(0, 6, 0, 2) });
        foreach (var b in bulbs) { var row = new BulbRow(this, h, b); _rows[b] = row; st.Children.Add(row.Root); }
        return Card(st);
    }

    static Slider Slider(int min, int max, int val, string tip) => new() { Minimum = min, Maximum = max, Value = val, ToolTip = tip, IsMoveToPointEnabled = true, Margin = new Thickness(0, 2, 0, 2) };

    /// <summary>Sends at most ~6 updates/s while dragging, and always the final value.</summary>
    static void Debounce(Slider s, Action<int> send)
    {
        DateTime last = default; var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) => { t.Stop(); send((int)s.Value); last = DateTime.UtcNow; };
        s.ValueChanged += (_, e) => { if (!s.IsLoaded || s.Tag is "sync") return; t.Stop(); if ((DateTime.UtcNow - last).TotalMilliseconds > 160) { send((int)s.Value); last = DateTime.UtcNow; } else t.Start(); };
    }

    void UpdateRows() { foreach (var r in _rows.Values) r.Update(); }

    sealed class BulbRow
    {
        public readonly StackPanel Root = new() { Margin = new Thickness(0, 4, 0, 2) };
        readonly MainWindow _w; readonly Button _find;
        readonly Bulb _b; readonly CheckBox _sw; readonly Slider? _bri, _ct; readonly TextBlock _via = new() { FontSize = 10, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Border _viaBox; readonly Border _dot = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        public BulbRow(MainWindow w, Home h, Bulb b)
        {
            _b = b;
            var line = new DockPanel();
            _sw = new CheckBox { Style = (Style)w.FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center };
            _sw.Click += (_, _) => { var on = _sw.IsChecked == true; w.Run(() => w.Send(h, b, new Cmd.Power(on))); };
            DockPanel.SetDock(_sw, Dock.Right); line.Children.Add(_sw);
            _viaBox = new Border { CornerRadius = new CornerRadius(4), Background = Br("Ctl"), Padding = new Thickness(0, 1, 0, 1), Child = _via, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How this bulb is reached" };
            DockPanel.SetDock(_viaBox, Dock.Right); line.Children.Add(_viaBox);
            var more = new Button { Content = "⋯", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Color", Visibility = b.Caps.HasFlag(Caps.Temp) || b.Caps.HasFlag(Caps.Color) ? Visibility.Visible : Visibility.Collapsed };
            DockPanel.SetDock(more, Dock.Right); line.Children.Add(more);
            _w = w;
            _find = new Button { Content = "◎", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Find this bulb (blinks)" };
            System.Windows.Automation.AutomationProperties.SetName(_find, "Find this bulb: " + b.Name);
            _find.Click += (_, _) => w.ToggleBlink(h, b);
            var find = _find;
            DockPanel.SetDock(find, Dock.Right); line.Children.Add(find);
            DockPanel.SetDock(_dot, Dock.Left); line.Children.Add(_dot);
            line.Children.Add(new TextBlock { Text = b.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            Root.Children.Add(line);
            if (b.Caps.HasFlag(Caps.Dim))
            {
                _bri = MainWindow.Slider(1, 100, b.State?.Brightness ?? 50, "Brightness");
                Debounce(_bri, v => w.Run(() => w.Send(h, b, new Cmd.Brightness(v))));
                Root.Children.Add(_bri);
            }
            var extra = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(18, 0, 0, 4) };
            if (b.Caps.HasFlag(Caps.Temp))
            {
                var g = new DockPanel();
                var warm = new TextBlock { Text = "Warm", FontSize = 10, Foreground = Br("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                var cool = new TextBlock { Text = "Cool", FontSize = 10, Foreground = Br("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(warm, Dock.Left); DockPanel.SetDock(cool, Dock.Right); g.Children.Add(warm); g.Children.Add(cool);
                _ct = MainWindow.Slider(0, 100, b.State is { Mode: <= 100 } s ? s.Mode : 50, "Color temperature");
                Debounce(_ct, v => w.Run(() => w.Send(h, b, new Cmd.Temp(v))));
                g.Children.Add(_ct); extra.Children.Add(g);
            }
            if (b.Caps.HasFlag(Caps.Color))
            {
                var sw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var hex in new[] { "#F7768E", "#FF9E64", "#E0AF68", "#9ECE6A", "#73DACA", "#7AA2F7", "#BB9AF7" })
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex);
                    var btn = new Button { Width = 26, Height = 22, Padding = new Thickness(0), Background = new SolidColorBrush(c), ToolTip = hex };
                    btn.Click += (_, _) => w.Run(() => w.Send(h, b, new Cmd.Rgb(c.R, c.G, c.B)));
                    sw.Children.Add(btn);
                }
                var pick = new Button { Content = "Custom…", Padding = new Thickness(6, 1, 6, 1) };
                pick.Click += (_, _) =>
                {
                    using var d = new System.Windows.Forms.ColorDialog { FullOpen = true };
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) w.Run(() => w.Send(h, b, new Cmd.Rgb(d.Color.R, d.Color.G, d.Color.B)));
                };
                sw.Children.Add(pick); extra.Children.Add(sw);
            }
            more.Click += (_, _) => extra.Visibility = extra.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Root.Children.Add(extra);
        }

        public void Update()
        {
            var s = _b.State;
            var blinking = _w._blinks.IsActive(_b);
            _find.Foreground = blinking ? Br("AccentFg") : Br("Fg");
            _find.Background = blinking ? Br("Accent") : Br("Ctl");
            _find.ToolTip = blinking ? "Stop blinking" : "Find this bulb (blinks)";
            System.Windows.Automation.AutomationProperties.SetName(_find, (blinking ? "Stop blinking: " : "Find this bulb: ") + _b.Name);
            _sw.IsChecked = s?.On == true;
            if (_bri != null && s != null) { _bri.Tag = "sync"; _bri.Value = Math.Max(1, s.Brightness); _bri.Tag = null; }
            if (_ct != null && s is { Mode: <= 100 }) { _ct.Tag = "sync"; _ct.Value = s.Mode; _ct.Tag = null; }
            _via.Text = _b.Via switch { Via.Ble => "BLE", _ => "OUT OF RANGE" };
            _via.Foreground = _b.Via switch { Via.Ble => B("#7AA2F7"), _ => Br("Dim") };
            _dot.Background = s is { On: true } ? (s.Mode == 0xfe ? B($"#{s.Rgb.R:X2}{s.Rgb.G:X2}{s.Rgb.B:X2}") : B("#E0AF68")) : Br("Ctl");
        }
        static Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    // ---------------- settings ----------------
    UIElement SettingsPanel()
    {
        var p = new StackPanel();
        TextBlock H(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 4) };
        TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 11, Margin = new Thickness(0, 2, 0, 6) };
        CheckBox Toggle(string t, bool v, Action<bool> set) { var c = new CheckBox { Content = t, IsChecked = v, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 4, 0, 2) }; c.Click += async (_, _) => { set(c.IsChecked == true); Save(); await Reconnect(); }; return c; }

        var conn = new StackPanel();
        conn.Children.Add(H("Connections"));
        conn.Children.Add(Toggle("Bluetooth (direct to nearby bulbs)", _set.Ble, v => _set.Ble = v));
        conn.Children.Add(Note("Offline only: Lanternly has no cloud or internet code and talks to bulbs over Bluetooth from this PC. Keep at least one bulb within Bluetooth range; the mesh relays to the rest."));
        p.Children.Add(Card(conn));

        var keys = new StackPanel();
        keys.Children.Add(H("Bulbs and mesh keys"));
        keys.Children.Add(Note(_vault.Homes.Count == 0 ? "No key file loaded." : $"{_vault.Homes.Count} home(s), {_vault.Homes.Sum(h => h.Bulbs.Count)} bulb(s) stored, encrypted for your Windows account."));
        var row = new WrapPanel();
        var imp = new Button { Content = "Import key file…", Margin = new Thickness(0, 0, 6, 6) };
        imp.Click += async (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Lanternly key file|*.json|All files|*.*" };
            if (d.ShowDialog(this) != true) return;
            try
            {
                var text = File.ReadAllText(d.FileName);
                string? pass = null;
                if (KeyFile.IsProtected(text)) { pass = Prompts.Passphrase(this, "This key file is protected. Enter its passphrase:", confirm: false); if (pass == null) return; }
                var v = KeyFile.Import(text, pass);
                if (v.Homes.Count == 0) throw new InvalidDataException("no homes in file");
                await StopAllBlinks(); // send pending stops over the old connection before it is replaced
                _vault.Homes = v.Homes; Store.Unblock(); _set.BackedUp = true;
                if (Save()) { _settingsOpen = false; await Reconnect(); }
            }
            catch (Exception ex) { Program.Log(ex, "import"); MessageBox.Show(this, "Could not import: " + ex.Message, "Lanternly"); }
        };
        var exp = new Button { Content = "Export key file…", Margin = new Thickness(0, 0, 6, 6), IsEnabled = _vault.Homes.Count > 0 };
        exp.Click += (_, _) => ExportKeys();
        row.Children.Add(imp); row.Children.Add(exp);
        keys.Children.Add(row);
        p.Children.Add(Card(keys));

        p.Children.Add(Card(AdoptPanel(H, Note)));
        p.Children.Add(Card(ResetGuide(H, Note)));

        var about = new StackPanel();
        about.Children.Add(H("About"));
        about.Children.Add(Note("Lanternly 0.1.3 · MIT license. Not affiliated with GE, Savant or Cync. No telemetry, no cloud, no internet connections. Keys are never logged."));
        var forget = new Button { Content = "Forget everything", HorizontalAlignment = HorizontalAlignment.Left };
        forget.Click += async (_, _) => { if (MessageBox.Show(this, "Delete stored keys and bulbs from this PC? Bulbs you added will only answer to a key-file backup or another factory reset.", "Lanternly", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; await StopAllBlinks(); await Teardown(); Store.Forget(); _vault = new(); _schedules = []; Replan(); _set.BackedUp = true; await Reconnect(); Render(); };
        about.Children.Add(forget);
        var done = new Button { Content = "Done", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        done.Click += (_, _) => { _settingsOpen = false; Render(); };
        about.Children.Add(done);
        p.Children.Add(Card(about));
        return p;
    }

    void ExportKeys()
    {
        if (_vault.Homes.Count == 0) return;
        var choice = MessageBox.Show(this, "The key file contains your mesh password. Anyone with it can control your bulbs over Bluetooth.\n\nProtect it with a passphrase? (Recommended.)\nYes = passphrase · No = plain file · Cancel = don't export", "Export key file", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        string? pass = null;
        if (choice == MessageBoxResult.Yes) { pass = Prompts.Passphrase(this, "Choose a passphrase (at least 8 characters). There is no way to recover it.", confirm: true); if (pass == null) return; }
        var d = new Microsoft.Win32.SaveFileDialog { FileName = "lanternly-keys.json", Filter = "Lanternly key file|*.json" };
        if (d.ShowDialog(this) != true) return;
        try { File.WriteAllText(d.FileName, KeyFile.Export(_vault, pass)); _set.BackedUp = true; Save(); UpdateStatus(); }
        catch (Exception ex) { Program.Log(ex, "export"); MessageBox.Show(this, "Could not export: " + ex.Message, "Lanternly"); }
    }

    UIElement AdoptPanel(Func<string, TextBlock> H, Func<string, TextBlock> Note)
    {
        var p = new StackPanel();
        p.Children.Add(H("Add a reset bulb (experimental)"));
        p.Children.Add(Note("This is how bulbs get into Lanternly. A bulb already paired with the Cync app must be factory reset first (see the guide below); the reset loses its current color, which you can set again right after. Lanternly then gives it a private Bluetooth mesh key generated on this PC. No account. Keep the bulb within a few meters of this PC."));
        var msg = Note("");
        var go = new Button { Content = "Find reset bulbs", HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += async (_, _) =>
        {
            if (_demo) { msg.Text = "Demo mode."; return; }
            go.IsEnabled = false;
            try
            {
                msg.Text = "Listening for reset bulbs (8 s)…";
                var macs = await WinGatt.FindResetBulbsAsync(TimeSpan.FromSeconds(8));
                if (macs.Count == 0) { msg.Text = "No reset bulb found. A bulb is ready when it was reset (it blinks) and was not added to another app since."; return; }
                var home = CurrentHome;
                if (home == null)
                {
                    var (n, pw) = Ble.NewMeshCredentials();
                    home = new Home { Id = (uint)Random.Shared.Next(1, int.MaxValue), Name = "My bulbs", MeshName = n, MeshPassword = pw };
                    _vault.Homes.Add(home); _set.HomeIndex = _vault.Homes.Count - 1;
                }
                await Teardown(); // free the radio
                int added = 0;
                foreach (var mac in macs)
                {
                    msg.Text = $"Adding {mac}…";
                    try { var b = await WinGatt.AdoptAsync(mac, home, $"Bulb {home.Bulbs.Count + 1}"); home.Bulbs.Add(b); added++; _set.BackedUp = false; Save(); }
                    catch (Exception ex) { Program.Log(ex, "adopt"); msg.Text = $"{mac}: {ex.Message}"; }
                }
                msg.Text = added > 0 ? $"Added {added} of {macs.Count} bulb(s)." : msg.Text;
                await Reconnect();
                if (added > 0 && MessageBox.Show(this, "These bulbs now only answer to the mesh key stored on this PC. If this PC's data is lost, the only way back is another factory reset.\n\nSave a key-file backup now?", "Back up your new mesh key", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    ExportKeys();
            }
            catch (Exception ex) { Program.Log(ex, "find reset bulbs"); msg.Text = ex.Message; }
            finally { go.IsEnabled = true; }
        };
        p.Children.Add(go); p.Children.Add(msg);
        return p;
    }

    UIElement ResetGuide(Func<string, TextBlock> H, Func<string, TextBlock> Note)
    {
        var p = new StackPanel();
        p.Children.Add(H("Bulb reset guide (optional)"));
        p.Children.Add(Note(ResetText.Intro));
        foreach (var (title, steps) in ResetText.Methods) { p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) }); p.Children.Add(Note(steps)); }
        return p;
    }

}

/// <summary>--demo: sample homes and an in-memory link, for screenshots and trying the UI without bulbs.</summary>
sealed class DemoLink : ILink
{
    public Via Kind => Via.Ble;
    public event Action<Home?, BulbState>? StateChanged;
    public bool CanReach(Home h, Bulb b) => true;
    public Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default) => Task.CompletedTask;
    public static Vault SampleVault() => new()
    {
        Homes = [new Home { Id = 1, Name = "Home", MeshName = "demo", MeshPassword = "demo",
            Rooms = [new Room { Id = 1, Name = "Living room", MeshIds = [1, 2] }, new Room { Id = 2, Name = "Bedroom", MeshIds = [3] }],
            Bulbs = [new Bulb { Name = "Floor lamp", MeshId = 1, Type = 6, State = new(1, true, true, 70, 30, (0, 0, 0)) },
                     new Bulb { Name = "Shelf strip", MeshId = 2, Type = 6, State = new(2, true, true, 40, 0xfe, (122, 162, 247)) },
                     new Bulb { Name = "Bedside", MeshId = 3, Type = 9, State = new(3, true, false, 20, 0, (0, 0, 0)) }] }],
    };
}

static class Prompts
{
    /// <summary>Add-schedule dialog: time, days, action (on/off/scene) and target (all, a room or a bulb). Null on cancel.</summary>
    public static Schedule? NewSchedule(Window owner, Home h)
    {
        var w = new Window { Owner = owner, Title = "Add schedule", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.Resources["Bg"] };
        var p = new StackPanel { Margin = new Thickness(16), Width = 340 };
        TextBlock L(string t) => new() { Text = t, Margin = new Thickness(0, 6, 0, 2) };
        p.Children.Add(L("Time (24-hour)"));
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        var hh = new ComboBox { ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToList(), SelectedIndex = 7, Width = 64 };
        var mm = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList(), SelectedIndex = 0, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(hh, "Hour"); System.Windows.Automation.AutomationProperties.SetName(mm, "Minute");
        time.Children.Add(hh); time.Children.Add(new TextBlock { Text = ":", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); time.Children.Add(mm);
        p.Children.Add(time);
        p.Children.Add(L("Days"));
        var days = new WrapPanel();
        var boxes = Schedules.DayNames.Select(n => new CheckBox { Content = n, IsChecked = true, Margin = new Thickness(0, 0, 8, 4) }).ToList();
        boxes.ForEach(b => days.Children.Add(b));
        p.Children.Add(days);
        p.Children.Add(L("Action"));
        var act = new ComboBox { ItemsSource = Schedules.Actions.Select(Schedules.ActionText).ToList(), SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(act, "Action");
        p.Children.Add(act);
        p.Children.Add(L("Bulbs"));
        var targets = new List<(string Id, string Name)> { ("all", "All bulbs") };
        targets.AddRange(h.Rooms.Select(r => ("room:" + r.Id, "Room: " + r.Name)));
        targets.AddRange(h.Bulbs.Select(b => ("bulb:" + b.MeshId, b.Name)));
        var tgt = new ComboBox { ItemsSource = targets.Select(t => t.Name).ToList(), SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(tgt, "Bulbs");
        p.Children.Add(tgt);
        p.Children.Add(new TextBlock { Text = "Scenes set color only on bulbs that support it. Runs while Lanternly is running and the PC is awake.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 8, 0, 0) });
        var err = new TextBlock { Foreground = Brushes.IndianRed }; p.Children.Add(err);
        var ok = new Button { Content = "Add", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Schedule? result = null;
        ok.Click += (_, _) =>
        {
            int mask = 0; for (int i = 0; i < 7; i++) if (boxes[i].IsChecked == true) mask |= 1 << i;
            if (mask == 0) { err.Text = "Choose at least one day."; return; }
            result = new Schedule { Hour = hh.SelectedIndex, Minute = mm.SelectedIndex, Days = mask, Action = Schedules.Actions[act.SelectedIndex], Target = targets[tgt.SelectedIndex].Id };
            w.DialogResult = true;
        };
        p.Children.Add(ok); w.Content = p;
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Small modal passphrase prompt. Returns null on cancel.</summary>
    public static string? Passphrase(Window owner, string text, bool confirm)
    {
        var w = new Window { Owner = owner, Title = "Lanternly", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.Resources["Bg"] };
        var p = new StackPanel { Margin = new Thickness(16), Width = 320 };
        p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var a = new PasswordBox { Margin = new Thickness(0, 0, 0, 6) }; p.Children.Add(a);
        var b = new PasswordBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = "Repeat" }; if (confirm) p.Children.Add(b);
        var err = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap }; p.Children.Add(err);
        var ok = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        string? result = null;
        ok.Click += (_, _) =>
        {
            if (confirm && a.Password.Length < 8) { err.Text = "Use at least 8 characters."; return; }
            if (confirm && a.Password != b.Password) { err.Text = "The passphrases don't match."; return; }
            if (a.Password.Length == 0) { err.Text = "Enter the passphrase."; return; }
            result = a.Password; w.DialogResult = true;
        };
        p.Children.Add(ok); w.Content = p; w.Loaded += (_, _) => a.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
