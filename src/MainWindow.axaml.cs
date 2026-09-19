namespace FsCopilot.Exerciser;

using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Connection;

public partial class MainWindow : Window
{
    private const int VkF9 = 0x78;

    private readonly DispatcherTimer _capture = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Recorder _recorder = new();
    private Session? _session;
    private IReadOnlyList<PanelInfo> _panels = [];
    private IReadOnlyList<SimWindow> _simWindows = [];
    private WriteableBitmap? _frame;
    private SimWindow? _target;
    private string[] _config = [];
    private string _sync = "none";
    private string _appRole = "";
    private bool _appUp;
    private bool _appRunning;
    private bool _waitingForPoint;
    private bool _linked;                   // a peer of ours is connected
    private bool _hadLink;                  // and one was, until it dropped
    private bool _left;                     // left on purpose, so Rejoin is the way back
    private bool _degraded;                 // dropped from this side, so Recover is the way back
    private int _windowTicks;
    private int _armedTicks;
    private string? _autoJoin;
    private int _sent, _received;

    public MainWindow()
    {
        InitializeComponent();
        View.GestureMade += OnGestureMade;
        _capture.Tick += (_, _) => GrabFrame();
        _capture.Start();
        // The window list every two seconds, the pop-out key eight times a second: the list
        // is cheap but not free, and a keypress missed by a quarter second feels broken.
        _watch.Tick += (_, _) =>
        {
            PollPopOutKey();
            if (++_windowTicks % 8 != 0) return;
            RefreshWindows();
            var running = Local.FsCopilotRunning;
            if (running != _appRunning) { _appRunning = running; Refresh(); }
        };
        _watch.Start();

        var options = Options.Current;
        if (options.Relay != null) SelectRelay(options.Relay);
        CodeBox.Text = options.Join ?? "";
        CodeBox.GetObservable(TextBox.TextProperty).Subscribe(_ => Refresh());

        _appRunning = Local.FsCopilotRunning;
        RefreshWindows();
        Opened += async (_, _) =>
        {
            StartSession();
            // Selected by default, so a one-machine run is fast without anyone choosing it.
            if (RelayHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) await EnsureRelay();
            if (options.Window != null) SelectWindow(options.Window);
            if (options.Join != null) OnJoin(this, new RoutedEventArgs());
            if (options.PopOut) await WaitThenPopOut();
            if (options.Press != null) await ScriptedPress(options.Press);
        };
        Closed += (_, _) => { PopOut.Disarm(); Local.StopRelay(); _session?.Dispose(); };
        Refresh();
    }

    private string RelayHost => (RelayBox.SelectedItem as ComboBoxItem)?.Content as string ?? "localhost";

    /// <summary>One place that decides what the window says, from what is true. Not
    /// called Show: that is Window's own, and hiding it is a trap for the next reader.</summary>
    private void Refresh()
    {
        var panel = PanelBox.SelectedItem as PanelInfo;
        var joined = _sync is "live" or "degraded";

        var stale = Sync.Stale();
        var synced = Sync.Read();
        SyncState.Text = stale != null ? "Out of date"
            : synced?.Commit != null ? synced.Commit
            : "Up to date";
        SyncState.Foreground = Ink(stale == null, waiting: stale != null);
        Problem(SyncProblem, stale);

        var local = RelayHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
        RelayState.Text = local ? Local.RelayRunning ? "Hosted here, up" : "Not started" : "Remote";
        RelayState.Foreground = Ink(!local || Local.RelayRunning);
        AppState.Text = _appUp ? "Connected" : "Not running";
        AppState.Foreground = Ink(_appUp);
        TrafficText.Text = $"Sent {_sent}   Received {_received}";

        // Restarting is the same button: the app has to come back on the same relay
        // with a code this window knows, which is exactly what starting it does.
        StartAppButton.Content = _appRunning ? "Restart FS Copilot" : "Start FS Copilot";

        // The app reports its own role; this side holds the other one.
        SessionState.Text = _degraded ? "Dropped from here"
            : _sync switch
        {
            "live" => _appRole switch
            {
                "master" => "Live, you are slave",
                "slave" => "Live, you are master",
                _ => "Live"
            },
            "degraded" => "The app lost its peer",
            "connecting" => "Connecting",
            _ => "Not joined"
        };
        SessionState.Foreground = Ink(_sync == "live" && !_degraded, _degraded || _sync is "connecting" or "degraded");
        var haveCode = (CodeBox.Text ?? "").Trim().Length > 0;
        JoinButton.IsEnabled = haveCode && !joined;
        // One button per thing that can be done, and each one flips to its undo.
        LeaveButton.Content = joined ? "Leave" : "Rejoin";
        LeaveButton.IsEnabled = joined || (haveCode && _left);
        ControlButton.Content = _appRole == "slave" ? "Give control" : "Take control";
        ControlButton.IsEnabled = _sync == "live";
        DegradeButton.Content = _degraded ? "Restore connection" : "Degrade connection";
        DegradeButton.IsEnabled = joined || _degraded;

        // Nothing on this page means anything without a session: no panel to press, no
        // gesture to receive, no overlay the app would renew.
        // A degraded session still has everything to look at: the panel is locked, the
        // app is holding history for us, and recovering is one button away. It is only a
        // session never joined, or left on purpose, that has nothing to show.
        var session = _linked || (_hadLink && (_degraded || _sync == "degraded"));
        PointerPage.IsVisible = session;
        NoSession.IsVisible = !session;

        PopOutButton.Content = _waitingForPoint ? "Cancel" : "Pop out";
        PopOutButton.IsEnabled = _waitingForPoint || (panel != null && _simWindows.Count > 0);
        PanelHint.Text = _waitingForPoint ? "Click the panel in the simulator; that click is swallowed"
            : panel == null ? ""
            : panel.Rect == null ? $"{panel.Key} reported no size"
            : _target == null ? $"Instrument {panel.Rect[0]}x{panel.Rect[1]}"
            : $"Instrument {panel.Rect[0]}x{panel.Rect[1]} · capturing {_target.Title}";
        ZoomText.Text = _frame == null ? "" : $"{View.ZoomPercent:0}%";
    }

