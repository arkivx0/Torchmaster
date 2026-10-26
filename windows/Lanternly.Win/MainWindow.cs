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
    readonly TextBlock _status = new() { Foreground = Br("Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
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
    /// <summary>The short schedule run in progress (cancelled by a home switch or close). Fades are in <see cref="_fades"/>.</summary>
    CancellationTokenSource? _runCts;
    /// <summary>Running app-side effect, and what the Now playing bar stops.</summary>
    CancellationTokenSource? _effectCts;
    (Home H, List<Bulb> Bulbs)? _nowTarget;
    readonly Border _nowBar = new() { Background = Br("Warn"), Padding = new Thickness(14, 4, 8, 4), Visibility = Visibility.Collapsed };
    readonly TextBlock _nowText = new() { Foreground = Br("OnWarn"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Swatch last applied per bulb from this app, for the ✓ (status reports carry only a coarse color).</summary>
    readonly Dictionary<Bulb, string> _activeSwatch = [];
    List<string> _vaultWarnings = [];

    Home? CurrentHome => _vault.Homes.Count == 0 ? null : _vault.Homes[Math.Clamp(_set.HomeIndex, 0, _vault.Homes.Count - 1)];

    public MainWindow(bool demo)
    {
        _demo = demo;
        // Icons (⚙ ⏰ ✓ ⚠ ■ ▶) need a symbol font; without the fallback they render as empty boxes on some systems.
        FontFamily = Theme.UiFont;
        _set = demo ? new Settings() : Store.LoadSettings();
        _vault = demo ? DemoLink.SampleVault() : Store.LoadVault();
        _vaultWarnings = BulbReset.AddressWarnings(_vault); // shown in settings; nothing is changed
        _schedules = demo ? [] : Store.LoadSchedules();
        Title = "Lanternly"; Width = 440; Height = 640; MinWidth = 360; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/lanternly.ico")); } catch (Exception ex) { Program.Log(ex, "icon"); }
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        var top = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        var gear = new Button { Content = "⚙", ToolTip = "Settings", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(gear, "Settings");
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
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetName(_homePick, "Home");
        _homePick.SelectionChanged += async (_, _) => { if (_homePick.SelectedIndex >= 0 && _homePick.SelectedIndex != _set.HomeIndex) { var idx = _homePick.SelectedIndex; await StopAllBlinks(); StopEffectsLocal(); _runCts?.Cancel(); CancelFades(null); await _scheduleGate.WaitAsync(); /* never swap the connection under a running schedule */ try { _set.HomeIndex = idx; Save(); await Reconnect(); } finally { _scheduleGate.Release(); } } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        // Now playing / Stop: shown while any effect runs, so Stop is always one click away.
        var nowRow = new DockPanel();
        var nowStop = new Button { Content = "■ Stop", Background = Br("OnWarn"), Foreground = Br("Warn"), Margin = new Thickness(6, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(nowStop, "Stop effect");
        nowStop.Click += (_, _) => { if (_nowTarget is { } t) StopEffects(t.H, t.Bulbs); else StopEffectsLocal(); };
        DockPanel.SetDock(nowStop, Dock.Right); nowRow.Children.Add(nowStop); nowRow.Children.Add(_nowText);
        _nowBar.Child = nowRow;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_nowText, System.Windows.Automation.AutomationLiveSetting.Polite);
        DockPanel.SetDock(_nowBar, Dock.Top); root.Children.Add(_nowBar);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 0, 14, 14) });
        Content = root;

        _tray.Open += ShowFromTray;
        _tray.Quit += () => { _quitting = true; Close(); };
        _tray.AllPower += on => RunBatch(async () => CurrentHome is { } h ? await _router.SendManyAsync(h, h.Bulbs, new Cmd.Power(on)) : new BatchResult(0, 0, null));
        _tray.Scene += s => RunBatch(async () => CurrentHome is { } h ? await Scenes.ApplyAsync(_router, h, s) : new BatchResult(0, 0, null));
        _tray.Effect += id => { if (CurrentHome is { } h) { if (id == 0) StopEffects(h, h.Bulbs); else StartNative(h, "All bulbs", h.Bulbs, Effects.Find(id)!); } };
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };
        Closed += async (_, _) => { _clock.Stop(); StopEffectsLocal(); _runCts?.Cancel(); CancelFades(null); Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged; Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; _tray.Dispose(); await StopAllBlinks(); await Teardown(); Application.Current.Shutdown(); };
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
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Render();
    }

    // ---------------- schedules ----------------
    void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { TimeZoneInfo.ClearCachedData(); Replan(); });

    /// <summary>After sleep the Bluetooth link is usually dead even if it still looks logged in: reconnect, then check schedules.</summary>
    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || _demo) return;
        Dispatcher.BeginInvoke(async () =>
        {
            await _scheduleGate.WaitAsync();
            try { await Reconnect(); } catch (Exception ex) { Program.Log(ex, "resume reconnect"); } finally { _scheduleGate.Release(); }
            CheckSchedules();
        });
    }

    long LastFired(long id) => _set.LastFired.TryGetValue(id, out var v) ? v : 0;

    /// <summary>Recomputes every next run from now (and after the last run that fired), so nothing fires late or twice.</summary>
    void Replan()
    {
        _due.Clear();
        var now = DateTimeOffset.Now;
        foreach (var s in _schedules.Where(s => s.Enabled)) if (Schedules.NextFireAfter(s, now, TimeZoneInfo.Local, LastFired(s.Id), _set.Sun) is { } t) _due[s.Id] = t;
    }

    /// <summary>Runs schedules that are due. A run more than <see cref="Schedules.Grace"/> late (PC asleep, clock changed) is skipped.</summary>
    void CheckSchedules()
    {
        var now = DateTimeOffset.Now;
        foreach (var s in _schedules.Where(s => s.Enabled).ToList())
        {
            if (!_due.TryGetValue(s.Id, out var due) || due > now) continue;
            if (Schedules.IsReplay(due, LastFired(s.Id))) Program.Log(null, "schedule replay skipped (clock set back)");
            else if (Schedules.OnTime(due, now))
            {
                _set.LastFired[s.Id] = due.ToUnixTimeMilliseconds(); Save();
                RunSchedule(s, due);
            }
            else { Program.Log(null, "schedule skipped (missed)"); Note(Label(s) + $": skipped (more than {Schedules.Grace.TotalMinutes:0} minutes late)"); }
            if (Schedules.NextFireAfter(s, now, TimeZoneInfo.Local, LastFired(s.Id), _set.Sun) is { } t) _due[s.Id] = t; else _due.Remove(s.Id);
        }
    }

    static string Label(Schedule s) => $"Schedule {Schedules.WhenText(s)} ({Schedules.ActionText(s.Action)})";

    /// <summary>Status line plus the persistent "Last run" line on the Schedules page.</summary>
    void Note(string msg)
    {
        SetStatus(msg);
        _set.LastRun = $"{DateTime.Now:g} · {msg}";
        Save();
        if (_schedulesOpen) Render();
    }

    async Task<BatchResult> TryRun(Router r, Home h, Schedule s, CancellationToken ct)
    {
        try { return await Schedules.RunAsync(r, h, s, ct); }
        catch (Exception ex) { Program.Log(ex, "schedule"); return new BatchResult(0, Schedules.Targets(h, s).Count, ex.Message); }
    }

    /// <summary>A schedule for a home that is not selected: a temporary connection to that home's mesh, closed afterwards.</summary>
    async Task<BatchResult> RunOtherHome(Home h, Schedule s, CancellationToken ct)
    {
        if (!_set.Ble) return new BatchResult(0, Schedules.Targets(h, s).Count, "Bluetooth is off in Lanternly settings");
        BleSession? t = null;
        try { t = await WinGatt.ConnectMeshAsync(h, _ => { }); t.StateChanged += OnState; return await TryRun(new Router([t]), h, s, ct); }
        catch (Exception ex) { Program.Log(ex, "schedule other home"); return new BatchResult(0, Schedules.Targets(h, s).Count, ex.Message); }
        finally { if (t != null) await t.DisposeAsync(); }
    }

    /// <summary>Fades in progress. They run outside <see cref="_scheduleGate"/>, so schedules due meanwhile still run on time.</summary>
    readonly List<(CancellationTokenSource Cts, HashSet<(long, int)> Keys)> _fades = [];

    static HashSet<(long, int)> Keys(Home h, IEnumerable<Bulb> bulbs) => bulbs.Where(b => b.AddressKnown).Select(b => ((long)h.Id, (int)b.MeshId)).ToHashSet();

    /// <summary>Stops fades on any of <paramref name="keys"/> (null = all). Their bulbs stay where they were.</summary>
    void CancelFades(HashSet<(long, int)>? keys)
    {
        foreach (var f in _fades.ToList()) if (keys == null || f.Keys.Overlaps(keys)) f.Cts.Cancel();
    }

    async void RunSchedule(Schedule s, DateTimeOffset due)
    {
        var label = Label(s);
        var h = _vault.Homes.FirstOrDefault(x => x.Id == s.HomeId);
        if (h == null) { Note(label + ": its home was removed"); return; }
        // A newer run takes over its own bulbs only: fades on other bulbs or another home keep going.
        var keys = Keys(h, Schedules.Targets(h, s));
        CancelFades(keys);
        if (h == CurrentHome) StopEffectsLocal(); // an app-side show would overwrite this run on its next step
        var fade = Schedules.FadeMins(s.Action) != null;
        var cts = new CancellationTokenSource();
        await _scheduleGate.WaitAsync();
        var held = true;
        try
        {
            // It may have waited behind another run (connecting takes a while): "skipped, not run late" still applies.
            if (!Schedules.OnTime(due, DateTimeOffset.Now)) { Note(label + $": skipped (more than {Schedules.Grace.TotalMinutes:0} minutes late)"); return; }
            var other = !_demo && h != CurrentHome;
            SetStatus(label + ": running…");
            BatchResult r;
            if (fade)
            {
                // Connect under the gate, then let it go: a fade lasts up to an hour.
                BleSession? t = null; Router router;
                if (other)
                {
                    if (!_set.Ble) { Note(label + ": Bluetooth is off in Lanternly settings"); return; }
                    try { t = await WinGatt.ConnectMeshAsync(h, _ => { }); t.StateChanged += OnState; router = new Router([t]); }
                    catch (Exception ex) { Program.Log(ex, "schedule other home"); Note($"{label}: not reachable ({ex.Message})"); return; }
                }
                else { if (!_demo && _ble?.LoggedIn != true) await Reconnect(); router = _router; }
                var rec = (cts, keys);
                _fades.Add(rec);
                _scheduleGate.Release(); held = false;
                try { r = await TryRun(router, h, s, cts.Token); }
                finally { _fades.Remove(rec); if (t != null) await t.DisposeAsync(); }
                Note($"{label}: " + (cts.IsCancellationRequested ? "fade stopped part-way (a later schedule on these bulbs, a home switch or closing took over)" : r.ToString())
                    + (other ? $" · {h.Name} (not the selected home, temporary connection)" : ""));
                return;
            }
            _runCts = cts;
            if (other) r = await RunOtherHome(h, s, cts.Token);
            else
            {
                if (!_demo && _ble?.LoggedIn != true) await Reconnect();
                r = await TryRun(_router, h, s, cts.Token);
            }
            var retried = false;
            if (!_demo && r.Ok == 0 && !cts.IsCancellationRequested && Schedules.OnTime(due, DateTimeOffset.Now)) // stale link (e.g. after sleep): reconnect and retry once
            {
                retried = true;
                if (other) r = await RunOtherHome(h, s, cts.Token);
                else { await Reconnect(); r = await TryRun(_router, h, s, cts.Token); }
            }
            Note($"{label}: {r}" + (other ? $" · {h.Name} (not the selected home, temporary connection)" : "") + (retried ? " · after one retry" : ""));
        }
        catch (Exception ex) { Program.Log(ex, "schedule"); Note($"{label}: {ex.Message}"); }
        finally { if (_runCts == cts) _runCts = null; cts.Dispose(); if (held) _scheduleGate.Release(); UpdateRows(); }
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
        c.Children.Add(new TextBlock { Text = $"Schedules run on this PC only while Lanternly is running (closing the window keeps it in the tray), the PC is awake, Bluetooth is on and a bulb is in range. A run missed by more than {Schedules.Grace.TotalMinutes:0} minutes is skipped, not run late. If no bulb answers, Lanternly reconnects and retries once. Schedules for a home that is not selected still run, over a temporary connection to that home.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 12, Margin = new Thickness(0, 2, 0, 8) });
        if (!_demo && Store.SchedulesQuarantined is { } q)
            c.Children.Add(new TextBlock { Text = $"⚠ The saved schedules could not be read, so they were moved aside ({q}) and the list starts empty.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Yellow"), Margin = new Thickness(0, 0, 0, 6) });
        if (!_demo && Store.SchedulesSkipped > 0)
            c.Children.Add(new TextBlock { Text = $"⚠ {Store.SchedulesSkipped} saved schedule(s) were invalid and are ignored.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Yellow"), Margin = new Thickness(0, 0, 0, 6) });
        if (!_demo && _set.LastRun is { } lr)
            c.Children.Add(new TextBlock { Text = "Last run: " + lr, TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
        if (_schedules.Any(x => x.Trigger != "time") && _set.Sun == null)
            c.Children.Add(new TextBlock { Text = "⚠ Sunrise/sunset schedules don't run until you enter a location in Settings.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Yellow"), Margin = new Thickness(0, 0, 0, 6) });
        if (_schedules.Count == 0) c.Children.Add(new TextBlock { Text = "No schedules yet.", Foreground = Br("Dim"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var s in _schedules)
        {
            var sc = s; var home = _vault.Homes.FirstOrDefault(x => x.Id == s.HomeId);
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var del = new Button { Content = "✕ Delete", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(del, "Delete schedule " + Schedules.Describe(home, s));
            del.Click += (_, _) => { if (MessageBox.Show(this, "Delete this schedule?\n" + Schedules.Describe(home, sc), "Lanternly", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; _schedules.Remove(sc); _set.LastFired.Remove(sc.Id); Save(); SaveSchedules(); Render(); };
            DockPanel.SetDock(del, Dock.Right); line.Children.Add(del);
            var on = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Enabled" };
            System.Windows.Automation.AutomationProperties.SetName(on, "Enabled: " + Schedules.Describe(home, s));
            on.Click += (_, _) => { sc.Enabled = on.IsChecked == true; SaveSchedules(); };
            DockPanel.SetDock(on, Dock.Right); line.Children.Add(on);
            line.Children.Add(new TextBlock { Text = Schedules.Describe(home, s) + (_vault.Homes.Count > 1 && home != null ? (home == CurrentHome ? $" ({home.Name})" : $" ({home.Name}, not selected: runs over a temporary connection)") : ""), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            c.Children.Add(line);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = new Button { Content = "Add schedule…", Margin = new Thickness(0, 0, 6, 0), IsEnabled = CurrentHome != null };
        add.Click += (_, _) =>
        {
            if (CurrentHome is not { } h) return;
            if (Prompts.NewSchedule(this, h, _set.Sun != null) is { } ns) { ns.Id = DateTime.UtcNow.Ticks; ns.HomeId = h.Id; _schedules.Add(ns); SaveSchedules(); Render(); }
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

    // ---------------- reset bulb (factory reset over BLE) ----------------
    void ResetBulb(Home h, Bulb b)
    {
        if (BulbReset.RefuseReason(h, b) is { } why) { MessageBox.Show(this, why, "Can't reset this bulb", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, BulbReset.ConfirmText(b), BulbReset.ConfirmTitle, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (MessageBox.Show(this, $"Last check: erase the pairing of \"{b.Name}\" now? This cannot be undone.", BulbReset.ConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Run(async () =>
        {
            if (_demo) { SetStatus("Demo mode: nothing was reset."); return; }
            CancelFades(Keys(h, [b])); // a fade on this bulb must not keep sending during the reset
            await _scheduleGate.WaitAsync(); // never in the middle of a schedule run
            try
            {
                SetStatus($"Resetting {b.Name}…");
                if (_ble?.LoggedIn != true) await Reconnect();
                _blinks.Stop(b); _blinkHome.Remove(b);
                try { await BulbReset.SendAsync(_router, h, b); }
                catch (Exception ex) { throw new IOException($"{b.Name}: reset not sent ({ex.Message}). Nothing was removed.", ex); }
                bool seen = false;
                if (BulbReset.CanConfirm(b)) // no Bluetooth address on file: nothing to recognise, go straight to the question
                {
                    SetStatus($"Reset sent. Checking that {b.Name} restarted ({BulbReset.ConfirmScan.TotalSeconds:0} s)…");
                    try { seen = BulbReset.Confirmed(b, await WinGatt.FindResetBulbsAsync(BulbReset.ConfirmScan)); }
                    catch (Exception ex) { Program.Log(ex, "reset confirm scan"); }
                }
                if (!seen && MessageBox.Show(this, BulbReset.UnconfirmedText(b), BulbReset.UnconfirmedTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) != MessageBoxResult.No)
                { SetStatus($"{b.Name} was kept. Nothing was removed. Reconnecting…"); await Reconnect(); Render(); return; } // its link may have dropped
                var own = BulbReset.SchedulesFor(h, b, _schedules);
                foreach (var s in Prompts.PickSchedules(this, $"Delete {b.Name}'s schedules?", own.Select(x => Schedules.Describe(h, x)).ToList()) is { } keep ? own.Where((_, i) => keep[i]) : [])
                { _schedules.Remove(s); _set.LastFired.Remove(s.Id); _due.Remove(s.Id); }
                BulbReset.Forget(h, b);
                Save(); SaveSchedules(); Replan();
                await Reconnect(); // the reset bulb may have been our connection
                if (!seen) { SetStatus($"{b.Name} was removed."); Render(); return; }
                SetStatus(BulbReset.Done);
                MessageBox.Show(this, BulbReset.Done, "Bulb reset", MessageBoxButton.OK, MessageBoxImage.Information);
                _settingsOpen = true; _schedulesOpen = false; Render();
            }
            finally { _scheduleGate.Release(); }
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
        if (_demo) { _demoLink ??= new DemoLink(); _demoLink.StateChanged += OnState; _router = new([_demoLink]); foreach (var b in h?.Bulbs ?? []) b.Via = Via.Ble; Render(); SetStatus("Demo · BLE (sample data)"); return; }
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
        // key warnings are in the banner (KeyBanner), not this transient line
        SetStatus(s);
        if (CurrentHome is { } h) foreach (var b in h.Bulbs) b.Via = _router.Pick(h, b)?.Kind ?? Via.None;
        UpdateRows();
    }
    void SetStatus(string s)
    {
        _status.Text = s; _tray.Status("Lanternly · " + s);
        try { System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged); } catch (Exception) { }
    }

    void OnState(Home? h, BulbState s) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var b in (h ?? CurrentHome)?.Bulbs.Where(b => b.MeshId == s.MeshId) ?? []) b.State = s;
        UpdateRows();
    });

    async Task Send(Home h, Bulb b, Cmd c)
    {
        await _router.SendAsync(h, b, c);
        if (c is Cmd.Identify or Cmd.FactoryReset or Cmd.Effect) return; // no on/brightness/color change
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

    /// <summary>High-contrast banner for the key warnings, with the fix one click away.</summary>
    UIElement? KeyBanner()
    {
        string? msg = Store.Quarantined != null && !_demo ? "Stored keys unreadable, saving is blocked. Import a key file (Settings) to fix it."
            : !_set.BackedUp && !_demo && _vault.Homes.Count > 0 ? "Your bulbs only answer to the mesh key on this PC. If it's lost, every bulb must be factory reset. Export a key file now." : null;
        if (msg == null) return null;
        var st = new StackPanel();
        var head = new TextBlock { Text = Store.Quarantined != null ? "⚠ Keys unreadable" : "⚠ Backup needed", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Br("OnWarn") };
        System.Windows.Automation.AutomationProperties.SetHeadingLevel(head, System.Windows.Automation.AutomationHeadingLevel.Level2);
        st.Children.Add(head);
        st.Children.Add(new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, Foreground = Br("OnWarn"), FontSize = 13, Margin = new Thickness(0, 2, 0, 6) });
        var go = new Button { Content = Store.Quarantined != null ? "Import key file…" : "Export now", Background = Br("OnWarn"), Foreground = Br("Warn"), HorizontalAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.SemiBold };
        go.Click += (_, _) => { if (Store.Quarantined != null) { _settingsOpen = true; _schedulesOpen = false; Render(); } else ExportKeys(); };
        st.Children.Add(go);
        return new Border { Background = Br("Warn"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = st };
    }

    // ---------------- light shows ----------------
    /// <summary>Effects menu for all bulbs, a room or one bulb: Stop first, then "Runs in the bulb" and "Needs Lanternly open".</summary>
    ContextMenu EffectsMenu(Home h, string title, List<Bulb> bulbs)
    {
        var ok = bulbs.Where(b => b.AddressKnown).ToList(); var color = ok.Where(Effects.Supports).ToList();
        var m = new ContextMenu();
        var stop = new MenuItem { Header = "■ Stop effect", FontWeight = FontWeights.Bold }; stop.Click += (_, _) => StopEffects(h, ok); m.Items.Add(stop);
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = color.Count < ok.Count ? $"Runs in the bulb ({color.Count} of {ok.Count} bulbs)" : "Runs in the bulb", IsEnabled = false });
        if (color.Count == 0) m.Items.Add(new MenuItem { Header = "Needs a full-color bulb", IsEnabled = false });
        else foreach (var e in Effects.All) { var x = e; var mi = new MenuItem { Header = "✨ " + e.Name }; mi.Click += (_, _) => StartNative(h, title, color, x); m.Items.Add(mi); }
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "Needs Lanternly open", IsEnabled = false });
        foreach (var n in AppEffects.All) { var x = n; var mi = new MenuItem { Header = "↻ " + n }; mi.Click += (_, _) => StartApp(h, title, ok, x); m.Items.Add(mi); }
        return m;
    }
    Button EffectsButton(Home h, string title, List<Bulb> bulbs, string text)
    {
        var b = new Button { Content = text, ToolTip = "Light shows" };
        System.Windows.Automation.AutomationProperties.SetName(b, "Effects: " + title);
        b.Click += (_, _) => { var m = EffectsMenu(h, title, bulbs); m.PlacementTarget = b; m.IsOpen = true; };
        return b;
    }
    void ShowNow(string label, Home h, List<Bulb> bulbs) { _nowTarget = (h, bulbs); _nowText.Text = "Now playing: " + label; _nowBar.Visibility = Visibility.Visible; }
    void HideNow() { _nowTarget = null; _nowBar.Visibility = Visibility.Collapsed; }
    void StopEffectsLocal() { _effectCts?.Cancel(); _effectCts = null; HideNow(); }
    void StopEffects(Home h, List<Bulb> bulbs)
    {
        StopEffectsLocal();
        RunBatch(() => _router.SendManyAsync(h, bulbs.Where(b => b.AddressKnown && Effects.Supports(b)), new Cmd.Effect(0)));
    }
    void StartNative(Home h, string title, List<Bulb> bulbs, Effects.Native e)
    {
        StopEffectsLocal();
        Run(async () =>
        {
            var r = await _router.SendManyAsync(h, bulbs.Where(b => b.AddressKnown && Effects.Supports(b)), new Cmd.Effect(e.Id));
            if (r.Ok > 0) ShowNow($"✨ {e.Name} · {title} (in the bulb)", h, bulbs);
            SetStatus(r.AllOk ? $"{e.Name} on {title}" : r.ToString());
        });
    }
    /// <summary>App-side effect: steps go out one after another (rate limited in AppEffects) until Stop, home switch or quit.</summary>
    void StartApp(Home h, string title, List<Bulb> bulbs, string name)
    {
        StopEffectsLocal();
        var targets = bulbs.Where(b => AppEffects.Suits(name, b)).ToList();
        if (targets.Count == 0) { SetStatus($"{name}: no suitable bulb here"); return; }
        var cts = _effectCts = new CancellationTokenSource();
        ShowNow($"↻ {name} · {title} (keep Lanternly open)", h, targets);
        Run(async () =>
        {
            await _router.SendManyAsync(h, targets.Where(Effects.Supports), new Cmd.Effect(0)); // end a built-in show first
            await AppEffects.RunAsync(_router, h, targets, name, cts.Token);
        });
    }

    UIElement HomePanel(Home h)
    {
        var p = new StackPanel();
        if (KeyBanner() is { } kb) p.Children.Add(kb);
        var scenes = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var s in Scenes.All)
        {
            var sc = s; var btn = new Button { Content = s.Name, Margin = new Thickness(0, 0, 6, 6) };
            btn.Click += (_, _) => RunBatch(() => Scenes.ApplyAsync(_router, h, sc));
            scenes.Children.Add(btn);
        }
        scenes.Children.Add(EffectsButton(h, "All bulbs", h.Bulbs, "✨ Effects ▾"));
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
        var known = bulbs.Where(b => b.AddressKnown).ToList(); // address 0 would reach whichever bulb is connected
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = bulbs.Any(b => b.State?.On == true), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(sw, "Power: " + name);
        sw.Click += (_, _) => { var on = sw.IsChecked == true; Run(async () => { foreach (var b in known) await Send(h, b, new Cmd.Power(on)); }); };
        DockPanel.SetDock(sw, Dock.Right); head.Children.Add(sw);
        var fx = EffectsButton(h, name, bulbs, "✨"); DockPanel.SetDock(fx, Dock.Right); head.Children.Add(fx);
        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetHeadingLevel(title, System.Windows.Automation.AutomationHeadingLevel.Level2);
        head.Children.Add(title);
        st.Children.Add(head);
        if (bulbs.Any(b => b.Caps.HasFlag(Caps.Dim)))
        {
            var sl = Slider(1, 100, bulbs.Select(b => b.State?.Brightness ?? 50).DefaultIfEmpty(50).Max(), "Brightness: " + name);
            Debounce(sl, v => Run(async () => { foreach (var b in known.Where(b => b.Caps.HasFlag(Caps.Dim))) await Send(h, b, new Cmd.Brightness(v)); }));
            st.Children.Add(sl);
        }
        st.Children.Add(new Border { Height = 1, Background = Br("Line"), Margin = new Thickness(0, 6, 0, 2) });
        foreach (var b in bulbs) { var row = new BulbRow(this, h, b); _rows[b] = row; st.Children.Add(row.Root); }
        return Card(st);
    }

    static Slider Slider(int min, int max, int val, string tip)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = val, ToolTip = tip, IsMoveToPointEnabled = true, Margin = new Thickness(0, 2, 0, 2), MinHeight = 24 };
        System.Windows.Automation.AutomationProperties.SetName(s, tip);
        return s;
    }

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
            System.Windows.Automation.AutomationProperties.SetName(_sw, "Power: " + b.Name);
            _sw.Click += (_, _) => { var on = _sw.IsChecked == true; w.Run(() => w.Send(h, b, new Cmd.Power(on))); };
            DockPanel.SetDock(_sw, Dock.Right); line.Children.Add(_sw);
            _viaBox = new Border { CornerRadius = new CornerRadius(4), Background = Br("Ctl"), Padding = new Thickness(0, 1, 0, 1), Child = _via, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How this bulb is reached" };
            DockPanel.SetDock(_viaBox, Dock.Right); line.Children.Add(_viaBox);
            var more = new Button { Content = "⋯", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0), ToolTip = "More: effects, color, reset bulb" };
            System.Windows.Automation.AutomationProperties.SetName(more, "More options: " + b.Name);
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
            if (!b.AddressKnown)
            {
                _sw.IsEnabled = false; _find.IsEnabled = false; more.Visibility = Visibility.Collapsed;
                _unknown = true;
                Root.Children.Add(new TextBlock { Text = b.MeshId == 0 ? "Address unknown: this bulb's own controls are off (they would reach whichever bulb is connected). Re-add it (reset, then Find reset bulbs) or import a key file that has its address." : $"Invalid mesh address {b.MeshId}: this bulb's own controls are off.", TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 12, Margin = new Thickness(18, 0, 0, 4) });
                return;
            }
            if (b.Caps.HasFlag(Caps.Dim))
            {
                _bri = MainWindow.Slider(1, 100, b.State?.Brightness ?? 50, "Brightness: " + b.Name);
                Debounce(_bri, v => w.Run(() => w.Send(h, b, new Cmd.Brightness(v))));
                Root.Children.Add(_bri);
            }
            var extra = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(18, 0, 0, 4) };
            var fx = w.EffectsButton(h, b.Name, [b], "✨ Effects ▾"); fx.HorizontalAlignment = HorizontalAlignment.Left; fx.Margin = new Thickness(0, 4, 0, 4);
            extra.Children.Add(fx);
            if (b.Caps.HasFlag(Caps.Temp))
            {
                var g = new DockPanel();
                var warm = new TextBlock { Text = "Warm", FontSize = 12, Foreground = Br("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                var cool = new TextBlock { Text = "Cool", FontSize = 12, Foreground = Br("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(warm, Dock.Left); DockPanel.SetDock(cool, Dock.Right); g.Children.Add(warm); g.Children.Add(cool);
                _ct = MainWindow.Slider(0, 100, b.State is { Mode: <= 100 } s ? s.Mode : 50, "Color temperature: " + b.Name);
                Debounce(_ct, v => { w._activeSwatch.Remove(b); w.Run(() => w.Send(h, b, new Cmd.Temp(v))); });
                g.Children.Add(_ct); extra.Children.Add(g);
            }
            if (b.Caps.HasFlag(Caps.Color))
            {
                var sw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var (name, hex) in Tokens.Swatches) // 44 px targets, named for Narrator, ✓ on the color last set here
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex); var nm = name;
                    var btn = new Button { Width = Tokens.MinTouchPx, Height = Tokens.MinTouchPx, Padding = new Thickness(0), Background = new SolidColorBrush(c), ToolTip = name,
                        Foreground = Br("AccentFg"), FontWeight = FontWeights.Bold, FontSize = 16 };
                    System.Windows.Automation.AutomationProperties.SetName(btn, $"Set {b.Name} to {name}");
                    btn.Click += (_, _) => { w._activeSwatch[b] = nm; w.Run(() => w.Send(h, b, new Cmd.Rgb(c.R, c.G, c.B))); };
                    _swatches.Add((nm, btn));
                    sw.Children.Add(btn);
                }
                var pick = new Button { Content = "Custom…", Padding = new Thickness(6, 1, 6, 1) };
                pick.Click += (_, _) =>
                {
                    w._activeSwatch.Remove(b);
                    using var d = new System.Windows.Forms.ColorDialog { FullOpen = true };
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) w.Run(() => w.Send(h, b, new Cmd.Rgb(d.Color.R, d.Color.G, d.Color.B)));
                };
                sw.Children.Add(pick); extra.Children.Add(sw);
            }
            extra.Children.Add(new Border { Height = 1, Background = Br("Line"), Margin = new Thickness(0, 8, 0, 2) });
            var reset = new Button { Content = "⚠ Reset bulb…", Foreground = Br("Warn"), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Factory reset over Bluetooth (erases the pairing)" };
            System.Windows.Automation.AutomationProperties.SetName(reset, "Reset bulb: " + b.Name);
            reset.Click += (_, _) => w.ResetBulb(h, b);
            extra.Children.Add(reset);
            more.Click += (_, _) => extra.Visibility = extra.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Root.Children.Add(extra);
        }

        readonly List<(string Name, Button Btn)> _swatches = [];
        bool _unknown;
        public void Update()
        {
            var s = _b.State;
            if (_unknown) { _sw.IsChecked = s?.On == true; _via.Text = "ADDRESS UNKNOWN"; _via.Foreground = Br("Yellow"); return; }
            var act = _w._activeSwatch.TryGetValue(_b, out var a) ? a : null;
            foreach (var (n, btn) in _swatches) { btn.Content = n == act ? "✓" : ""; System.Windows.Automation.AutomationProperties.SetItemStatus(btn, n == act ? "Selected" : ""); }
            var blinking = _w._blinks.IsActive(_b);
            _find.Foreground = blinking ? Br("AccentFg") : Br("Fg");
            _find.Background = blinking ? Br("Accent") : Br("Ctl");
            _find.ToolTip = blinking ? "Stop blinking" : "Find this bulb (blinks)";
            System.Windows.Automation.AutomationProperties.SetName(_find, (blinking ? "Stop blinking: " : "Find this bulb: ") + _b.Name);
            _sw.IsChecked = s?.On == true;
            if (_bri != null && s != null) { _bri.Tag = "sync"; _bri.Value = Math.Max(1, s.Brightness); _bri.Tag = null; }
            if (_ct != null && s is { Mode: <= 100 }) { _ct.Tag = "sync"; _ct.Value = s.Mode; _ct.Tag = null; }
            _via.Text = _b.Via switch { Via.Ble => "BLE", _ => "OUT OF RANGE" }; _via.FontSize = 11; _via.FontWeight = FontWeights.SemiBold;
            _via.Foreground = _b.Via switch { Via.Ble => Br("Ok"), _ => Br("Dim") };
            _dot.Background = s is { On: true } ? (s.Mode == 0xfe ? B($"#{s.Rgb.R:X2}{s.Rgb.G:X2}{s.Rgb.B:X2}") : B("#E0AF68")) : Br("Ctl");
        }
        static Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    // ---------------- settings ----------------
    UIElement SettingsPanel()
    {
        var p = new StackPanel();
        TextBlock H(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 4) };
        TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Br("Dim"), FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };
        if (KeyBanner() is { } kb) p.Children.Add(kb);
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
                _vaultWarnings = BulbReset.AddressWarnings(_vault);
                if (Save()) { _settingsOpen = false; await Reconnect(); }
                if (_vaultWarnings.Count > 0) MessageBox.Show(this, string.Join("\n\n", _vaultWarnings), "Imported, with warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { Program.Log(ex, "import"); MessageBox.Show(this, "Could not import: " + ex.Message, "Lanternly"); }
        };
        var exp = new Button { Content = "Export key file…", Margin = new Thickness(0, 0, 6, 6), IsEnabled = _vault.Homes.Count > 0 };
        exp.Click += (_, _) => ExportKeys();
        row.Children.Add(imp); row.Children.Add(exp);
        keys.Children.Add(row);
        foreach (var wmsg in _vaultWarnings) keys.Children.Add(new TextBlock { Text = "⚠ " + wmsg, TextWrapping = TextWrapping.Wrap, Foreground = Br("Yellow"), Margin = new Thickness(0, 2, 0, 4) });
        p.Children.Add(Card(keys));

        var sun = new StackPanel();
        sun.Children.Add(H("Sunrise / sunset location (optional)"));
        sun.Children.Add(Note("For sunrise and sunset schedules only. Type latitude and longitude in decimal degrees (north and east positive), e.g. from a paper map; 1 decimal place (about 10 km) is enough. Sun times are computed on this PC; no location service or internet is used."));
        var la = new TextBox { Text = _set.SunLat?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", Margin = new Thickness(0, 0, 0, 4) };
        var lo = new TextBox { Text = _set.SunLon?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", Margin = new Thickness(0, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(la, "Latitude"); System.Windows.Automation.AutomationProperties.SetName(lo, "Longitude");
        sun.Children.Add(new TextBlock { Text = "Latitude (e.g. 40.7)" }); sun.Children.Add(la);
        sun.Children.Add(new TextBlock { Text = "Longitude (e.g. -74.0)" }); sun.Children.Add(lo);
        var sunMsg = Note("");
        var sunRow = new WrapPanel();
        var saveLoc = new Button { Content = "Save location" };
        saveLoc.Click += (_, _) =>
        {
            if (SunLocation.Parse(la.Text, lo.Text) is not { } v) { sunMsg.Text = "Latitude must be -90..90 and longitude -180..180."; return; }
            var rl = v.Rounded(); _set.SunLat = rl.Lat; _set.SunLon = rl.Lon; Save(); Replan(); // stored to 1 decimal (~10 km): plenty for sun times
            var today = DateOnly.FromDateTime(DateTime.Now);
            string F(DateTimeOffset? t) => t is { } x ? x.ToString("HH:mm") : "none";
            sunMsg.Text = $"Saved. Today: sunrise {F(Sun.Event(today, rl, true, TimeZoneInfo.Local))}, sunset {F(Sun.Event(today, rl, false, TimeZoneInfo.Local))}.";
        };
        var clearLoc = new Button { Content = "Clear", IsEnabled = _set.Sun != null };
        clearLoc.Click += (_, _) => { _set.SunLat = null; _set.SunLon = null; Save(); Replan(); Render(); };
        sunRow.Children.Add(saveLoc); sunRow.Children.Add(clearLoc);
        sun.Children.Add(sunRow); sun.Children.Add(sunMsg);
        p.Children.Add(Card(sun));

        p.Children.Add(Card(AdoptPanel(H, Note)));
        p.Children.Add(Card(ResetGuide(H, Note)));

        var about = new StackPanel();
        about.Children.Add(H("About"));
        about.Children.Add(Note("Lanternly 0.1.7 · MIT license. Not affiliated with GE, Savant or Cync. No telemetry, no cloud, no internet connections. Keys are never logged."));
        var forget = new Button { Content = "⚠ Forget everything…", Foreground = Br("Warn"), HorizontalAlignment = HorizontalAlignment.Left };
        forget.Click += async (_, _) => { if (MessageBox.Show(this, "Delete stored keys and bulbs from this PC? Bulbs you added will only answer to a key-file backup or another factory reset.", "Lanternly", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; await StopAllBlinks(); await Teardown(); Store.Forget(); _vault = new(); _schedules = []; _set.LastFired.Clear(); _set.LastRun = null; Replan(); _set.BackedUp = true; await Reconnect(); Render(); };
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
        try { File.WriteAllText(d.FileName, KeyFile.Export(_vault, pass)); _set.BackedUp = true; Save(); UpdateStatus(); Render(); }
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
    /// <summary>Pick which items to apply (all ticked). Null = keep all / cancel.</summary>
    public static bool[]? PickSchedules(Window owner, string title, List<string> items)
    {
        if (items.Count == 0) return null;
        var w = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.Resources["Bg"] };
        var p = new StackPanel { Margin = new Thickness(16), Width = 380 };
        p.Children.Add(new TextBlock { Text = "These schedules controlled only this bulb. Ticked ones are deleted; kept ones show \"(removed bulb)\".", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var boxes = items.Select(t => new CheckBox { Content = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Width = 340 }, IsChecked = true, Margin = new Thickness(0, 0, 0, 6) }).ToList();
        boxes.ForEach(b => p.Children.Add(b));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var keep = new Button { Content = "Keep all", IsCancel = true }; var del = new Button { Content = "Delete ticked", IsDefault = true };
        bool[]? result = null;
        del.Click += (_, _) => { result = boxes.Select(b => b.IsChecked == true).ToArray(); w.DialogResult = true; };
        row.Children.Add(keep); row.Children.Add(del); p.Children.Add(row); w.Content = p;
        return w.ShowDialog() == true ? result : null;
    }

    public static Schedule? NewSchedule(Window owner, Home h, bool hasLocation)
    {
        var w = new Window { Owner = owner, Title = "Add schedule", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.Resources["Bg"] };
        var p = new StackPanel { Margin = new Thickness(16), Width = 340 };
        TextBlock L(string t) => new() { Text = t, Margin = new Thickness(0, 6, 0, 2) };
        p.Children.Add(L("When"));
        var trig = new ComboBox { ItemsSource = new[] { "At a time", "Sunrise", "Sunset" }, SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(trig, "When");
        p.Children.Add(trig);
        var timeLabel = L("Time (24-hour)"); p.Children.Add(timeLabel);
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        var hh = new ComboBox { ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToList(), SelectedIndex = 7, Width = 64 };
        var mm = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList(), SelectedIndex = 0, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(hh, "Hour"); System.Windows.Automation.AutomationProperties.SetName(mm, "Minute");
        time.Children.Add(hh); time.Children.Add(new TextBlock { Text = ":", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); time.Children.Add(mm);
        p.Children.Add(time);
        var offLabel = L("Offset in minutes (−180 to 180; −30 = 30 min before)");
        var off = new TextBox { Text = "0" }; System.Windows.Automation.AutomationProperties.SetName(off, "Offset in minutes");
        var sunNote = new TextBlock { Text = hasLocation ? "Uses the location in Settings." : "No location set: enter latitude/longitude in Settings or this won't run.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.Resources["Dim"] };
        p.Children.Add(offLabel); p.Children.Add(off); p.Children.Add(sunNote);
        void Sync() { var sun = trig.SelectedIndex > 0; timeLabel.Visibility = time.Visibility = sun ? Visibility.Collapsed : Visibility.Visible; offLabel.Visibility = off.Visibility = sunNote.Visibility = sun ? Visibility.Visible : Visibility.Collapsed; }
        trig.SelectionChanged += (_, _) => Sync(); Sync();
        p.Children.Add(L("Days"));
        var days = new WrapPanel();
        var boxes = Schedules.DayNames.Select(n => new CheckBox { Content = n, IsChecked = true, Margin = new Thickness(0, 0, 8, 4) }).ToList();
        boxes.ForEach(b => days.Children.Add(b));
        p.Children.Add(days);
        p.Children.Add(L("Action"));
        var act = new ComboBox { ItemsSource = Schedules.AllActions.Select(Schedules.ActionText).ToList(), SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(act, "Action");
        p.Children.Add(act);
        p.Children.Add(L("Bulbs"));
        var targets = new List<(string Id, string Name)> { ("all", "All bulbs") };
        targets.AddRange(h.Rooms.Select(r => ("room:" + r.Id, "Room: " + r.Name)));
        targets.AddRange(h.Bulbs.Where(b => b.AddressKnown).Select(b => ("bulb:" + b.MeshId, b.Name)));
        var tgt = new ComboBox { ItemsSource = targets.Select(t => t.Name).ToList(), SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(tgt, "Bulbs");
        p.Children.Add(tgt);
        p.Children.Add(new TextBlock { Text = "Scenes set color and effects run only on bulbs that support them. Runs while Lanternly is running and the PC is awake. A fade starts at the set time and keeps the connection for its whole length.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 8, 0, 0) });
        var err = new TextBlock { Foreground = Brushes.IndianRed }; p.Children.Add(err);
        var ok = new Button { Content = "Add", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Schedule? result = null;
        ok.Click += (_, _) =>
        {
            int mask = 0; for (int i = 0; i < 7; i++) if (boxes[i].IsChecked == true) mask |= 1 << i;
            if (mask == 0) { err.Text = "Choose at least one day."; return; }
            var trigger = Schedules.Triggers[trig.SelectedIndex]; int offset = 0;
            if (trigger != "time" && (!int.TryParse(off.Text.Trim().Replace('−', '-'), out offset) || offset is < -Schedules.MaxOffset or > Schedules.MaxOffset)) { err.Text = "Offset must be −180 to 180 minutes."; return; }
            result = new Schedule { Hour = trigger == "time" ? hh.SelectedIndex : 0, Minute = trigger == "time" ? mm.SelectedIndex : 0, Days = mask, Action = Schedules.AllActions[act.SelectedIndex], Target = targets[tgt.SelectedIndex].Id, Trigger = trigger, Offset = offset };
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