    /// <summary>Three inks and no others: good, waiting, and nothing to report.</summary>
    private static IBrush Ink(bool good, bool waiting = false) =>
        new SolidColorBrush(good ? Color.FromRgb(0x6C, 0xC6, 0x8A)
            : waiting ? Color.FromRgb(0xE0, 0xB0, 0x5A)
            : Color.FromRgb(0x77, 0x77, 0x82));

    private void Problem(TextBlock where, string? text)
    {
        where.Text = text ?? "";
        where.IsVisible = text != null;
    }

    /* ---- connection ---- */

    private async Task<bool> EnsureRelay()
    {
        Problem(ConnectionProblem, null);
        var problem = await Local.StartRelay(Log);
        Problem(ConnectionProblem, problem);
        Refresh();
        return problem == null;
    }

    /// <summary>Hands the copy to sync-and-rebuild.ps1 and closes, because fsc/ holds assemblies this
    /// process has mapped and Windows will not let it replace those. The script waits for this
    /// pid, copies, and starts the exerciser again.</summary>
    private void OnSync(object? sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            Problem(SyncProblem, null);
            var problem = Sync.Start();
            if (problem != null) { Problem(SyncProblem, problem); return; }

            Log("syncing: closing so the copy can replace what this process has loaded");
            Close();
        }
        catch (Exception ex)
        {
            // A handler that throws takes the window with it, and a sync is the one button
            // most likely to meet a locked file or a missing path.
            Problem(SyncProblem, $"sync failed to start: {ex.Message}");
        }
        finally { SyncButton.IsEnabled = true; Refresh(); }
    }

    private async void OnStartApp(object? sender, RoutedEventArgs e)
    {
        StartAppButton.IsEnabled = false;
        try
        {
            if (RelayHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) && !await EnsureRelay()) return;

            if (Local.FsCopilotRunning)
            {
                Log("closing the running FS Copilot");
                await Local.StopFsCopilot();
                _appRunning = false;
            }

            if (!Local.TakesTestFlags())
            {
                // Started anyway, as the pilot would: the flags are the convenience, not the run.
                // Its own failure outranks the note about the flags, or a build that never starts
                // reads as a build that started without them.
                var flagless = Local.StartFsCopilot("", "");
                Problem(ConnectionProblem, flagless ??
                    "this app build takes no --relay or --peer-id: set its relay yourself and paste its code");
                return;
            }

            var code = "EX" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            var error = Local.StartFsCopilot(RelayHost, code);
            Problem(ConnectionProblem, error);
            if (error != null) return;
            CodeBox.Text = code;
            _autoJoin = code;
            _appRunning = true;
            Log($"started FS Copilot on {RelayHost} as {code}; joining when it answers");
        }
        finally { StartAppButton.IsEnabled = true; Refresh(); }
    }

    private async void OnRelayChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_session == null) return;                 // still building the window
        Log($"relay is now {RelayHost}");
        StartSession();
        if (RelayHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) await EnsureRelay();
        Refresh();
    }

    /* ---- session ---- */

    private void StartSession()
    {
        _session?.Dispose();
        _linked = false;
        _session = new Session(RelayHost);
        _session.Log += Log;
        _session.PanelsChanged += p => Post(() => { _panels = p; FillPanels(); Refresh(); });
        _session.ConfigChanged += c => Post(() => { _config = c.ToArray(); FillPanels(); Refresh(); });
        _session.AppChanged += up => Post(() =>
        {
            _appUp = up;
            if (!up) { _config = []; _panels = []; FillPanels(); }
            // An app this window started announced its code before it existed, so joining it
            // needs nobody to copy anything.
            if (up && _autoJoin != null) { _autoJoin = null; OnJoin(this, new RoutedEventArgs()); }
            Refresh();
        });
        _session.SyncChanged += (sync, role) => Post(() =>
        {
            _sync = sync;
            _appRole = role;
            Refresh();
        });
        _session.PeersChanged += _ => Post(() =>
        {
            _linked = _session?.Linked == true;
            if (_linked) { _hadLink = true; _degraded = false; }
            Refresh();
        });
        _session.GestureReceived += e => Post(() =>
        {
            _received++;
            if (e.Key == View.Key) View.ShowIncoming(e);
            _recorder.Note(e, false);
            Refresh();
        });
        _session.WatchPanels();
        Log($"exerciser {_session.PeerId} on {RelayHost}");
    }

    private void SelectRelay(string host)
    {
        var known = RelayBox.Items.Cast<ComboBoxItem>().ToList()
            .FindIndex(i => (i.Content as string ?? "").StartsWith(host, StringComparison.OrdinalIgnoreCase));
        if (known >= 0) { RelayBox.SelectedIndex = known; return; }
        RelayBox.Items.Add(new ComboBoxItem { Content = host });
        RelayBox.SelectedIndex = RelayBox.ItemCount - 1;
    }

    private async void OnJoin(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var code = (CodeBox.Text ?? "").Trim();
        if (code.Length == 0) return;
        Problem(SessionProblem, null);
        JoinButton.IsEnabled = false;
        try
        {
            var result = await _session.Join(code, CancellationToken.None);
            if (result != "Success") Problem(SessionProblem, $"join: {result}");
            else { _degraded = false; _left = false; }
        }
        catch (Exception ex) { Problem(SessionProblem, ex.Message); }
        finally { Refresh(); }
    }

    /// <summary>Leave, then come back: the same button, because a session that cannot be
    /// rejoined from where it was left is a dead end.</summary>
    private void OnLeave(object? sender, RoutedEventArgs e)
    {
        if (_sync is "live" or "degraded")
        {
            _session?.Leave();
            _left = true;
            _degraded = false;
            _hadLink = false;
        }
        else
        {
            _left = false;
            OnJoin(this, new RoutedEventArgs());
        }
        Refresh();
    }

    /// <summary>Master is whoever claimed it last. Giving it back means naming the app,
    /// which is the code in the box - the app's own peer id.</summary>
    private void OnControl(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (_appRole == "slave") _session.GiveControl((CodeBox.Text ?? "").Trim());
        else _session.TakeControl();
    }

    /// <summary>Degrade goes quiet with no goodbye, which is what the app's degraded state
    /// and its held history are for. Recover is a fresh join, because nothing in the
    /// transport re-establishes a link on its own.</summary>
    private async void OnDegrade(object? sender, RoutedEventArgs e)
    {
        if (_degraded)
        {
            _degraded = false;
            OnJoin(this, new RoutedEventArgs());
            return;
        }
        _degraded = true;
        Log("dropped the link with no goodbye; Restore connection to come back");
        StartSession();
        _hadLink = true;                          // the session is degraded, not over
        await Task.Delay(300);
        Refresh();
    }

    /* ---- panels, pop-out, capture ---- */

    private void FillPanels()
    {
        var wanted = (PanelBox.SelectedItem as PanelInfo)?.Key ?? Options.Current.Panel;
        var opted = _panels.Where(IsOpted).ToList();
        if (opted.Select(p => p.ToString()).SequenceEqual(PanelBox.Items.OfType<PanelInfo>().Select(p => p.ToString())))
            return;                                 // same list, so leave the selection alone
        PanelBox.ItemsSource = opted;
        if (opted.Count == 0) { PanelBox.SelectedIndex = -1; return; }
        var keep = opted.FindIndex(p => p.Key == wanted || p.Identifier == wanted);
        PanelBox.SelectedIndex = keep >= 0 ? keep : 0;
    }

    /// <summary>The profile's own rule: an entry without a '|' names an identifier and takes
    /// every panel with it, an entry with one names that panel.</summary>
    private bool IsOpted(PanelInfo p) => _config.Contains(p.Key) || _config.Contains(p.Identifier);

    private void OnPanelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PanelBox.SelectedItem is not PanelInfo panel) return;
        View.Key = panel.Key;
        View.PanelRect = panel.Rect;
        // A pop-out carries the identifier as its title, which is a shortcut rather than a
        // rule: two panels can share one - the A220's two CTPs differ only in a query the
        // title does not carry - so only a single match picks itself.
        var hits = Capture.Candidates(_simWindows, panel.Identifier);
        if (hits.Count == 1) WindowBox.SelectedItem = hits[0];
        Refresh();
    }

    private void OnPopOut(object? sender, RoutedEventArgs e)
    {
        if (_waitingForPoint) { CancelPopOut("cancelled"); return; }
        if (PanelBox.SelectedItem is not PanelInfo) return;

        var handles = _simWindows.Select(w => w.Handle).ToList();
        if (!PopOut.ArmForClick(p => PopOut.WindowAt(handles, p), (sim, at) => Post(() => FirePopOut(sim, at))))
            Log("pop out: could not watch for a click; point at the panel and press F9 instead");
        _waitingForPoint = true;
        _armedTicks = 0;
        Log("pop out: click the panel in the simulator — that click is swallowed, nothing is pressed");
        Refresh();
    }

    private void CancelPopOut(string why)
    {
        PopOut.Disarm();
        _waitingForPoint = false;
        Log($"pop out: {why}");
        Refresh();
    }

    private async void FirePopOut(IntPtr sim, Point? at = null)
    {
        if (!_waitingForPoint) return;
        _waitingForPoint = false;
        PopOut.Disarm();
        var before = _simWindows.Select(w => w.Handle).ToHashSet();
        var title = _simWindows.FirstOrDefault(w => w.Handle == sim)?.Title ?? "";
        Refresh();
        // Off the UI thread: the sequence waits out the simulator between steps, and a UI
        // thread that is sleeping is a window that has stopped answering.
        var self = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await Task.Run(() => PopOut.RightAltClick(sim, self, at));
        Log($"pop out: sent Right-Alt + click into {(title.Length == 0 ? "the simulator" : title)}");
        await WaitForNewWindow(before);
    }

    /// <summary>--popout, which arrives before the app has said which panels exist.</summary>
    private async Task WaitThenPopOut()
    {
        for (var i = 0; i < 120 && PanelBox.SelectedItem == null; i++) await Task.Delay(500);
        OnPopOut(this, new RoutedEventArgs());
    }

    private void PollPopOutKey()
    {
        if (!_waitingForPoint) return;
        // Armed state is not left lying around: a hook that eats clicks has to stop eating
        // them whether or not anyone remembers it is on.
        if (++_armedTicks > 240) { CancelPopOut("nothing clicked; stopped waiting"); return; }
        if (!PopOut.KeyDown(VkF9)) return;
        var sim = _simWindows.FirstOrDefault(w => PopOut.CursorOver(w.Handle));
        if (sim == null) { Log("pop out: the cursor is not over a simulator window"); return; }
        FirePopOut(sim.Handle);
    }

    /// <summary>The new window is the one the pop-out made. Its title is the instrument
    /// identifier, so a mismatch is worth saying: the pilot pointed at another panel.</summary>
    private async Task WaitForNewWindow(HashSet<IntPtr> before)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            var now = Capture.SimWindows();
            var fresh = now.FirstOrDefault(w => !before.Contains(w.Handle));
            if (fresh == null) continue;
            // The window is left at the size the simulator gave it. Resizing it down
            // resamples the panel, and zooming into the capture then magnifies a blur.
            Post(() =>
            {
                _simWindows = now;
                WindowBox.ItemsSource = now;
                WindowBox.SelectedItem = now.FirstOrDefault(w => w.Handle == fresh.Handle);
                if (PanelBox.SelectedItem is PanelInfo panel &&
                    !string.Equals(fresh.Title, panel.Identifier, StringComparison.OrdinalIgnoreCase))
                    Log($"popped out {fresh.Title}, but the panel selected here is {panel.Identifier}");
                Log($"capturing {fresh.Title}");
                Refresh();
            });
            return;
        }
        Post(() => Log("pop out: no new window appeared. Right-Alt + click the panel yourself, then pick its window."));
    }

    private void OnWindowChanged(object? sender, SelectionChangedEventArgs e)
    {
        _target = WindowBox.SelectedItem as SimWindow;
        _frame = null;
        Refresh();
    }

    private void OnRefreshWindows(object? sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var found = Capture.SimWindows();
        if (found.Select(w => w.ToString()).SequenceEqual(_simWindows.Select(w => w.ToString()))) return;
        _simWindows = found;
        var selected = _target?.Handle;
        WindowBox.ItemsSource = found;
        var keep = found.ToList().FindIndex(w => w.Handle == selected);
        if (keep >= 0) WindowBox.SelectedIndex = keep;
        else if (_target != null) { _target = null; _frame = null; }
        Refresh();
    }

    private void SelectWindow(string title)
    {
        var hit = _simWindows.ToList().FindIndex(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (hit >= 0) WindowBox.SelectedIndex = hit;
        else Log($"no simulator window matching '{title}'");
    }

    private void GrabFrame()
    {
        if (_target == null) { View.ShowFrame(null); return; }
        View.ShowFrame(Capture.Grab(_target.Handle, ref _frame));
        if (_frame != null) ZoomText.Text = $"{View.ZoomPercent:0}%";
    }

    private void OnFit(object? sender, RoutedEventArgs e) { View.ResetView(); Refresh(); }
    private void OnFitHeight(object? sender, RoutedEventArgs e) { View.FitHeight(); Refresh(); }
    private void OnActualSize(object? sender, RoutedEventArgs e) { View.ActualSize(); Refresh(); }

    /* ---- overlays ---- */

    /// <summary>The overlays follow sync state, so showing one on demand means asking the
    /// panel itself. pointer.js keeps window.fscOverlay for this and holds it against the
    /// state renewals until cleared.</summary>
    private async void OnOverlay(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string state }) return;
        await Panel($"window.fscOverlay('{state}')");
    }

    private async void OnForceClear(object? sender, RoutedEventArgs e)
    {
        OverlayNone.IsChecked = true;
        await Panel("window.fscUnlock()");
    }

    private async Task Panel(string expression)
    {
        var key = View.Key;
        if (key.Length == 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var page = await Inspector.PanelPage(key, cts.Token);
            if (page < 0)
            {
                Log($"no panel document holds {key}; is the simulator's inspector on 19999?");
                return;
            }
            Log($"panel: {await Inspector.Evaluate(page, expression, cts.Token)}");
        }
        catch (Exception ex) { Log($"panel: {ex.Message}"); }
    }

    /* ---- gestures ---- */

    private void OnGestureMade(PointerEvent gesture)
    {
        if (_session == null) return;
        _session.Send(gesture);
        _recorder.Note(gesture, true);
        _sent++;
        Refresh();
    }

    /// <summary>One press from the command line, for a run with nobody at the mouse. Live is
    /// not enough: a panel has to have helloed the app too, or the gesture is dropped there
    /// with "No panel for ..." and the run looks like a routing fault.</summary>
    private async Task ScriptedPress(string spec)
    {
        var parts = spec.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[0], out var x) || !float.TryParse(parts[1], out var y))
        {
            Log($"--press wants x,y fractions, got '{spec}'");
            return;
        }
        for (var i = 0; i < 120 && (_sync != "live" || PanelBox.SelectedItem == null); i++) await Task.Delay(500);
        if (PanelBox.SelectedItem is not PanelInfo panel) { Log("--press gave up: no panel"); return; }
        await Task.Delay(500);
        OnGestureMade(new PointerEvent(panel.Key, 0, 0, 0, PointerKind.Press, 0, 120, 0, x, y, x, y, PointerEvent.NoPath));
    }

    private void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (RecordButton.IsChecked == true) { _recorder.Start(); Log("recording"); }
        else Log($"recorded {_recorder.Stop()} gesture(s) to {_recorder.Path}");
    }

    private async void OnReplay(object? sender, RoutedEventArgs e)
    {
        if (_session == null || View.Key.Length == 0) return;
        PlayButton.IsEnabled = false;
        try
        {
            var played = await _recorder.Replay(View.Key, g => { _session.Send(g); _sent++; Refresh(); });
            Log($"replayed {played} gesture(s)");
        }
        catch (Exception ex) { Log($"replay failed: {ex.Message}"); }
        finally { PlayButton.IsEnabled = true; }
    }

    /* ---- log ---- */

    private void Post(Action a) => Dispatcher.UIThread.Post(a);

    private void Log(string line) => Post(() =>
    {
        Serilog.Log.Information(line);
        LogText.Text = $"{DateTime.Now:HH:mm:ss}  {line}\n{LogText.Text}";
        if (LogText.Text!.Length > 20000) LogText.Text = LogText.Text[..20000];
    });

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(LogText.Text ?? "");
    }

    private void OnClearLog(object? sender, RoutedEventArgs e) => LogText.Text = "";
}
