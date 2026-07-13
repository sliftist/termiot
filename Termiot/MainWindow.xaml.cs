using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Termiot.Terminal;
using Termiot.Ui;

namespace Termiot;

// One MainWindow per process; the process IS the window. Shells are independent host processes — this window merely watches a set of them over named pipes, so moving a tab between windows is just a change of watcher.
public partial class MainWindow : Window
{
    private const string DefaultTabTitle = "cmd";
    private const int StateSaveDebounceMs = 2000;
    private const int RenderTickMs = 16;
    private const double DragStartThresholdPx = 6;
    // Identifies our WM_COPYDATA adoption messages; anything else is ignored.
    private static readonly IntPtr AdoptMessageId = (IntPtr)0x7E541071;
    // The old watcher's Detach must land before the new watcher dials the pipe, or the connect attempt spawns a duplicate host.
    private const int DragHandoffDelayMs = 400;
    private static readonly Regex PromptRegex = new(@"^([A-Za-z]:\\[^<>|?*""]*?)>\s*$", RegexOptions.Compiled);

    private sealed class TabVm
    {
        public TabInfo Info = new();
        public TermScreen Screen = null!;
        public VtParser Parser = null!;
        // Null while the tab is a not-yet-resumed restore: the tab and its saved state exist, but no shell process runs until the user confirms (or auto-resume is on).
        public ShellSession? Session;
        public bool Dead;
        // True from the moment a session starts until its replay burst settles: the terminal is held behind a loading overlay so the recent history isn't animated frame-by-frame, then revealed in one paint. Purely client-side — doesn't depend on the host build. Volatile because the render tick reads it while output arrives on the reader thread.
        public volatile bool Replaying;
        // Environment.TickCount64 when the session started and when its last output byte arrived; the render tick reveals once output has been quiet for a moment (the replay finished) or a hard cap elapses.
        public long ReplayStartTicks;
        public long LastOutputTicks;
        public int Dirty;
        public bool Win32Input;
        // Runtime: the connected host reported it's running elevated (drives the admin badge). Distinct from Info.Elevated, which is the sticky preference.
        public bool Elevated;
        public bool LoggedFirstRender;
        public string PendingInput = "";
        public string LastCommand = "";
        public CommandHistory History = null!;
        // Per-tab search/filter, so switching tabs restores each tab's own search instead of carrying one query across all of them.
        public string SearchQuery = "";
        public bool SearchOpen;
        public bool SearchFilter;
        public int SearchMatchIndex = -1;
        // Raw-keys mode has no input box, so the current line is reconstructed from keystrokes to feed history: printable text accumulates, Backspace pops, plain Enter commits. Tainted (paste, navigation/edit keys, or over the keystroke cap) → the line is not stored.
        public readonly System.Text.StringBuilder RawHistoryLine = new();
        public int RawHistoryKeys;
        public bool RawHistoryTainted;
        public bool Running;
        // (command, absolute line index) pairs from termiot-cmd markers; guarded by Screen.Sync (appended during parsing, read for LLM context).
        public List<(string Cmd, int Line)> CommandMarks = new();
        // Set when the shell process names itself via OSC title sequences; empty = use our automatic "folder + command" title.
        public string CustomTitle = "";
        // Latest raw OSC title (written on the reader thread) and a flag the render tick consumes — coalesced so a replay with thousands of title changes triggers one header relayout per frame, not thousands of dispatches.
        public volatile string? PendingTitle;
        public int TitleDirty;
        // Cached AUTORESUME.cmd contents ("" = none), refreshed off the UI thread so the hot title-refresh path never touches disk. Null = not yet loaded.
        public string? AutoResumeContent;
        public Border Header = null!;
        public TextBlock HeaderText = null!;
        public TextBox TitleEditor = null!;
        public TextBlock AutoResumeButton = null!;
        public TextBlock RestartButton = null!;
        public TextBlock RevertTitleButton = null!;
        public TextBlock Win32Badge = null!;
        public TextBlock AdminBadge = null!;
        public StackPanel ResourceRow = null!;
        public double StickyHeaderWidth;
        // Width stickiness: when the header last recompacted and how many recompactions landed in quick succession — enough rapid ones pin the width to StickyPinWidth until StickyPinnedUntilTicks, so a title whose width keeps jumping stops reflowing the rows.
        public long StickyLastRecompactTicks;
        public int StickyRapidChanges;
        public double StickyPinWidth;
        public long StickyPinnedUntilTicks;
        // Index order: VRAM, CPU, memory.
        public TextBlock[] ResourceTexts = null!;
        public System.Windows.Shapes.Rectangle[] ResourceBars = null!;
    }

    private readonly string _windowId;
    private readonly AppSettings _settings;
    private readonly List<TabVm> _tabs = new();
    private int _active = -1;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _syncTimer;
    private List<(int Line, int Col)> _matches = new();
    private int _matchIndex = -1;
    // Incremental search: committed (scrollback) matches are scanned once and kept here; only newly-committed lines and the volatile live screen are rescanned as output arrives. _searchScanned is the scrollback count already folded in; _searchDropped detects cap trimming (which shifts indices, forcing a full rescan).
    private List<(int Line, int Col)> _stableMatches = new();
    private int _searchScanned;
    private long _searchDropped;
    private long _lastSearchUpdateTicks;
    private const int SearchUpdateThrottleMs = 100;
    private const int YarnNamesCacheMs = 5000;
    // Regular candidates are recomputed at most this often after typing stops, off the UI thread, so directory enumeration never blocks keystrokes.
    private const int CandDebounceMs = 50;

    private string _yarnNamesCwd = "";
    private long _yarnNamesTick;
    private List<string> _yarnNames = new();
    private readonly LlmPredictor _predictor;
    private readonly ResourceSampler _resources = new();
    private readonly DispatcherTimer _llmTimer;
    private readonly DispatcherTimer _candTimer;
    // Bumped on every regular-candidate recompute; a background result whose generation is stale (or whose input has changed) is discarded.
    private int _candComputeGen;
    // The input text _candidates was computed for — lets Tab tell whether the async result is stale and recompute synchronously.
    private string _candText = "";
    private readonly object _yarnLock = new();
    private readonly List<TextBlock> _suggestionRows = new();
    // The unified suggestion list shown under the input: LLM predictions when the LLM toggle is on, otherwise the regular candidates (history, yarn, filesystem). Tab walks _candIndex through the whole list; _candWindowStart is the rolling display window.
    private List<string> _candidates = new();
    private int _candIndex = -1;
    private int _candWindowStart;
    private string _candBase = "";
    // History walking (Up from the "center"): entries swap into the input without appearing in the suggestion rows; Down walks back toward the stashed typed text. -1 = not in history.
    private int _histIndex = -1;
    private List<string> _histEntries = new();
    private string _histStash = "";

    private const int RawKeyLogMaxChars = 400;
    private string _rawKeyLog = "";
    private const int WindowFileSyncMs = 15000;
    // Gives the dying host time to release its pipe name and exit before the replacement spawns.
    private const int ShellRestartDelayMs = 500;
    // Upper bound on waiting for a killed host to actually vanish before spawning its replacement (returns as soon as it's gone, usually tens of ms).
    private const int HostDeathWaitMs = 2000;
    // Keep the restart progress icon this long after the new shell is up, so a fast restart is still noticeable.
    private const int RestartIconLingerMs = 2000;
    // Gap between starting each non-focused tab's session during restore, so several heavy shells never replay/parse their history simultaneously.
    private const int RestoreStaggerMs = 120;
    // Non-focused tabs' history reconstruction is held until this long after the focused tab's is done (or until the tab is switched to), so it can't slow the initial load.
    private const int BackgroundHeadDelayMs = 5000;
    // Don't flash the loading overlay for replays that finish quickly; only show it if the replay is still running after this long.
    private const int LoadingOverlayDelayMs = 200;
    // The replay burst is a back-to-back stream of frames; once no output has arrived for this long, it's finished and we reveal the caught-up screen.
    private const int RevealIdleMs = 60;
    // Hard cap so a tab can never stay stuck behind the overlay (e.g. a shell spewing continuously right at reconnect never goes idle).
    private const int ReplayRevealTimeoutMs = 5000;
    // Hard ceiling on the rebuild: a build that wedges (rather than failing) gets killed so the reload button can never hang on the hourglass forever.
    private const int ReloadTimeoutMinutes = 3;
    // How many trailing build-log lines the reload button's tooltip shows (the full log lives in the settings window).
    private const int BuildLogTooltipTailLines = 40;

    private const int LlmDebounceMs = 400;
    private const int LlmMaxPairs = 20;
    private const int LlmMaxOutputChars = 3000;
    private const int LlmMaxOutputLines = 40;
    private bool _settingInputText;
    // Set while restoring a tab's search state so the SearchBox/FilterToggle change handlers don't recompute mid-restore.
    private bool _restoringSearch;
    private bool _closedBecauseEmpty;
    // Set once the window starts closing so the async restore (and its staggered follow-ups) stop creating tabs on a window that's going away.
    private bool _closing;
    // Startup profiling: mark the first replay byte and the first painted frame exactly once each.
    private int _tracedFirstOutput;
    private bool _tracedFirstRender;
    // The tab focused at restore, and whether its completion has kicked off the delayed background-history reconstruction.
    private TabVm? _restoreFocusedTab;
    private bool _backgroundHeadsScheduled;
    // Count of title-change dispatches queued to the UI thread — a flood here would be its own source of stall.
    private int _titleDispatches;
    private TextBox? _activeTitleEditor;
    // Checkbox Checked/Unchecked handlers fire from the ctor's programmatic IsChecked assignments; side effects (saving, opening windows) must wait for real user interaction.
    private readonly bool _uiReady;
    private Point _dragStart;
    private TabVm? _dragCandidate;
    private TabVm? _draggingVm;
    private DragGhost? _dragGhost;

    public MainWindow(string windowId, Termiot.WindowState state, bool forceResume = false, bool takeFocus = false)
    {
        _windowId = windowId;
        StartupTrace.Mark("ctor-start");
        // Captured before our own window can become foreground: new windows center over whatever the user was focused on when they launched us.
        var launchForeground = GetForegroundWindow();
        InitializeComponent();
        StartupTrace.Mark("init-component");
        _inlinePlus = new Button { Content = "+", Width = 30, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), BorderThickness = new Thickness(0), FontSize = 16 };
        _inlinePlus.Click += NewTabBtn_Click;
        _settings = AppSettings.Load();
        StartupTrace.Mark("settings+history");
        // Explicit launches (e.g. Cursor's Ctrl+Shift+C) take focus — the user asked for a terminal. Everything else (restores, respawns) surfaces via the topmost flip without stealing focus.
        ShowActivated = takeFocus;
        Loaded += (_, _) =>
        {
            if (takeFocus)
            {
                ForceForeground();
            }
            else
            {
                BringToTopWithoutFocus();
            }
        };
        if (state.X is { } x && state.Y is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => CenterOverWindow(launchForeground);
        }
        if (state.Width is { } w and > 100 && state.Height is { } h and > 100)
        {
            Width = w;
            Height = h;
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StateSaveDebounceMs) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveState();
        };
        // Output events only set a dirty flag; this timer does the repaints. Rendering directly from output notifications at Render priority starves input dispatching under heavy output and freezes the window.
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(RenderTickMs) };
        _renderTimer.Tick += (_, _) => RenderDirtyTabs();
        _renderTimer.Start();
        // The window file is the single source of truth for window↔shell ownership; re-reading it periodically picks up shells assigned to this window by other processes even if their handoff message was lost.
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WindowFileSyncMs) };
        _syncTimer.Tick += (_, _) => SyncTabsFromWindowFileAsync();
        _syncTimer.Start();

        _predictor = new LlmPredictor(_settings);
        _predictor.Updated += () => Dispatcher.BeginInvoke(() =>
        {
            if (UseLlmFor(InputBox.Text))
            {
                // Models love echoing the typed text back and repeating themselves; neither is a useful suggestion.
                _candidates = _predictor.Suggestions
                    .Where(s => s.Trim().Length > 0 && !string.Equals(s.Trim(), InputBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _candIndex = -1;
                _candWindowStart = 0;
            }
            UpdateLlmUi();
        });
        _llmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LlmDebounceMs) };
        _llmTimer.Tick += (_, _) =>
        {
            _llmTimer.Stop();
            RequestLlmPrediction();
        };
        _candTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CandDebounceMs) };
        _candTimer.Tick += (_, _) =>
        {
            _candTimer.Stop();
            ComputeRegularCandidatesAsync();
        };
        BuildTimeText.Text = BuildInfo.Display;
        ReloadBtn.Visibility = BuildInfo.HasSource ? Visibility.Visible : Visibility.Collapsed;
        WindowNameBox.Text = state.Name;
        FpsText.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        var statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statsTimer.Tick += (_, _) =>
        {
            if (_settings.ShowFps)
            {
                var (count, avgMs) = Term.TakeFrameStats();
                FpsText.Text = count > 0 ? $"{count} fps · {avgMs:0.0}ms ≈{Math.Min(9999, 1000 / Math.Max(0.01, avgMs)):0} cap" : "0 fps";
            }
            UpdateResourceRows();
            UpdateScrollStats();
            long nowTicks = Environment.TickCount64;
            if (_tabs.Any(t => t.StickyPinnedUntilTicks != 0 && nowTicks >= t.StickyPinnedUntilTicks))
            {
                RelayoutTabRows();
            }
        };
        statsTimer.Start();
        LlmToggle.IsChecked = _settings.LlmEnabled;
        MultiToggle.IsChecked = _settings.LlmMultiComplete;
        RawToggle.IsChecked = _settings.RawInput;
        _uiReady = true;
        ApplyLlmUi();
        ApplyInputMode();
        // This is a command line: typing must always land in it. Keyboard focus is denied to everything except the input box, the search box, and the terminal in raw-keys mode — buttons and tabs still work from mouse clicks, which don't need keyboard focus.
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, InputBox) || ReferenceEquals(e.NewFocus, SearchBox) || (RawMode && ReferenceEquals(e.NewFocus, Term)) || (_activeTitleEditor != null && ReferenceEquals(e.NewFocus, _activeTitleEditor)))
            {
                return;
            }
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusInput);
        };
        Activated += (_, _) =>
        {
            LastActiveWindow.Save(_windowId, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusInput);
        };
        FocusSelectAll.Attach(SearchBox);
        // Only width changes rewrap the text and change the line count; reacting to our own height set (WidthChanged false) would just re-enter.
        InputBox.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                AdjustInputHeight();
            }
        };
        Scrollbar.Attach(Term);
        Term.CellSizeChanged += OnCellSizeChanged;
        Term.KeyDown += Term_KeyDown;
        Term.TextInput += Term_TextInput;
        PreviewKeyDown += Window_PreviewKeyDown;
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) =>
        {
            ScheduleSave();
            RelayoutTabRows();
        };
        // The scroller's slot shrinks/grows as the top-bar controls (fps, build text, window name) change size; row 0's capacity follows it.
        TabScroller.SizeChanged += (_, _) => RelayoutTabRows();
        // Tab adoption between our windows and open-tab-here requests arrive as WM_COPYDATA — a private channel, no OLE drag-drop involved.
        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            LastActiveWindow.Save(_windowId, handle);
        };
        Closing += (_, _) =>
        {
            _closing = true;
            if (_closedBecauseEmpty)
            {
                Termiot.WindowState.Delete(_windowId);
            }
            else
            {
                SaveState(closedCleanly: !Program.SessionEnding, closing: true);
            }
            _settingsWindow?.Close();
            LastActiveWindow.Remove(_windowId);
            // Timers must not outlive the window — the dispatcher may keep running (takeover teardown).
            _saveTimer.Stop();
            _renderTimer.Stop();
            _syncTimer.Stop();
            _llmTimer.Stop();
            foreach (var tab in _tabs)
            {
                tab.Session?.Detach();
            }
        };

        // Restore: a shell host that survived (window closed or crashed moments ago) is reused as-is; a terminated one gets a tab with a resume confirmation instead of silently starting a new process — unless auto-resume is enabled, which resumes immediately (running the shell's AUTORESUME.cmd if present).
        HttpOpenHost.Start(dir => Dispatcher.BeginInvoke(() =>
        {
            CreateTab(NewTabInfo(dir), activate: true, insertAfterActive: true);
            ForceForeground();
        }));
        BeginRestore(state, forceResume);
        StartupTrace.Mark("tabs-created");
        Loaded += (_, _) =>
        {
            StartupTrace.Mark("window-loaded");
            FocusInput();
        };
        ContentRendered += (_, _) =>
        {
            StartupTrace.Mark("content-rendered");
            StartupTrace.Flush();
        };
    }

    private sealed class RestoreEntry
    {
        public TabInfo Info = null!;
        public bool Alive;
        public bool EverRan;
    }

    // Restore is fully off the UI thread up to the point of touching controls: all per-shell disk reads (shell.json, host.json liveness, the died-near-close filter) happen in the background, then tabs are created and their sessions started focused-first and staggered — so the tab the user sees loads and renders before the others begin, and no heavy shell's replay/parse piles onto another's.
    private void BeginRestore(Termiot.WindowState state, bool forceResume)
    {
        string focusedId = state.Shells.Count > 0 ? state.Shells[Math.Clamp(state.ActiveIndex, 0, state.Shells.Count - 1)] : "";
        long exitCutoff = state.ClosedAtTicks > 0 ? state.ClosedAtTicks - TimeSpan.FromSeconds(Termiot.WindowState.ExitNearCloseGraceSeconds).Ticks : 0;
        StartupTrace.Mark($"restore-kickoff({state.Shells.Count})");
        Task.Run(() =>
        {
            // Gap from restore-kickoff shows thread-pool spin-up; the gap to restore-entries-ready is the disk-read + liveness cost.
            StartupTrace.Mark("restore-bg-entered");
            var entries = new List<RestoreEntry>();
            foreach (var id in state.Shells)
            {
                if (!Directory.Exists(AppPaths.ShellDir(id)))
                {
                    continue;
                }
                var info = ShellInfo.Load(id) ?? new ShellInfo();
                // One liveness check per shell, reused for both the died-near-close filter and the entry (Process.GetProcessById + StartTime is not free).
                bool alive = HostInfo.IsShellAlive(id);
                if (exitCutoff > 0 && info.ExitedAtTicks != 0 && info.ExitedAtTicks < exitCutoff && !alive)
                {
                    continue;
                }
                entries.Add(new RestoreEntry
                {
                    Info = new TabInfo { Id = id, Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = info.EnsureOrder, ExitedAtTicks = info.ExitedAtTicks },
                    Alive = alive,
                    // A shell that never ran (no host.json) is a fresh seed, not a terminated session — start it outright instead of asking to resume.
                    EverRan = File.Exists(AppPaths.HostInfoFile(id)),
                });
            }
            StartupTrace.Mark($"restore-entries-ready({entries.Count})");
            Dispatcher.BeginInvoke(() => ApplyRestore(entries, focusedId, forceResume));
        });
    }

    private void ApplyRestore(List<RestoreEntry> entries, string focusedId, bool forceResume)
    {
        if (_closing || _closedBecauseEmpty || entries.Count == 0)
        {
            return;
        }
        // Gap from restore-entries-ready shows how long the UI thread was busy before it could run this; the next gaps are tab-header construction and starting the focused shell's session.
        StartupTrace.Mark("restore-apply-start");
        var vms = entries.Select(e => CreateTab(e.Info, activate: false, start: false)).ToList();
        StartupTrace.Mark($"restore-tabs-created({vms.Count})");
        int focusedIdx = entries.FindIndex(e => e.Info.Id == focusedId);
        if (focusedIdx < 0)
        {
            focusedIdx = 0;
        }
        _restoreFocusedTab = vms[focusedIdx];
        ActivateTab(focusedIdx);
        StartRestored(vms[focusedIdx], entries[focusedIdx], forceResume);
        if (vms[focusedIdx].Session is { } focusedSession)
        {
            // Focused tab: parse and reconstruct as part of the initial load. Its completion starts the delay before the rest.
            focusedSession.Activate();
        }
        else
        {
            // No focused session to wait on (e.g. a dead shell showing the resume prompt) — schedule the rest directly.
            ScheduleBackgroundHeads();
        }
        StartupTrace.Mark("restore-focused-started");
        // Flush now so the timeline is on disk even though this is well past ContentRendered; first-output-frame and first-tab-render refine it.
        StartupTrace.Flush();
        var rest = Enumerable.Range(0, vms.Count).Where(i => i != focusedIdx).ToList();
        StaggerStartRest(rest, vms, entries, forceResume, 0);
    }

    private void StaggerStartRest(List<int> order, List<TabVm> vms, List<RestoreEntry> entries, bool forceResume, int i)
    {
        if (_closing || i >= order.Count)
        {
            return;
        }
        int idx = order[i];
        if (_tabs.Contains(vms[idx]))
        {
            StartRestored(vms[idx], entries[idx], forceResume);
        }
        // Spread the background head-parses so several heavy shells never fill in their history at once.
        Task.Delay(RestoreStaggerMs).ContinueWith(_ => Dispatcher.BeginInvoke(() => StaggerStartRest(order, vms, entries, forceResume, i + 1)));
    }

    // Kick off the delay after which every not-yet-reconstructed tab gets its history rebuilt (unless the user switches to it first, which reconstructs it immediately).
    private void ScheduleBackgroundHeads()
    {
        if (_backgroundHeadsScheduled)
        {
            return;
        }
        _backgroundHeadsScheduled = true;
        Task.Delay(BackgroundHeadDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(ParseBackgroundHeads));
    }

    private void ParseBackgroundHeads()
    {
        foreach (var vm in _tabs)
        {
            if (vm != _restoreFocusedTab)
            {
                vm.Session?.Activate();
            }
        }
    }

    private void StartRestored(TabVm vm, RestoreEntry entry, bool forceResume)
    {
        if (entry.Alive || !entry.EverRan)
        {
            StartSession(vm);
        }
        if (!entry.Alive && entry.EverRan && (_settings.AutoResumeShells || forceResume))
        {
            ResumeTab(vm, runAutoResumeCommand: true);
        }
        else if (!entry.Alive && !entry.EverRan)
        {
            // Fresh seeds with a pre-written AUTORESUME.cmd (--ensure launches) run their command immediately.
            RunAutoResumeCommand(vm);
        }
    }

    private TabVm? Active => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    private bool RawMode => RawToggle.IsChecked.GetValueOrDefault();

    private static TabInfo NewTabInfo(string cwd)
    {
        return new TabInfo { Id = Program.NewShellId(), Cwd = cwd, Title = DefaultTabTitle };
    }

    private TabVm CreateTab(TabInfo info, bool activate, bool start = true, bool insertAfterActive = false, int insertIndex = -1)
    {
        var screen = new TermScreen(120, 30) { ScrollbackCap = _settings.ScrollbackLines };
        var parser = new VtParser(screen) { ShowEscapes = _settings.ShowEscapeSequences };
        var vm = new TabVm { Info = info, Screen = screen, Parser = parser, PendingInput = info.PendingInput, Win32Input = info.Win32Input, History = new CommandHistory(AppPaths.ShellHistoryFile(info.Id)) };
        // Just record the latest title; the render tick applies it (see RenderDirtyTabs). Dispatching per sequence floods the UI thread — a heavy replay carries thousands of title changes.
        parser.OnTitle = title =>
        {
            Interlocked.Increment(ref _titleDispatches);
            vm.PendingTitle = title;
            Interlocked.Exchange(ref vm.TitleDirty, 1);
        };
        parser.OnCommandMarker = cmd => vm.CommandMarks.Add((cmd, vm.Screen.ScrollbackCount + vm.Screen.CursorY));
        parser.OnWin32InputMode = on =>
        {
            vm.Win32Input = on;
            Dispatcher.BeginInvoke(() =>
            {
                if (vm.Info.Win32Input != on)
                {
                    vm.Info.Win32Input = on;
                    ScheduleSave();
                }
                RefreshTabTitle(vm);
                if (vm == Active)
                {
                    UpdateWindowTitle();
                }
            });
        };
        BuildTabHeader(vm);
        RefreshTabTitle(vm);
        RefreshAutoResumeState(vm);
        int index = insertIndex >= 0 ? Math.Clamp(insertIndex, 0, _tabs.Count) : insertAfterActive && _active >= 0 && _active < _tabs.Count ? _active + 1 : _tabs.Count;
        _tabs.Insert(index, vm);
        if (start)
        {
            StartSession(vm);
        }
        RefreshTabHeaders();
        ScheduleSave();
        if (activate)
        {
            ActivateTab(index);
        }
        return vm;
    }

    private void StartSession(TabVm vm)
    {
        if (vm.Info.ExitedAtTicks != 0)
        {
            vm.Info.ExitedAtTicks = 0;
            ShellInfo.Save(vm.Info);
        }
        var session = ShellSession.Create(vm.Info.Id, string.IsNullOrEmpty(vm.Info.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : vm.Info.Cwd, _windowId, vm.Screen, vm.Parser, vm.Info.Elevated);
        vm.Session = session;
        session.ElevatedReported += elevated => Dispatcher.BeginInvoke(() =>
        {
            if (vm.Session != session)
            {
                return;
            }
            vm.Elevated = elevated;
            RefreshTabTitle(vm);
        });
        // Hold painting from the outset: parse the replay silently, then reveal the caught-up screen in one paint (see the reveal check in RenderDirtyTabs). Set before Begin so no frame can paint first.
        vm.ReplayStartTicks = Environment.TickCount64;
        vm.LastOutputTicks = 0;
        vm.Replaying = true;
        Task.Delay(LoadingOverlayDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(UpdateLoadingOverlay));
        session.OutputReceived += () =>
        {
            vm.LastOutputTicks = Environment.TickCount64;
            Interlocked.Exchange(ref vm.Dirty, 1);
            if (Interlocked.Exchange(ref _tracedFirstOutput, 1) == 0)
            {
                StartupTrace.Mark("first-output-frame");
            }
        };
        // Restored history is prepended above the tail already on screen, shifting every existing line down; command marks are absolute line indices, so move them by the same amount. Raised under Screen.Sync (which also guards CommandMarks), so this stays consistent with parsing and LLM-context reads.
        session.ScrollbackPrepended += delta =>
        {
            for (int i = 0; i < vm.CommandMarks.Count; i++)
            {
                vm.CommandMarks[i] = (vm.CommandMarks[i].Cmd, vm.CommandMarks[i].Line + delta);
            }
            // Prepending shifts every line's index down by delta; keep a scrolled-up view of the active tab on the same content.
            Dispatcher.BeginInvoke(() =>
            {
                if (vm == Active)
                {
                    Term.ShiftScrollAnchor(delta);
                    // Prepend invalidates the incremental search cache's absolute indices — rescan from scratch.
                    if (SearchBar.Visibility == Visibility.Visible)
                    {
                        RecomputeSearch();
                    }
                }
            });
        };
        // Once the focused tab's older history is loaded, wait, then load the rest — so background tabs never compete with the initial load.
        session.ScrollbackLoaded += () => Dispatcher.BeginInvoke(() =>
        {
            if (vm == _restoreFocusedTab)
            {
                ScheduleBackgroundHeads();
            }
        });
        session.Exited += _ => Dispatcher.BeginInvoke(() =>
        {
            // A stale Exited from a replaced session (e.g. after a shell restart) must not mark the new one dead.
            if (vm.Session != session)
            {
                return;
            }
            vm.Dead = true;
            vm.Info.ExitedAtTicks = DateTime.UtcNow.Ticks;
            ShellInfo.Save(vm.Info);
            if (vm == Active)
            {
                Term.ShowTermCursor = RawMode && !vm.Dead;
                Term.RenderFrame();
            }
        });
        session.Begin();
        if (Term.Cols > 0)
        {
            lock (vm.Screen.Sync)
            {
                vm.Screen.Resize(Term.Cols, Term.RowsVisible);
            }
            vm.Session.Resize(Term.Cols, Term.RowsVisible);
        }
    }

    private void ResumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Active is { Session: null } vm)
        {
            ResumeTab(vm, runAutoResumeCommand: true);
        }
    }

    private void ResumeNoRunBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Active is { Session: null } vm)
        {
            ResumeTab(vm, runAutoResumeCommand: false);
        }
    }

    // Kill the shell process, then treat the tab exactly like a freshly restored one: with an AUTORESUME.cmd present, either auto-resume it (setting on) or show the resume confirmation; without one, just start a new shell — same directory, full history via log replay.
    private void RestartShell(TabVm vm)
    {
        // Show progress the instant it's clicked.
        SetTabRestarting(vm, true);
        var old = vm.Session;
        var shellId = vm.Info.Id;
        bool wasElevated = vm.Elevated;
        vm.Session = null;
        vm.Dead = false;
        vm.Running = false;
        RefreshTabTitle(vm);
        Task.Run(() =>
        {
            // Kill as hard as possible and WAIT until the host process is actually gone before spawning the replacement — a lingering old host would fight the new one for the pipe name, which is what made restart flaky. Off the UI thread since the teardown can stall.
            if (wasElevated)
            {
                // An unelevated renderer can't force-kill an elevated process; ask the host to terminate itself instead.
                old?.ShutdownHost();
            }
            else
            {
                old?.KillHost();
                HostInfo.Kill(shellId);
            }
            var deadline = Environment.TickCount64 + HostDeathWaitMs;
            while (HostInfo.IsShellAlive(shellId) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!_tabs.Contains(vm))
                {
                    return;
                }
                // Always start a fresh shell (never fall into the resume-prompt); re-run AUTORESUME.cmd if the tab has one.
                StartSession(vm);
                RunAutoResumeCommand(vm);
                RefreshTabTitle(vm);
                if (vm == Active)
                {
                    UpdateResumeOverlay();
                    ApplyInputMode();
                    UpdateWindowTitle();
                }
                // Keep the progress icon a couple seconds longer so a fast restart is still visible.
                Task.Delay(RestartIconLingerMs).ContinueWith(_ => Dispatcher.BeginInvoke(() => SetTabRestarting(vm, false)));
            });
        });
    }

    private void SetTabRestarting(TabVm vm, bool restarting)
    {
        if (vm.RestartButton == null)
        {
            return;
        }
        vm.RestartButton.Text = restarting ? "⏳" : "↻";
        vm.RestartButton.Foreground = new SolidColorBrush(restarting ? Color.FromRgb(0xE0, 0xC0, 0x40) : Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void ResumeTab(TabVm vm, bool runAutoResumeCommand)
    {
        if (vm.Session != null)
        {
            return;
        }
        StartSession(vm);
        if (runAutoResumeCommand)
        {
            RunAutoResumeCommand(vm);
        }
        if (vm == Active)
        {
            UpdateResumeOverlay();
            ApplyInputMode();
            UpdateWindowTitle();
            FocusInput();
        }
    }

    // --ensure targeting: make the named shell run (fresh command via its AUTORESUME.cmd). If it's already running, this is a takeover — the old process dies first.
    private void WindowNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateWindowNamePlaceholder();
        if (_uiReady)
        {
            ScheduleSave();
        }
    }

    private bool _chromeHover;
    private void ChromePanel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _chromeHover = true;
        UpdateWindowNamePlaceholder();
    }
    private void ChromePanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _chromeHover = false;
        UpdateWindowNamePlaceholder();
    }
    private void WindowNameBox_FocusChanged(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        UpdateWindowNamePlaceholder();
    }
    // The window-name field collapses to nothing until named; the "name" hint (and thus a click target) is only shown while hovering the chrome or editing the field.
    private void UpdateWindowNamePlaceholder()
    {
        bool empty = string.IsNullOrEmpty(WindowNameBox.Text);
        WindowNamePlaceholder.Visibility = empty && (_chromeHover || WindowNameBox.IsKeyboardFocused) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureShellRuns(string shellId)
    {
        SyncTabsFromWindowFile();
        var vm = _tabs.FirstOrDefault(t => t.Info.Id == shellId);
        if (vm == null)
        {
            AppLog.Write("ui", $"ensure-shell: {shellId} not in this window");
            return;
        }
        // Focus follows the highest --order tab: lower-ordered ensures start their command in the background, so concurrent startup scripts always leave the highest-order tab focused (ties: either wins).
        if (_tabs.All(t => t == vm || t.Info.EnsureOrder <= vm.Info.EnsureOrder))
        {
            ActivateTab(_tabs.IndexOf(vm));
            ForceForeground();
        }
        if (vm.Session is { } old)
        {
            Task.Run(old.KillHost);
            vm.Session = null;
            vm.Dead = false;
            Task.Delay(ShellRestartDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (vm.Session == null && _tabs.Contains(vm))
                {
                    ResumeTab(vm, runAutoResumeCommand: true);
                }
            }));
        }
        else
        {
            vm.Dead = false;
            ResumeTab(vm, runAutoResumeCommand: true);
        }
    }

    // Runs the shell's AUTORESUME.cmd by sending its path; input is queued in the pty, so cmd executes it as soon as its prompt is ready.
    private void RunAutoResumeCommand(TabVm vm)
    {
        var autoResumePath = AppPaths.AutoResumeFile(vm.Info.Id);
        if (vm.Dead || !File.Exists(autoResumePath) || vm.Session is not { } session)
        {
            return;
        }
        string command = $"\"{autoResumePath}\"";
        session.SendCommandMarker(command);
        session.SendText(command + "\r");
        vm.LastCommand = command;
        vm.Running = true;
        RefreshTabTitle(vm);
        if (vm == Active)
        {
            UpdateWindowTitle();
            Term.ScrollToBottom();
        }
    }

    // Reads AUTORESUME.cmd off the UI thread and refreshes the cached value + dependent UI. External writers (the Claude hook, --ensure) are picked up on the periodic sync tick; local actions call this directly.
    private void RefreshAutoResumeState(TabVm vm)
    {
        var path = AppPaths.AutoResumeFile(vm.Info.Id);
        Task.Run(() =>
        {
            string content = "";
            try
            {
                if (File.Exists(path))
                {
                    content = File.ReadAllText(path).Trim();
                }
            }
            catch
            {
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!_tabs.Contains(vm) || vm.AutoResumeContent == content)
                {
                    return;
                }
                vm.AutoResumeContent = content;
                RefreshTabTitle(vm);
                if (vm == Active)
                {
                    UpdateResumeOverlay();
                }
            });
        });
    }

    private void UpdateResumeOverlay()
    {
        var vm = Active;
        bool show = vm is { Session: null };
        ResumeOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }
        // Cached (refreshed off-thread by RefreshAutoResumeState) so showing the overlay never blocks on disk.
        string content = vm!.AutoResumeContent ?? "";
        if (content.Length > 0)
        {
            AutoResumeText.Text = "On resume, AUTORESUME.cmd will run:\n" + content;
            AutoResumeText.Visibility = Visibility.Visible;
            ResumeNoRunBtn.Visibility = Visibility.Visible;
            ResumeBtn.Content = "Resume and run";
        }
        else
        {
            AutoResumeText.Visibility = Visibility.Collapsed;
            ResumeNoRunBtn.Visibility = Visibility.Collapsed;
            ResumeBtn.Content = "Resume shell";
        }
    }

    // Used by the settings window's shell list and by cross-window tab drops. Works for both live shells (connects to the running host) and dead ones (ShellSession spawns a fresh host in the saved cwd and the log replay restores the scrollback).
    public void AddShellTab(string shellId, int insertIndex = -1)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Info.Id == shellId)
            {
                ActivateTab(i);
                return;
            }
        }
        var info = ShellInfo.Load(shellId) ?? new ShellInfo();
        CreateTab(new TabInfo { Id = shellId, Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle }, activate: true, insertIndex: insertIndex);
    }

    private const double ResourceCellWidth = 56;
    // Index order everywhere in the row: VRAM, CPU, memory.
    private static readonly Color[] ResourceBarColors = { Color.FromRgb(0x2E, 0x5B, 0x3A), Color.FromRgb(0x6B, 0x4A, 0x2E), Color.FromRgb(0x2E, 0x4A, 0x6B) };
    private static readonly string[] ResourceTips = { "V: dedicated GPU memory", "C: CPU, averaged over the last minute", "M: memory (working set of the tab's process tree)" };

    // One mini bar-gauge per metric: the text sits over a rectangle whose width is the tab's share of the window's per-metric maximum, so the heaviest tab reads at a glance.
    private void BuildResourceRow(TabVm vm)
    {
        vm.ResourceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3), Visibility = _settings.ShowTabResources ? Visibility.Visible : Visibility.Collapsed };
        vm.ResourceTexts = new TextBlock[3];
        vm.ResourceBars = new System.Windows.Shapes.Rectangle[3];
        for (int i = 0; i < 3; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(ResourceBarColors[i]),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            var label = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 10,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Grid { Width = ResourceCellWidth, Height = 13, Margin = new Thickness(0, 0, 2, 0), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), ToolTip = ResourceTips[i] };
            cell.Children.Add(bar);
            cell.Children.Add(label);
            vm.ResourceBars[i] = bar;
            vm.ResourceTexts[i] = label;
            vm.ResourceRow.Children.Add(cell);
        }
    }

    private void UpdateResourceRows()
    {
        _resources.SetShells(_tabs.Select(t => t.Info.Id));
        if (!_settings.ShowTabResources || _tabs.Count == 0)
        {
            return;
        }
        var usages = _tabs.Select(t => _resources.Get(t.Info.Id)).ToList();
        double maxMem = Math.Max(1, usages.Max(u => (double?)(u?.MemoryBytes ?? 0) ?? 0));
        double maxGpu = Math.Max(1, usages.Max(u => (double?)(u?.GpuBytes ?? 0) ?? 0));
        double maxCpu = Math.Max(1, usages.Max(u => u?.CpuPercent ?? 0));
        for (int i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];
            var usage = usages[i];
            if (usage == null)
            {
                vm.ResourceTexts[0].Text = vm.ResourceTexts[1].Text = vm.ResourceTexts[2].Text = "—";
                vm.ResourceBars[0].Width = vm.ResourceBars[1].Width = vm.ResourceBars[2].Width = 0;
                continue;
            }
            vm.ResourceTexts[0].Text = "V " + FmtBytes(usage.GpuBytes);
            vm.ResourceTexts[1].Text = $"C {usage.CpuPercent:0}%";
            vm.ResourceTexts[2].Text = "M " + FmtBytes(usage.MemoryBytes);
            vm.ResourceBars[0].Width = usage.GpuBytes / maxGpu * ResourceCellWidth;
            vm.ResourceBars[1].Width = usage.CpuPercent / maxCpu * ResourceCellWidth;
            vm.ResourceBars[2].Width = usage.MemoryBytes / maxMem * ResourceCellWidth;
        }
    }

    private static string FmtBytes(long bytes)
    {
        if (bytes >= 1_000_000_000)
        {
            return $"{bytes / 1_000_000_000.0:0.0}G";
        }
        return $"{bytes / 1_000_000.0:0}M";
    }

    private void BuildTabHeader(TabVm vm)
    {
        var text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            MinWidth = 100,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                EndTitleEdit(vm, commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndTitleEdit(vm, commit: false);
            }
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (editor.Visibility == Visibility.Visible)
            {
                EndTitleEdit(vm, commit: true);
            }
        };
        vm.TitleEditor = editor;
        var revertTitle = new TextBlock
        {
            Text = "⟲",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "Revert to the automatic title (folder + command)",
        };
        revertTitle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            vm.Info.ForcedTitle = "";
            vm.CustomTitle = "";
            ScheduleSave();
            EndTitleEdit(vm, commit: false);
        };
        vm.RevertTitleButton = revertTitle;
        // Indicator only: the shell's host is running elevated (administrator).
        var adminBadge = new TextBlock
        {
            Text = "🛡",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x40)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Running as administrator",
        };
        vm.AdminBadge = adminBadge;
        // Indicator only: the shell has enabled win32-input-mode (an icon rather than title text — titles are too cramped for it).
        var win32Badge = new TextBlock
        {
            Text = "⌨",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xDC)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "win32-input-mode — full keyboard fidelity (Ctrl+Enter, Shift+Enter, Alt chords)",
        };
        vm.Win32Badge = win32Badge;
        // Indicator only — shows that this shell will auto-resume via AUTORESUME.cmd (tooltip shows the command); deliberately not clickable.
        var autoRun = new TextBlock
        {
            Text = "⏻",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xC9, 0x6F)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        vm.AutoResumeButton = autoRun;
        var refresh = new TextBlock
        {
            Text = "↻",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Restart shell — kills it and starts a new one in the same directory (scrollback is kept)",
        };
        vm.RestartButton = refresh;
        refresh.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            RestartShell(vm);
        };
        var close = new TextBlock
        {
            Text = "✕",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseTab(vm);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(text);
        panel.Children.Add(editor);
        panel.Children.Add(revertTitle);
        panel.Children.Add(adminBadge);
        panel.Children.Add(win32Badge);
        panel.Children.Add(autoRun);
        panel.Children.Add(refresh);
        panel.Children.Add(close);
        BuildResourceRow(vm);
        var headerStack = new StackPanel();
        headerStack.Children.Add(vm.ResourceRow);
        headerStack.Children.Add(panel);
        var border = new Border
        {
            Child = headerStack,
            Padding = new Thickness(12, 2, 10, 2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                _dragCandidate = null;
                StartTitleEdit(vm);
                return;
            }
            int index = _tabs.IndexOf(vm);
            if (index >= 0)
            {
                ActivateTab(index);
            }
            _dragCandidate = vm;
            _dragStart = e.GetPosition(this);
        };
        border.MouseMove += (_, e) =>
        {
            if (_draggingVm == vm && e.LeftButton == MouseButtonState.Pressed)
            {
                OnTabDragMove(e);
                return;
            }
            if (_dragCandidate != vm || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) > DragStartThresholdPx || Math.Abs(pos.Y - _dragStart.Y) > DragStartThresholdPx)
            {
                _dragCandidate = null;
                BeginTabDrag(vm, border);
            }
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            _dragCandidate = null;
            if (_draggingVm == vm)
            {
                e.Handled = true;
                EndTabDrag(vm, border, e);
            }
        };
        border.LostMouseCapture += (_, _) =>
        {
            if (_draggingVm == vm)
            {
                CancelTabDrag(border);
            }
        };
        border.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseTab(vm);
            }
        };
        border.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowTabContextMenu(vm, border);
        };
        vm.Header = border;
        vm.HeaderText = text;
    }

    // Tab dragging is entirely ours — mouse capture plus a ghost window, no OLE drag-drop, so nothing termiot-specific ever reaches other applications. Drop resolution: our own strip → reorder; another termiot window (found via WindowFromPoint + process identity) → adoption via WM_COPYDATA; anywhere else → a new window process around the shell. Esc cancels.
    private void BeginTabDrag(TabVm vm, Border border)
    {
        _draggingVm = vm;
        _dragGhost = new DragGhost(vm.HeaderText.Inlines.OfType<Run>().FirstOrDefault()?.Text ?? vm.Info.Id);
        _dragGhost.MoveToCursor(VisualTreeHelper.GetDpi(this).DpiScaleX);
        _dragGhost.Show();
        border.CaptureMouse();
    }

    private void OnTabDragMove(MouseEventArgs e)
    {
        _dragGhost?.MoveToCursor(VisualTreeHelper.GetDpi(this).DpiScaleX);
        var pos = e.GetPosition(TabBar);
        if (pos.X >= 0 && pos.X <= TabBar.ActualWidth && pos.Y >= 0 && pos.Y <= TabBar.ActualHeight)
        {
            ShowDropIndicator(TabInsertIndexAt(pos));
        }
        else
        {
            DropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelTabDrag(Border border)
    {
        _draggingVm = null;
        _dragGhost?.Close();
        _dragGhost = null;
        DropIndicator.Visibility = Visibility.Collapsed;
        if (border.IsMouseCaptured)
        {
            border.ReleaseMouseCapture();
        }
    }

    private void EndTabDrag(TabVm vm, Border border, MouseEventArgs e)
    {
        var stripPos = e.GetPosition(TabBar);
        bool overOwnStrip = stripPos.X >= 0 && stripPos.X <= TabBar.ActualWidth && stripPos.Y >= 0 && stripPos.Y <= TabBar.ActualHeight;
        int stripIndex = TabInsertIndexAt(stripPos);
        CancelTabDrag(border);
        GetCursorPos(out var cursor);
        var rootUnderCursor = GetAncestor(WindowFromPoint(cursor), GA_ROOT);
        var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (rootUnderCursor == selfHwnd)
        {
            if (!overOwnStrip)
            {
                return;
            }
            int current = _tabs.IndexOf(vm);
            int target = stripIndex;
            if (target > current)
            {
                target--;
            }
            target = Math.Clamp(target, 0, _tabs.Count - 1);
            if (target != current && current >= 0)
            {
                _tabs.RemoveAt(current);
                _tabs.Insert(target, vm);
                ActivateTab(target);
                SaveState();
            }
            return;
        }
        if (rootUnderCursor != IntPtr.Zero && IsOtherTermiotWindow(rootUnderCursor))
        {
            if (SendAdoptMessage(rootUnderCursor, selfHwnd, vm.Info.Id, cursor))
            {
                AppLog.Write("ui", $"drag: shell {vm.Info.Id} adopted by another window — detaching");
                RemoveTabForMove(vm);
            }
            else
            {
                AppLog.Write("ui", $"drag: adoption message rejected for shell {vm.Info.Id}");
            }
            return;
        }
        SpawnWindowAroundShell(vm);
    }

    private static bool IsOtherTermiotWindow(IntPtr root)
    {
        try
        {
            GetWindowThreadProcessId(root, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId)
            {
                return false;
            }
            using var process = Process.GetProcessById((int)pid);
            using var self = Process.GetCurrentProcess();
            return string.Equals(process.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SendAdoptMessage(IntPtr target, IntPtr source, string shellId, POINT cursor)
    {
        var payload = $"{shellId}|{cursor.X}|{cursor.Y}";
        var bytes = Encoding.Unicode.GetBytes(payload);
        var mem = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            var cds = new COPYDATASTRUCT { dwData = AdoptMessageId, cbData = bytes.Length, lpData = mem };
            return SendMessage(target, WM_COPYDATA, source, ref cds) != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_COPYDATA)
        {
            return IntPtr.Zero;
        }
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        if (cds.dwData == LastActiveWindow.OpenTabMessageId && cds.lpData != IntPtr.Zero)
        {
            var cwd = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? "";
            handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                CreateTab(NewTabInfo(cwd), activate: true, insertAfterActive: true);
                ForceForeground();
            });
            return (IntPtr)1;
        }
        if (cds.dwData == LastActiveWindow.EnsureShellMessageId && cds.lpData != IntPtr.Zero)
        {
            var ensureShellId = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? "";
            handled = true;
            Dispatcher.BeginInvoke(() => EnsureShellRuns(ensureShellId));
            return (IntPtr)1;
        }
        if (cds.dwData != AdoptMessageId || cds.lpData == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var payload = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? "";
        var parts = payload.Split('|');
        if (parts.Length < 3 || !int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y))
        {
            return IntPtr.Zero;
        }
        handled = true;
        string shellId = parts[0];
        int insertIndex = -1;
        try
        {
            var local = TabBar.PointFromScreen(new Point(x, y));
            if (local.X >= 0 && local.X <= TabBar.ActualWidth && local.Y >= 0 && local.Y <= TabBar.ActualHeight)
            {
                insertIndex = TabInsertIndexAt(local);
            }
        }
        catch
        {
        }
        // Delayed so the source window's Detach lands before we dial the shell's pipe.
        Task.Delay(DragHandoffDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(() => AddShellTab(shellId, insertIndex)));
        return (IntPtr)1;
    }

    private void SpawnWindowAroundShell(TabVm vm)
    {
        GetCursorPos(out var cursor);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var state = new Termiot.WindowState
        {
            Shells = new List<string> { vm.Info.Id },
            X = cursor.X / scale - 80,
            Y = cursor.Y / scale - 15,
            Width = ActualWidth,
            Height = ActualHeight,
        };
        string newWindowId = Program.NewId();
        state.Save(newWindowId);
        AppLog.Write("ui", $"drag-out: creating window {newWindowId} around shell {vm.Info.Id} at {state.X:0},{state.Y:0}");
        // Spawn before detaching: the new window's claim and tab load only depend on the just-written file, and this ordering can't be broken by the source window closing itself when this was its last tab.
        Program.SpawnWindowProcess(newWindowId);
        RemoveTabForMove(vm);
    }

    private void RemoveTabForMove(TabVm vm)
    {
        vm.Session?.Detach();
        int index = _tabs.IndexOf(vm);
        _tabs.Remove(vm);
        SaveState();
        if (_tabs.Count == 0)
        {
            _closedBecauseEmpty = true;
            Close();
            return;
        }
        ActivateTab(Math.Clamp(index, 0, _tabs.Count - 1));
    }

    // A green line drawn on an overlay canvas between the tabs — a pure preview; nothing in the strip moves until the drop. The canvas overlays the whole (possibly multi-row) tab bar, so the indicator lands at the edge of the target header wherever its row is.
    private void ShowDropIndicator(int index)
    {
        if (_tabs.Count == 0)
        {
            DropIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        var header = _tabs[Math.Min(index, _tabs.Count - 1)].Header;
        try
        {
            var topLeft = header.TranslatePoint(new Point(0, 0), TabBar);
            double x = index < _tabs.Count ? topLeft.X : topLeft.X + header.ActualWidth;
            Canvas.SetLeft(DropIndicator, Math.Max(0, x - 1.5));
            Canvas.SetTop(DropIndicator, topLeft.Y);
            DropIndicator.Height = Math.Max(10, header.ActualHeight);
            DropIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            DropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    // Insert position from a point in TabBar coordinates: the row is chosen by Y, the slot within it by X against each header's center. A point past a row's last header inserts after it; a point below every row appends.
    private int TabInsertIndexAt(Point point)
    {
        int afterRowLast = -1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var header = _tabs[i].Header;
            if (header.Parent == null)
            {
                continue;
            }
            Point topLeft;
            try
            {
                topLeft = header.TranslatePoint(new Point(0, 0), TabBar);
            }
            catch
            {
                continue;
            }
            if (point.Y < topLeft.Y || point.Y > topLeft.Y + header.ActualHeight)
            {
                continue;
            }
            if (point.X < topLeft.X + header.ActualWidth / 2)
            {
                return i;
            }
            afterRowLast = i + 1;
        }
        return afterRowLast >= 0 ? afterRowLast : _tabs.Count;
    }

    private void RefreshTabHeaders()
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];
            // Dark theme: the selected tab goes DARKER (pure black, merging with the terminal below), not lighter; inactive tabs alternate two shades so their boundaries are visible.
            vm.Header.Background = vm == Active ? Brushes.Black : (i % 2 == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)));
        }
        RelayoutTabRows();
    }

    // Multi-row tab strip. Row 0 shares the top bar with the window controls; further full-width rows are created on demand — as many as the tabs need, nothing scrolls.
    // A header may sit up to this many px wider than its content before it recompacts — absorbs small title fluctuations without reflowing the rows.
    private const double StickyHeaderSlackPx = 30;
    // Recompactions closer together than this count as rapid; StickyPinThreshold of them pins the header to its recent-larger width for StickyPinDurationMs so a jittery title stops thrashing the layout.
    private const long StickyRapidWindowMs = 2500;
    private const int StickyPinThreshold = 5;
    private const long StickyPinDurationMs = 20_000;
    // Long enough for a paste-detection window to close, short enough to feel instant.
    private const int Win32EnterDelayMs = 50;
    private readonly List<StackPanel> _extraRowPanels = new();
    private Button _inlinePlus = null!;

    // Measure one header's natural width and fold it through the sticky/pin logic into vm.StickyHeaderWidth. Called while the header is still attached to the tree so Measure actually re-runs; the title width also comes from a direct FormattedText measurement because a TextBlock hands back a stale width after its text shrinks.
    private void ComputeHeaderWidth(TabVm vm, long nowTicks)
    {
        var header = vm.Header;
        var titleBlock = vm.HeaderText;
        string titleStr = new System.Windows.Documents.TextRange(titleBlock.ContentStart, titleBlock.ContentEnd).Text;
        double titleW = 0;
        if (titleStr.Length > 0)
        {
            var typeface = new Typeface(titleBlock.FontFamily, titleBlock.FontStyle, titleBlock.FontWeight, titleBlock.FontStretch);
            var ft = new FormattedText(titleStr, System.Globalization.CultureInfo.CurrentUICulture, titleBlock.FlowDirection, typeface, titleBlock.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            titleW = Math.Ceiling(Math.Min(titleBlock.MaxWidth, ft.WidthIncludingTrailingWhitespace));
        }
        titleBlock.Width = titleW;
        header.MinWidth = 0;
        header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = Math.Ceiling(header.DesiredSize.Width);
        // Width stickiness: grow to fit immediately, and stay compact — hold at most StickyHeaderSlackPx of extra width so tiny title changes don't reflow, but recompact once the content shrinks past that. A title whose width keeps jumping gets pinned to its recent-larger width so it stops thrashing the rows.
        if (vm.StickyPinnedUntilTicks != 0 && nowTicks >= vm.StickyPinnedUntilTicks)
        {
            vm.StickyPinnedUntilTicks = 0;
            vm.StickyRapidChanges = 0;
        }
        double width;
        if (vm.StickyPinnedUntilTicks != 0)
        {
            // Pinned: hold the larger width, re-arming while the content keeps growing.
            if (natural > vm.StickyPinWidth)
            {
                vm.StickyPinWidth = natural;
                vm.StickyPinnedUntilTicks = nowTicks + StickyPinDurationMs;
            }
            width = vm.StickyPinWidth;
        }
        else if (natural >= vm.StickyHeaderWidth)
        {
            width = natural;
        }
        else if (vm.StickyHeaderWidth - natural > StickyHeaderSlackPx)
        {
            // Shrank past the slack — recompact, and watch for a jittery title that recompacts over and over.
            vm.StickyRapidChanges = nowTicks - vm.StickyLastRecompactTicks < StickyRapidWindowMs ? vm.StickyRapidChanges + 1 : 1;
            vm.StickyLastRecompactTicks = nowTicks;
            if (vm.StickyRapidChanges >= StickyPinThreshold)
            {
                vm.StickyPinWidth = vm.StickyHeaderWidth;
                vm.StickyPinnedUntilTicks = nowTicks + StickyPinDurationMs;
                vm.StickyRapidChanges = 0;
                width = vm.StickyPinWidth;
            }
            else
            {
                width = natural;
            }
        }
        else
        {
            width = vm.StickyHeaderWidth;
        }
        vm.StickyHeaderWidth = width;
        header.MinWidth = width;
        if (width > 220)
        {
            PerfLog.Record($"tab-size {vm.Info.Id}: nat={natural:0} w={width:0} titleW={titleW:0} attached={(VisualTreeHelper.GetParent(header) != null ? 1 : 0)}");
        }
    }

    private void RelayoutTabRows()
    {
        // Measure header widths while they are still attached from the last layout. A detached element never re-runs Measure — it hands back the last arranged width — so measuring after the panels are cleared returns stale sizes and tabs never shrink.
        if (!_settings.SingleRowTabs)
        {
            long measureTicks = Environment.TickCount64;
            foreach (var vm in _tabs)
            {
                ComputeHeaderWidth(vm, measureTicks);
            }
        }
        TabStrip.Children.Clear();
        foreach (var panel in _extraRowPanels)
        {
            panel.Children.Clear();
        }
        if (_inlinePlus.Parent is Panel plusParent)
        {
            plusParent.Children.Remove(_inlinePlus);
        }
        // The scroller stretches to fill the top bar's remaining slot, so its own width IS row 0's capacity — no estimating around the other controls.
        double capacity0 = TabScroller.ActualWidth > 0 ? TabScroller.ActualWidth - 2 : 0;
        // Single-row mode: everything lives in the top scroller and overflow scrolls, exactly the pre-wrap behavior.
        if (!_settings.SingleRowTabs)
        {
            TabScroller.ScrollToHorizontalOffset(0);
        }
        if (_settings.SingleRowTabs)
        {
            foreach (var vm in _tabs)
            {
                vm.Header.MinWidth = 0;
                vm.HeaderText.Width = double.NaN;
                TabStrip.Children.Add(vm.Header);
            }
            TabStrip.Children.Add(_inlinePlus);
            while (_extraRowPanels.Count > 0)
            {
                TabBarRows.Children.Remove(_extraRowPanels[^1]);
                _extraRowPanels.RemoveAt(_extraRowPanels.Count - 1);
            }
            return;
        }
        double capacityFull = Math.Max(100, TabBarRows.ActualWidth);
        Panel PanelFor(int r)
        {
            while (_extraRowPanels.Count < r)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                _extraRowPanels.Add(panel);
                TabBarRows.Children.Add(panel);
            }
            return r == 0 ? TabStrip : _extraRowPanels[r - 1];
        }
        double CapacityFor(int r) => r == 0 ? capacity0 : capacityFull;
        int row = 0;
        double used = 0;
        foreach (var vm in _tabs)
        {
            var header = vm.Header;
            double width = vm.StickyHeaderWidth;
            if (used + width > CapacityFor(row) && PanelFor(row).Children.Count > 0)
            {
                row++;
                used = 0;
            }
            PanelFor(row).Children.Add(header);
            used += width;
        }
        // The + trails the last tab, spilling to the next row only if it genuinely doesn't fit.
        if (used + _inlinePlus.Width > CapacityFor(row) && PanelFor(row).Children.Count > 0)
        {
            row++;
        }
        PanelFor(row).Children.Add(_inlinePlus);
        while (_extraRowPanels.Count > row)
        {
            TabBarRows.Children.Remove(_extraRowPanels[^1]);
            _extraRowPanels.RemoveAt(_extraRowPanels.Count - 1);
        }
    }

    private const double TabScrollStepPx = 160;

    private void ScrollLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - TabScrollStepPx);
    }

    private void ScrollRightBtn_Click(object sender, RoutedEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + TabScrollStepPx);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
    }

    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }
        // Remember the outgoing tab's scroll position and search so switching back restores them instead of jumping / carrying the query across.
        if (Active is { } outgoing)
        {
            outgoing.Info.ScrollFromBottom = Term.ScrollFromBottom;
            SaveSearchState(outgoing);
        }
        _active = index;
        var vm = _tabs[index];
        Term.ShowTermCursor = RawMode && !vm.Dead && vm.Session != null;
        Term.Attach(vm.Screen);
        Term.SetScrollFromBottom(vm.Info.ScrollFromBottom);
        // Switching to a tab loads its older history now rather than waiting for the delayed pass (its recent tail is already on screen from the file read).
        vm.Session?.Activate();
        CwdLabel.Text = vm.Info.Cwd;
        SetInputText(vm.PendingInput);
        // The tab strip scrolls; an activated tab must be visible.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => vm.HeaderText.BringIntoView());
        RefreshCandidates();
        UpdateResumeOverlay();
        UpdateLoadingOverlay();
        UpdateWindowTitle();
        UpdateScrollStats();
        RefreshTabHeaders();
        RestoreSearchState(vm);
        FocusInput();
        ScheduleSave();
    }

    // Closing a tab terminates the shell process, but its folder stays on disk — the settings window lists it and can resurrect it.
    private void CloseTab(TabVm vm)
    {
        int index = _tabs.IndexOf(vm);
        if (index < 0)
        {
            return;
        }
        if (vm.Session is { } old)
        {
            Task.Run(old.KillHost);
        }
        _tabs.RemoveAt(index);
        SaveState();
        if (_tabs.Count == 0)
        {
            _closedBecauseEmpty = true;
            Close();
            return;
        }
        ActivateTab(Math.Clamp(index, 0, _tabs.Count - 1));
    }

    private void RevealReplay(TabVm vm)
    {
        if (!vm.Replaying)
        {
            return;
        }
        vm.Replaying = false;
        if (vm == Active)
        {
            Term.RenderFrame();
        }
        UpdateLoadingOverlay();
    }

    private void UpdateLoadingOverlay()
    {
        LoadingOverlay.Visibility = Active is { Replaying: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPendingTitle(TabVm vm)
    {
        var title = (vm.PendingTitle ?? "").Trim();
        // cmd constantly re-announces its own path as the title; only deliberate titles (e.g. the `title` command) count as custom.
        vm.CustomTitle = title.Length == 0 || title.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ? "" : title;
        RefreshTabTitle(vm);
        if (vm == Active)
        {
            UpdateWindowTitle();
        }
    }

    private void RenderDirtyTabs()
    {
        foreach (var vm in _tabs)
        {
            // Reveal a replaying tab once its burst of replay output has gone quiet (finished catching up), or a hard cap elapses.
            if (vm.Replaying)
            {
                long now = Environment.TickCount64;
                long sinceOutput = vm.LastOutputTicks != 0 ? now - vm.LastOutputTicks : now - vm.ReplayStartTicks;
                if (sinceOutput > RevealIdleMs || now - vm.ReplayStartTicks > ReplayRevealTimeoutMs)
                {
                    RevealReplay(vm);
                }
            }
            // Apply the latest title once per tick regardless of output, coalescing any burst of title changes into a single header relayout.
            if (Interlocked.Exchange(ref vm.TitleDirty, 0) == 1)
            {
                ApplyPendingTitle(vm);
            }
            if (Interlocked.Exchange(ref vm.Dirty, 0) == 0)
            {
                continue;
            }
            UpdateCwd(vm);
            if (vm == Active && !vm.Replaying)
            {
                Term.RenderFrame();
                // New output may add matches; re-scan (throttled) so search stays live instead of frozen at the term's first scan.
                if (SearchBar.Visibility == Visibility.Visible)
                {
                    UpdateSearchForNewOutput();
                }
            }
            if (!_tracedFirstRender)
            {
                _tracedFirstRender = true;
                // The moment content is actually on screen; gap from first-output-frame is parse + render. titles= flags a title-dispatch flood if that's the stall instead.
                StartupTrace.Mark($"first-tab-render(titles={_titleDispatches})");
                StartupTrace.Flush();
            }
            if (!vm.LoggedFirstRender)
            {
                vm.LoggedFirstRender = true;
                lock (vm.Screen.Sync)
                {
                    AppLog.Write("render", $"{vm.Info.Id}: first dirty render, active={vm == Active}, totalLines={vm.Screen.TotalLines}, cols={vm.Screen.Cols}");
                }
            }
        }
    }

    private void UpdateCwd(TabVm vm)
    {
        string? cwd = null;
        bool promptVisible = false;
        lock (vm.Screen.Sync)
        {
            for (int y = vm.Screen.Rows - 1; y >= 0; y--)
            {
                var text = vm.Screen.GetLine(vm.Screen.ScrollbackCount + y).GetText();
                if (text.Length == 0)
                {
                    continue;
                }
                var match = PromptRegex.Match(text);
                if (match.Success)
                {
                    promptVisible = true;
                    cwd = match.Groups[1].Value;
                }
                break;
            }
        }
        bool changed = false;
        if (cwd != null && cwd != vm.Info.Cwd)
        {
            vm.Info.Cwd = cwd;
            if (vm == Active)
            {
                CwdLabel.Text = cwd;
            }
            ScheduleSave();
            changed = true;
        }
        // An idle shell shows its prompt as the last line; anything else after a command was sent means that command is still running.
        bool running = !promptVisible && vm.LastCommand.Length > 0 && !vm.Dead && vm.Session != null;
        if (running != vm.Running)
        {
            vm.Running = running;
            changed = true;
        }
        if (changed)
        {
            RefreshTabTitle(vm);
            if (vm == Active)
            {
                UpdateWindowTitle();
            }
        }
    }

    private void ShowTabContextMenu(TabVm vm, Border border)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            PlacementTarget = border,
        };
        var escapes = new MenuItem
        {
            Header = "Render escape sequences literally",
            IsCheckable = true,
            IsChecked = vm.Parser.ShowEscapes,
        };
        escapes.Click += (_, _) =>
        {
            vm.Parser.ShowEscapes = escapes.IsChecked;
            RefreshTabTitle(vm);
            if (vm == Active)
            {
                UpdateWindowTitle();
            }
        };
        var rename = new MenuItem { Header = "Rename tab…" };
        rename.Click += (_, _) => StartTitleEdit(vm);
        var admin = new MenuItem
        {
            Header = "Run as administrator",
            IsCheckable = true,
            IsChecked = vm.Info.Elevated,
        };
        admin.Click += (_, _) => SetElevated(vm, admin.IsChecked);
        var clearHistory = new MenuItem { Header = "Clear command history" };
        clearHistory.Click += (_, _) =>
        {
            vm.History.Clear();
            if (vm == Active)
            {
                _histIndex = -1;
                RefreshCandidates();
            }
        };
        menu.Items.Add(escapes);
        menu.Items.Add(rename);
        menu.Items.Add(admin);
        menu.Items.Add(clearHistory);
        menu.IsOpen = true;
    }

    // Toggle the sticky elevated preference and restart the shell so the new host is (or isn't) elevated. Elevating prompts UAC; the renderer stays unelevated.
    private void SetElevated(TabVm vm, bool elevated)
    {
        vm.Info.Elevated = elevated;
        ShellInfo.Save(vm.Info);
        RestartShell(vm);
    }

    private void StartTitleEdit(TabVm vm)
    {
        vm.TitleEditor.Text = vm.Info.ForcedTitle.Length > 0 ? vm.Info.ForcedTitle : (vm.CustomTitle.Length > 0 ? vm.CustomTitle : LeafDir(vm.Info.Cwd));
        vm.HeaderText.Visibility = Visibility.Collapsed;
        vm.TitleEditor.Visibility = Visibility.Visible;
        vm.RevertTitleButton.Visibility = Visibility.Visible;
        _activeTitleEditor = vm.TitleEditor;
        vm.TitleEditor.Focus();
        vm.TitleEditor.SelectAll();
    }

    // Committing an empty title clears the forced title, same as the revert button.
    private void EndTitleEdit(TabVm vm, bool commit)
    {
        vm.TitleEditor.Visibility = Visibility.Collapsed;
        vm.HeaderText.Visibility = Visibility.Visible;
        vm.RevertTitleButton.Visibility = Visibility.Collapsed;
        _activeTitleEditor = null;
        if (commit)
        {
            vm.Info.ForcedTitle = vm.TitleEditor.Text.Trim();
            ScheduleSave();
        }
        RefreshTabTitle(vm);
        if (vm == Active)
        {
            UpdateWindowTitle();
        }
        FocusInput();
    }

    private static string TitleFlags(TabVm vm)
    {
        return vm.Parser.ShowEscapes ? "[esc] " : "";
    }

    private void RefreshTabTitle(TabVm vm)
    {
        vm.Win32Badge.Visibility = vm.Win32Input ? Visibility.Visible : Visibility.Collapsed;
        vm.AdminBadge.Visibility = vm.Elevated ? Visibility.Visible : Visibility.Collapsed;
        var text = vm.HeaderText;
        text.Inlines.Clear();
        var flags = TitleFlags(vm);
        if (flags.Length > 0)
        {
            text.Inlines.Add(new Run(flags) { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)) });
        }
        if (vm.Info.ForcedTitle.Length > 0)
        {
            text.Inlines.Add(new Run(vm.Info.ForcedTitle));
        }
        else if (vm.CustomTitle.Length > 0)
        {
            text.Inlines.Add(new Run(vm.CustomTitle));
        }
        else
        {
            string leaf = LeafDir(vm.Info.Cwd);
            text.Inlines.Add(new Run(leaf.Length > 0 ? leaf : vm.Info.Title));
            if (vm.LastCommand.Length > 0)
            {
                text.Inlines.Add(new Run("  " + vm.LastCommand)
                {
                    Foreground = new SolidColorBrush(vm.Running ? Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC) : Color.FromArgb(0x80, 0xCC, 0xCC, 0xCC)),
                });
            }
        }
        // Cached (refreshed off-thread by RefreshAutoResumeState) — this runs on every title change, so it must never touch disk.
        if (vm.AutoResumeContent is { Length: > 0 } autoResume)
        {
            vm.AutoResumeButton.Visibility = Visibility.Visible;
            vm.AutoResumeButton.ToolTip = autoResume;
        }
        else
        {
            vm.AutoResumeButton.Visibility = Visibility.Collapsed;
        }
        // Title text changes header width, which can change which row each tab fits on.
        RelayoutTabRows();
    }

    // Scrollback size of the current tab, shown next to the LLM stats.
    private void UpdateScrollStats()
    {
        if (Active is not { } vm)
        {
            ScrollStatsText.Text = "";
            return;
        }
        int lines;
        long chars;
        lock (vm.Screen.Sync)
        {
            lines = vm.Screen.TotalLines;
            chars = vm.Screen.TotalChars();
        }
        ScrollStatsText.Text = $"{FormatCount(lines)} lines | {FormatSize(chars)}";
    }

    private static string FormatCount(long n)
    {
        if (n >= 1_000_000)
        {
            return $"{n / 1_000_000.0:0.#}M";
        }
        if (n >= 1_000)
        {
            return $"{n / 1_000.0:0.#}K";
        }
        return n.ToString();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0}MB";
        }
        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0}KB";
        }
        return $"{bytes}B";
    }

    private void UpdateWindowTitle()
    {
        var vm = Active;
        string flags = vm != null ? TitleFlags(vm) : "";
        if (vm?.Info.ForcedTitle is { Length: > 0 } forced)
        {
            Title = flags + forced;
            return;
        }
        if (vm?.CustomTitle is { Length: > 0 } custom)
        {
            Title = flags + custom;
            return;
        }
        if (vm == null || vm.Info.Cwd.Length == 0)
        {
            Title = flags + "Termiot";
            return;
        }
        if (vm.LastCommand.Length == 0)
        {
            Title = flags + vm.Info.Cwd;
        }
        else if (vm.Running)
        {
            Title = $"{flags}{vm.Info.Cwd} — {vm.LastCommand}";
        }
        else
        {
            Title = $"{flags}{vm.Info.Cwd} — ({vm.LastCommand})";
        }
    }

    private static string LeafDir(string cwd)
    {
        if (cwd.Length == 0)
        {
            return "";
        }
        var leaf = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return leaf.Length > 0 ? leaf : cwd;
    }

    private void OnCellSizeChanged(int cols, int rows)
    {
        var vm = Active;
        if (vm == null)
        {
            return;
        }
        lock (vm.Screen.Sync)
        {
            vm.Screen.Resize(cols, rows);
        }
        vm.Session?.Resize(cols, rows);
        Term.RenderFrame();
    }

    private static double? Finite(double v)
    {
        return double.IsFinite(v) ? v : null;
    }

    // Reads the window file and each listed shell's state — pure disk reads, safe to run off the UI thread.
    private List<(TabInfo Info, bool Alive)> ReadWindowFileAdoptions()
    {
        var result = new List<(TabInfo, bool)>();
        try
        {
            var state = Termiot.WindowState.Load(_windowId);
            foreach (var id in state.Shells)
            {
                if (!Directory.Exists(AppPaths.ShellDir(id)))
                {
                    continue;
                }
                var info = ShellInfo.Load(id) ?? new ShellInfo();
                var tabInfo = new TabInfo { Id = id, Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = info.EnsureOrder, ExitedAtTicks = info.ExitedAtTicks };
                result.Add((tabInfo, HostInfo.IsShellAlive(id)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "window file sync read failed: " + ex.Message);
        }
        return result;
    }

    private void ApplyWindowFileAdoptions(List<(TabInfo Info, bool Alive)> adopt)
    {
        bool added = false;
        foreach (var (tabInfo, alive) in adopt)
        {
            if (_tabs.Any(t => t.Info.Id == tabInfo.Id))
            {
                continue;
            }
            // Ordered (--ensure --order) tabs land at their sorted position; unordered ones append.
            int insertAt = tabInfo.EnsureOrder != 0 ? _tabs.TakeWhile(t => t.Info.EnsureOrder <= tabInfo.EnsureOrder).Count() : -1;
            CreateTab(tabInfo, activate: false, start: alive, insertIndex: insertAt);
            added = true;
            AppLog.Write("ui", $"sync: adopted shell {tabInfo.Id} listed in window {_windowId}");
        }
        if (added)
        {
            SaveState();
        }
    }

    // Synchronous adopt: the --ensure path looks the shell up immediately afterwards, so it must already be present.
    private void SyncTabsFromWindowFile()
    {
        ApplyWindowFileAdoptions(ReadWindowFileAdoptions());
    }

    // Periodic sync: reads off the UI thread, then adopts and refreshes cached AUTORESUME state on it.
    private void SyncTabsFromWindowFileAsync()
    {
        Task.Run(() =>
        {
            var adopt = ReadWindowFileAdoptions();
            Dispatcher.BeginInvoke(() =>
            {
                ApplyWindowFileAdoptions(adopt);
                foreach (var tab in _tabs)
                {
                    RefreshAutoResumeState(tab);
                }
            });
        });
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState(bool closedCleanly = false, bool closing = false)
    {
        // The active tab's live scroll position lives on the shared control; capture it before persisting.
        if (Active is { } activeTab)
        {
            activeTab.Info.ScrollFromBottom = Term.ScrollFromBottom;
        }
        foreach (var tab in _tabs)
        {
            if (tab.Info.PendingInput != tab.PendingInput)
            {
                tab.Info.PendingInput = tab.PendingInput;
                ShellInfo.Save(tab.Info);
            }
        }
        var self = Process.GetCurrentProcess();
        new Termiot.WindowState
        {
            Name = WindowNameBox.Text.Trim(),
            Shells = _tabs.Select(t => t.Info.Id).ToList(),
            ActiveIndex = _active,
            X = Finite(Left),
            Y = Finite(Top),
            Width = Finite(ActualWidth),
            Height = Finite(ActualHeight),
            OwnerPid = self.Id,
            OwnerStartTicks = self.StartTime.Ticks,
            ClosedCleanly = closedCleanly,
            ClosedAtTicks = closing ? DateTime.UtcNow.Ticks : 0,
        }.Save(_windowId);
        foreach (var tab in _tabs)
        {
            ShellInfo.Save(tab.Info);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool Bound(string id) => Hotkeys.Matches(_settings, id, key, Keyboard.Modifiers);
        if (key == Key.Escape && _draggingVm is { } dragging)
        {
            e.Handled = true;
            CancelTabDrag(dragging.Header);
        }
        else if (Bound("new-tab"))
        {
            e.Handled = true;
            OpenNewTab();
        }
        else if (Bound("search"))
        {
            e.Handled = true;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
            // Reopening a tab whose box still holds a query should show its matches again, not wait for an edit.
            if (SearchBox.Text.Length > 0)
            {
                RecomputeSearch();
            }
        }
        else if (Bound("new-window"))
        {
            e.Handled = true;
            string newWindowId = Program.NewId();
            new Termiot.WindowState
            {
                X = Finite(Left + 40),
                Y = Finite(Top + 40),
                Width = Finite(ActualWidth),
                Height = Finite(ActualHeight),
            }.Save(newWindowId);
            Program.SpawnWindowProcess(newWindowId);
        }
        else if (Bound("close-tab"))
        {
            e.Handled = true;
            if (Active is { } vm)
            {
                CloseTab(vm);
            }
        }
        else if (Bound("next-tab") && _tabs.Count > 0)
        {
            e.Handled = true;
            ActivateTab((_active + 1) % _tabs.Count);
        }
        else if (Bound("prev-tab") && _tabs.Count > 0)
        {
            e.Handled = true;
            ActivateTab((_active - 1 + _tabs.Count) % _tabs.Count);
        }
        else if (Bound("restart-shell"))
        {
            e.Handled = true;
            if (Active is { } restartVm)
            {
                RestartShell(restartVm);
            }
        }
        else if ((key == Key.PageUp || key == Key.PageDown) && Active is { Screen: { } pgScreen } && !pgScreen.OnAltScreen)
        {
            // Scroll the scrollback; a full-screen app (alt-screen) gets the keys itself instead.
            e.Handled = true;
            Term.ScrollPage(key == Key.PageUp);
        }
        else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Term.HasSelection)
        {
            // Enter with a selection copies it (like Ctrl+C) instead of sending anything — swallowed at window level so it works in editor and raw mode alike.
            e.Handled = true;
            try
            {
                Clipboard.SetText(Term.GetSelectedText());
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "clipboard copy failed: " + ex.Message);
            }
            Term.ClearSelection();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.C && InputBox.IsKeyboardFocused && InputBox.SelectionLength > 0)
        {
            // Text selected in the input box is what the user wants copied — do that instead of falling through to the clear-input step.
            e.Handled = true;
            try
            {
                Clipboard.SetText(InputBox.SelectedText);
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "clipboard copy failed: " + ex.Message);
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.C && Term.HasSelection)
        {
            // Ctrl+C escalates: copy the selection if there is one, else clear pending input, else interrupt the shell (falls through below). Clearing the selection after copying makes the next Ctrl+C move down the ladder.
            e.Handled = true;
            try
            {
                Clipboard.SetText(Term.GetSelectedText());
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "clipboard copy failed: " + ex.Message);
            }
            Term.ClearSelection();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.C && !RawMode && InputBox.Text.Length > 0)
        {
            e.Handled = true;
            SetInputText("");
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.V && !SearchBox.IsKeyboardFocused)
        {
            // Editor mode: leave unhandled so the input box's native paste runs. Raw mode: send the clipboard text to the shell.
            if (RawMode && Active is { Dead: false, Session: { } pasteSession } pasteVm)
            {
                e.Handled = true;
                try
                {
                    var text = Clipboard.GetText();
                    if (text.Length > 0)
                    {
                        if (pasteVm.Win32Input)
                        {
                            pasteSession.SendInput(InputEncoder.EncodeWin32Text(text));
                        }
                        else
                        {
                            pasteSession.SendText(text);
                        }
                        LogRawKeystroke(text);
                        Term.ScrollToBottom();
                        // Pasted content isn't "typed" — don't let it end up in history.
                        pasteVm.RawHistoryTainted = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("ui", "clipboard paste failed: " + ex.Message);
                }
            }
        }
        else if (!RawMode && !SearchBox.IsKeyboardFocused && ShouldForwardToShell(key, Keyboard.Modifiers) && !IsInputEditingChord(key, Keyboard.Modifiers) && Active is { Dead: false, Session: { } session } forwardVm)
        {
            var encoded = forwardVm.Win32Input ? InputEncoder.EncodeWin32Key(key, Keyboard.Modifiers) : InputEncoder.Encode(key, Keyboard.Modifiers);
            if (encoded != null)
            {
                e.Handled = true;
                session.SendInput(encoded);
                if (!IsModifierKey(key))
                {
                    Term.ScrollToBottom();
                }
            }
        }
    }

    // Standard text-editing chords stay with the focused input editor instead of being forwarded to the shell: word navigation (Ctrl+arrows/Home/End), select-all, cut, undo/redo, word delete.
    private bool IsInputEditingChord(Key key, ModifierKeys mods)
    {
        if (!InputBox.IsKeyboardFocused || (mods & ModifierKeys.Alt) != 0 || (mods & ModifierKeys.Control) == 0)
        {
            return false;
        }
        return key is Key.Left or Key.Right or Key.Home or Key.End or Key.A or Key.X or Key.Z or Key.Y or Key.Back or Key.Delete;
    }

    // Editor mode owns plain typing and its own editing keys; everything that is NOT regular typing (Ctrl/Alt chords like Ctrl+C, F-keys, Escape, paging) belongs to the underlying shell.
    private static bool ShouldForwardToShell(Key key, ModifierKeys mods)
    {
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return true;
        }
        return key is Key.Escape or Key.PageUp or Key.PageDown or Key.Insert or (>= Key.F1 and <= Key.F24);
    }

    // --- input bar (editor mode) ---

    private void FocusInput()
    {
        if (RawMode)
        {
            Term.Focus();
        }
        else
        {
            InputBox.Focus();
        }
    }

    // Raw keys mode sends keystrokes straight through — the cwd/LLM row and the suggestion panel are irrelevant there, so the whole apparatus disappears and no predictions run.
    private void ApplyInputMode()
    {
        bool raw = RawMode;
        EditorArea.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        RawKeysText.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        CwdRow.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        SuggestionPanel.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        Term.ShowTermCursor = raw && Active is { Dead: false, Session: not null };
        Term.RenderFrame();
    }

    // Raw mode has no input echo of its own, so show what was sent — newest first, control bytes in caret notation (Ctrl+C → ^C, Escape → ^[, so an arrow key reads ^[[A), so chords are verifiable at a glance.
    private void LogRawKeystroke(string sent)
    {
        var visualized = new StringBuilder(sent.Length);
        foreach (var c in sent)
        {
            if (c < ' ')
            {
                visualized.Append('^').Append((char)(c + 0x40));
            }
            else if (c == '\x7f')
            {
                visualized.Append("^?");
            }
            else
            {
                visualized.Append(c);
            }
        }
        _rawKeyLog = visualized + "  " + _rawKeyLog;
        if (_rawKeyLog.Length > RawKeyLogMaxChars)
        {
            _rawKeyLog = _rawKeyLog[..RawKeyLogMaxChars];
        }
        RawKeysText.Text = _rawKeyLog;
    }

    private void RawToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }
        _settings.RawInput = RawMode;
        _settings.Save();
        ApplyInputMode();
        FocusInput();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingInputText)
        {
            return;
        }
        _histIndex = -1;
        if (Active is { } vm)
        {
            vm.PendingInput = InputBox.Text;
            ScheduleSave();
        }
        AdjustInputHeight();
        RefreshCandidates();
    }

    // Recompute what the suggestion panel offers for the current input. LLM mode: keep showing the previous predictions until the (debounced) new ones land. Regular mode: candidates are cheap, compute synchronously.
    private void RefreshCandidates()
    {
        _candIndex = -1;
        _candWindowStart = 0;
        if (UseLlmFor(InputBox.Text))
        {
            ScheduleLlmPrediction();
        }
        else
        {
            // Regular candidates hit the filesystem — recompute debounced and off-thread so typing never blocks on directory enumeration.
            _candTimer.Stop();
            _candTimer.Start();
        }
        UpdateLlmUi();
    }

    private bool UseLlmFor(string text)
    {
        if (!_predictor.IsConfigured)
        {
            return false;
        }
        return _settings.LlmEnabled || (_settings.LlmTriggerEnabled && MatchLlmTrigger(text) != null);
    }

    // Trigger matching compares letters only, case-insensitively ("Hey, llm" == "heyllm"); returns the instruction text after the matched phrase, or null.
    private string? MatchLlmTrigger(string text)
    {
        foreach (var phrase in _settings.LlmTriggerPhrases.Split('|'))
        {
            var normPhrase = new string(phrase.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if (normPhrase.Length == 0)
            {
                continue;
            }
            int matched = 0;
            int position = 0;
            while (position < text.Length && matched < normPhrase.Length)
            {
                var c = text[position];
                if (char.IsLetter(c))
                {
                    if (char.ToLowerInvariant(c) != normPhrase[matched])
                    {
                        break;
                    }
                    matched++;
                }
                position++;
            }
            if (matched == normPhrase.Length)
            {
                return text[position..].TrimStart(' ', ',', ':', '.', '-');
            }
        }
        return null;
    }

    private void SetInputText(string text)
    {
        _settingInputText = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _settingInputText = false;
        AdjustInputHeight();
        // Any programmatic text change ends a history walk; the Up/Down handlers re-assign the index right after when the change IS the walk.
        _histIndex = -1;
        if (Active is { } vm)
        {
            vm.PendingInput = text;
        }
    }

    // Pin the input box to exactly the height its text wraps to. Left to auto-size, a WPF TextBox pads its desired height with a phantom trailing line for the caret at certain positions, so the box (and the whole input bar) grows and shrinks by a line as the caret moves — the jitter. LineCount reflects the wrapped text alone, independent of the caret, so a height derived from it stays put while the caret moves.
    private void AdjustInputHeight()
    {
        if (InputBox.ActualWidth <= 0)
        {
            return;
        }
        // LineCount is only valid once the text has been laid out at the current width.
        InputBox.UpdateLayout();
        int lines = Math.Max(1, InputBox.LineCount);
        double lineHeight = InputBox.FontFamily.LineSpacing * InputBox.FontSize;
        InputBox.Height = Math.Ceiling(lineHeight * lines);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            {
                e.Handled = true;
                // Plain Enter submits, Shift/Ctrl+Enter inserts a newline — or the reverse with the swap setting.
                bool modifiedEnter = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                if (_settings.SwapEnterSubmit ? !modifiedEnter : modifiedEnter)
                {
                    int caret = InputBox.CaretIndex;
                    InputBox.Text = InputBox.Text.Insert(caret, "\n");
                    InputBox.CaretIndex = caret + 1;
                    break;
                }
                var vm = Active;
                string text = InputBox.Text;
                if (vm is { Session: null })
                {
                    ResumeBtn_Click(sender, e);
                    break;
                }
                if (vm is { Dead: false, Session: { } session })
                {
                    if (text.Trim().Length > 0)
                    {
                        session.SendCommandMarker(text.Trim());
                    }
                    if (vm.Win32Input)
                    {
                        // TUI apps with paste detection (Claude) treat one burst ending in \r as a paste containing a newline, not a submit. Send the text, then a genuine Enter key record after a beat so it reads as its own keypress.
                        if (text.Length > 0)
                        {
                            session.SendInput(InputEncoder.EncodeWin32Text(text));
                        }
                        var enterRecord = InputEncoder.EncodeWin32Key(Key.Enter, ModifierKeys.None)!;
                        Task.Delay(Win32EnterDelayMs).ContinueWith(_ => session.SendInput(enterRecord));
                    }
                    else
                    {
                        session.SendText(text + "\r");
                    }
                    vm.History.Add(text);
                    if (text.Trim().Length > 0)
                    {
                        vm.LastCommand = text.Trim();
                        vm.Running = true;
                        RefreshTabTitle(vm);
                        UpdateWindowTitle();
                    }
                    SetInputText("");
                    Term.ScrollToBottom();
                    RefreshCandidates();
                }
                break;
            }
            case Key.Tab:
            {
                e.Handled = true;
                CycleCandidates((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            }
            // The input is the "center": Down goes into the autocomplete rows below, Up goes into history above (swapped straight into the input, never shown as rows). Any edit resets to center.
            case Key.Up:
            {
                e.Handled = true;
                if (_histIndex >= 0)
                {
                    if (_histIndex + 1 < _histEntries.Count)
                    {
                        int next = _histIndex + 1;
                        SetInputText(_histEntries[next]);
                        _histIndex = next;
                    }
                }
                else if (_candIndex >= 0)
                {
                    CycleCandidates(backwards: true);
                }
                else
                {
                    _histEntries = Active?.History.Match("") ?? new List<string>();
                    if (_histEntries.Count > 0)
                    {
                        var stash = InputBox.Text;
                        SetInputText(_histEntries[0]);
                        _histStash = stash;
                        _histIndex = 0;
                    }
                }
                break;
            }
            case Key.Down:
            {
                e.Handled = true;
                if (_histIndex >= 0)
                {
                    int next = _histIndex - 1;
                    SetInputText(next >= 0 ? _histEntries[next] : _histStash);
                    _histIndex = next;
                }
                else
                {
                    CycleCandidates(backwards: false);
                }
                break;
            }
        }
    }

    // Tab / Down move forward through the suggestion list, Shift+Tab / Up move back; the visible window of rows rolls along with the selection, and one step past the ends restores what the user had typed.
    private void CycleCandidates(bool backwards)
    {
        if (!UseLlmFor(InputBox.Text) && (_candidates.Count == 0 || _candText != InputBox.Text))
        {
            // The background result may not have landed (or is for older input) — compute synchronously for this deliberate Tab press.
            _candidates = BuildTabCandidates(InputBox.Text);
            _candText = InputBox.Text;
            _candWindowStart = 0;
        }
        if (_candidates.Count == 0)
        {
            return;
        }
        if (_candIndex == -1)
        {
            _candBase = InputBox.Text;
        }
        int slots = _candidates.Count + 1;
        int current = _candIndex == -1 ? _candidates.Count : _candIndex;
        current = ((current + (backwards ? -1 : 1)) % slots + slots) % slots;
        _candIndex = current == _candidates.Count ? -1 : current;
        if (_candIndex >= 0)
        {
            int rows = SuggestionRowCount();
            if (_candIndex < _candWindowStart)
            {
                _candWindowStart = _candIndex;
            }
            else if (_candIndex >= _candWindowStart + rows)
            {
                _candWindowStart = _candIndex - rows + 1;
            }
        }
        SetInputText(_candIndex == -1 ? _candBase : _candidates[_candIndex]);
        UpdateLlmUi();
    }

    private int SuggestionRowCount()
    {
        return _settings.LlmMultiComplete ? Math.Clamp(_settings.MultiCompleteCount, 1, 100) : 1;
    }

    // --- LLM prediction ---

    private void LlmToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }
        _settings.LlmEnabled = LlmToggle.IsChecked.GetValueOrDefault();
        _settings.Save();
        if (_settings.LlmEnabled && !_predictor.IsConfigured)
        {
            OpenSettings().SelectLlmTab();
        }
        ApplyLlmUi();
        RefreshCandidates();
    }

    private void MultiToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }
        _settings.LlmMultiComplete = MultiToggle.IsChecked.GetValueOrDefault();
        _settings.Save();
        ApplyLlmUi();
        RefreshCandidates();
    }

    private void ApplyLlmUi()
    {
        SuggestionPanel.Visibility = RawMode ? Visibility.Collapsed : Visibility.Visible;
        int rows = SuggestionRowCount();
        while (_suggestionRows.Count < rows)
        {
            var row = new TextBlock
            {
                Text = " ",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(2, 1, 2, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _suggestionRows.Add(row);
            SuggestionPanel.Children.Add(row);
        }
        while (_suggestionRows.Count > rows)
        {
            SuggestionPanel.Children.Remove(_suggestionRows[^1]);
            _suggestionRows.RemoveAt(_suggestionRows.Count - 1);
        }
        UpdateLlmUi();
    }

    private void UpdateLlmUi()
    {
        LlmCostText.Text = _settings.LlmRequestCount > 0 || _settings.LlmTotalCostUsd > 0 ? $"${_settings.LlmTotalCostUsd:0.####} ({_settings.LlmRequestCount} calls)" : "";
        // A matched trigger phrase runs the LLM even with the toggle off — highlight the toggle so the user can tell why suggestions are appearing (without changing its checked state).
        bool triggered = !_settings.LlmEnabled && _settings.LlmTriggerEnabled && _predictor.IsConfigured && MatchLlmTrigger(InputBox.Text) != null;
        LlmToggle.Foreground = new SolidColorBrush(triggered ? Color.FromRgb(0x7F, 0xD4, 0x7F) : Color.FromRgb(0x99, 0x99, 0x99));
        LlmToggle.FontWeight = triggered ? FontWeights.Bold : FontWeights.Normal;
        _candWindowStart = Math.Clamp(_candWindowStart, 0, Math.Max(0, _candidates.Count - _suggestionRows.Count));
        for (int i = 0; i < _suggestionRows.Count; i++)
        {
            var row = _suggestionRows[i];
            int index = _candWindowStart + i;
            row.Text = index < _candidates.Count ? _candidates[index] : " ";
            bool selected = _candIndex == index && _candIndex >= 0;
            row.Background = selected ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) : Brushes.Transparent;
            row.Foreground = new SolidColorBrush(selected ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x8A, 0x8A, 0x8A));
        }
    }

    private void ScheduleLlmPrediction()
    {
        if (!UseLlmFor(InputBox.Text) || RawMode)
        {
            return;
        }
        _llmTimer.Stop();
        _llmTimer.Start();
    }

    private void RequestLlmPrediction()
    {
        if (!UseLlmFor(InputBox.Text) || Active is not { Dead: false, Session: not null } vm)
        {
            return;
        }
        int count = SuggestionRowCount();
        var (messages, display) = BuildLlmContext(vm, InputBox.Text, count);
        _predictor.Request(messages, display, count);
    }

    private (List<LlmMessage> Messages, string Display) BuildLlmContext(TabVm vm, string typed, int count)
    {
        var pairs = new List<(string Cmd, string Output)>();
        lock (vm.Screen.Sync)
        {
            var marks = vm.CommandMarks;
            int start = Math.Max(0, marks.Count - LlmMaxPairs);
            for (int i = start; i < marks.Count; i++)
            {
                int from = marks[i].Line + 1;
                int to = i + 1 < marks.Count ? marks[i + 1].Line : vm.Screen.TotalLines;
                var lines = new List<string>();
                for (int l = from; l < to && l < vm.Screen.TotalLines && lines.Count < LlmMaxOutputLines; l++)
                {
                    var text = vm.Screen.GetLine(l).GetText();
                    if (text.Length > 0)
                    {
                        lines.Add(text);
                    }
                }
                var output = string.Join("\n", lines);
                if (output.Length > LlmMaxOutputChars)
                {
                    output = output[..LlmMaxOutputChars] + "\n[truncated]";
                }
                pairs.Add((marks[i].Cmd, output));
            }
        }
        // Newest pairs win the budget; chronological order is restored afterwards.
        int budgetChars = Math.Max(1000, _settings.LlmContextTokens * 4);
        var kept = new List<(string Cmd, string Output)>();
        int used = 0;
        for (int i = pairs.Count - 1; i >= 0; i--)
        {
            used += pairs[i].Cmd.Length + pairs[i].Output.Length;
            if (used > budgetChars && kept.Count > 0)
            {
                break;
            }
            kept.Insert(0, pairs[i]);
        }
        var messages = new List<LlmMessage>
        {
            new("system", count > 1
                ? $"You predict the next shell command a user will run in cmd.exe on Windows. Respond with exactly {count} alternative complete command lines, one per line, most likely first. No commentary, no markdown, no numbering."
                : "You predict the next shell command a user will run in cmd.exe on Windows. Respond with exactly one complete command line. No commentary, no markdown."),
        };
        foreach (var (cmd, output) in kept)
        {
            messages.Add(new LlmMessage("user", "The user ran:\n" + cmd));
            if (output.Length > 0)
            {
                messages.Add(new LlmMessage("user", "Output:\n" + output));
            }
        }
        var instruction = MatchLlmTrigger(typed);
        string ask;
        if (instruction is { Length: > 0 })
        {
            ask = $"The user asked in natural language: \"{instruction}\"\nRespond with the command(s) that accomplish this.";
        }
        else if (typed.Length > 0)
        {
            ask = $"The user has typed so far: {typed}\nComplete or rewrite it into the full command they most likely want.";
        }
        else
        {
            ask = "Predict the next command the user will run.";
        }
        messages.Add(new LlmMessage("user", $"Current directory: {vm.Info.Cwd}\n" + ask));
        var display = string.Join("\n\n", messages.Select(m => $"[{m.Role}]\n{m.Content}"));
        return (messages, display);
    }

    // Debounced, off-thread recompute of the regular candidates: history (in-memory) is gathered on the UI thread, the filesystem/yarn work runs on a background thread, and the merged result is applied only if the input hasn't changed since — so keystrokes never wait on directory enumeration.
    private void ComputeRegularCandidatesAsync()
    {
        string text = InputBox.Text;
        if (UseLlmFor(text))
        {
            return;
        }
        var history = text.Length > 0 && Active is { } vm ? HistoryCandidates(text, vm) : new List<string>();
        string cwd = Active?.Info.Cwd ?? "";
        int gen = ++_candComputeGen;
        Task.Run(() =>
        {
            List<string> fs;
            try
            {
                fs = FsAndYarnCandidates(text, cwd);
            }
            catch
            {
                fs = new List<string>();
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _candComputeGen || InputBox.Text != text || UseLlmFor(text))
                {
                    return;
                }
                _candidates = CombineCandidates(history, fs);
                _candText = text;
                _candWindowStart = 0;
                UpdateLlmUi();
            });
        });
    }

    // Regular (non-LLM) candidates: history commands matching the whole input first, then yarn scripts when applicable, then entries of the current directory whose name starts with the last space-separated token. Synchronous — used as the Tab fallback when the async result isn't ready.
    private List<string> BuildTabCandidates(string text)
    {
        var history = text.Length > 0 && Active is { } vm ? HistoryCandidates(text, vm) : new List<string>();
        return CombineCandidates(history, FsAndYarnCandidates(text, Active?.Info.Cwd ?? ""));
    }

    private static List<string> CombineCandidates(List<string> history, List<string> fs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var h in history)
        {
            if (seen.Add(h))
            {
                result.Add(h);
            }
        }
        foreach (var f in fs)
        {
            if (seen.Add(f))
            {
                result.Add(f);
            }
        }
        return result;
    }

    private static List<string> HistoryCandidates(string text, TabVm vm)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in vm.History.Match(text))
        {
            if (!string.Equals(entry, text, StringComparison.OrdinalIgnoreCase) && seen.Add(entry))
            {
                result.Add(entry);
            }
        }
        return result;
    }

    // The slow part (directory enumeration, package.json / node_modules\.bin reads). Pure over (text, cwd) so it runs safely on a background thread.
    private List<string> FsAndYarnCandidates(string text, string cwd)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cwd.Length == 0 || text.Length == 0)
        {
            return result;
        }
        int lastSpace = text.LastIndexOf(' ');
        string head = lastSpace >= 0 ? text[..(lastSpace + 1)] : "";
        string token = text[(lastSpace + 1)..];
        int sepIndex = token.LastIndexOfAny(new[] { '\\', '/' });
        string dirPart = sepIndex >= 0 ? token[..(sepIndex + 1)] : "";
        string prefix = token[(sepIndex + 1)..];
        if (head.Trim().Equals("yarn", StringComparison.OrdinalIgnoreCase))
        {
            AddYarnCandidates(head, token, cwd, result, seen);
        }
        try
        {
            string dir = Path.IsPathRooted(dirPart) ? dirPart : Path.Combine(cwd, dirPart);
            var names = Directory.GetFileSystemEntries(dir)
                .Select(e => Path.GetFileName(e) + (Directory.Exists(e) ? "\\" : ""))
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var candidate = head + dirPart + name;
                if (seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }
        }
        catch
        {
        }
        return result;
    }

    // Context-aware completion for "yarn <partial>": package.json script names (in file order) followed by node_modules\.bin executables from the tab's current directory.
    private void AddYarnCandidates(string head, string prefix, string cwd, List<string> result, HashSet<string> seen)
    {
        foreach (var name in GetYarnNames(cwd))
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = head + name;
                if (seen.Add(candidate))
                {
                    result.Add(candidate);
                }
            }
        }
    }

    // Cached briefly because this runs on every keystroke while recomputing candidates.
    private List<string> GetYarnNames(string cwd)
    {
        // Reached from the background candidate thread as well as the UI thread (Tab fallback); serialize cache access.
        lock (_yarnLock)
        {
            if (cwd == _yarnNamesCwd && Environment.TickCount64 - _yarnNamesTick < YarnNamesCacheMs)
            {
                return _yarnNames;
            }
            var names = new List<string>();
            try
            {
                var packagePath = Path.Combine(cwd, "package.json");
                if (File.Exists(packagePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(packagePath));
                    if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var script in scripts.EnumerateObject())
                        {
                            names.Add(script.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("complete", "package.json parse failed: " + ex.Message);
            }
            try
            {
                var binDir = Path.Combine(cwd, "node_modules", ".bin");
                if (Directory.Exists(binDir))
                {
                    names.AddRange(Directory.GetFiles(binDir)
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
            }
            _yarnNamesCwd = cwd;
            _yarnNamesTick = Environment.TickCount64;
            _yarnNames = names;
            return names;
        }
    }

    // Bare modifier keys (Ctrl/Shift/Alt/Win pressed alone) are still forwarded in win32-input-mode, but must not scroll the view — otherwise holding Ctrl to Ctrl-click jumps the terminal to the bottom.
    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin;

    // --- raw keystroke mode ---

    // A REPL line typed in raw mode is any run of plain typing ended by a plain Enter. We rebuild it from keystrokes so it can go into history; a keystroke count over this cap (a long message, or a program the user is driving rather than typing a command into) drops the line instead.
    private const int MaxRawHistoryKeys = 200;

    // Printable typed text; control characters mean editing we can't model linearly, so they taint the line.
    private void RawHistoryText(TabVm vm, string text)
    {
        foreach (var ch in text)
        {
            if (ch < ' ' || ch == '\x7f')
            {
                vm.RawHistoryTainted = true;
            }
            else
            {
                vm.RawHistoryLine.Append(ch);
            }
            vm.RawHistoryKeys++;
        }
        if (vm.RawHistoryKeys > MaxRawHistoryKeys)
        {
            vm.RawHistoryTainted = true;
        }
    }

    private void RawHistoryBackspace(TabVm vm)
    {
        if (vm.RawHistoryLine.Length > 0)
        {
            vm.RawHistoryLine.Length--;
        }
        vm.RawHistoryKeys++;
    }

    // Anything other than plain typing/backspace (arrows, Tab-completion, Ctrl chords, a modified Enter that inserts a newline) makes the reconstructed line unreliable.
    private void RawHistoryTaint(TabVm vm)
    {
        vm.RawHistoryTainted = true;
        vm.RawHistoryKeys++;
    }

    private void RawHistoryCommit(TabVm vm)
    {
        var line = vm.RawHistoryLine.ToString();
        bool ok = !vm.RawHistoryTainted && vm.RawHistoryKeys <= MaxRawHistoryKeys && line.Trim().Length > 0;
        vm.RawHistoryLine.Clear();
        vm.RawHistoryKeys = 0;
        vm.RawHistoryTainted = false;
        if (ok)
        {
            vm.History.Add(line);
        }
    }

    private void Term_KeyDown(object sender, KeyEventArgs e)
    {
        if (!RawMode || Active is not { Dead: false, Session: { } session } vm)
        {
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var encoded = vm.Win32Input ? InputEncoder.EncodeWin32Key(key, Keyboard.Modifiers) : InputEncoder.Encode(key, Keyboard.Modifiers);
        if (encoded != null)
        {
            e.Handled = true;
            session.SendInput(encoded);
            LogRawKeystroke(System.Text.Encoding.UTF8.GetString(encoded));
            if (!IsModifierKey(key))
            {
                Term.ScrollToBottom();
            }
            bool noEditMods = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0;
            if (key == Key.Enter && noEditMods)
            {
                RawHistoryCommit(vm);
            }
            else if (key == Key.Back && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                RawHistoryBackspace(vm);
            }
            else
            {
                RawHistoryTaint(vm);
            }
        }
    }

    private void Term_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (!RawMode || Active is not { Dead: false, Session: { } session } vm || e.Text.Length == 0)
        {
            return;
        }
        if (vm.Win32Input)
        {
            session.SendInput(InputEncoder.EncodeWin32Text(e.Text));
        }
        else
        {
            session.SendText(e.Text);
        }
        LogRawKeystroke(e.Text);
        Term.ScrollToBottom();
        RawHistoryText(vm, e.Text);
        e.Handled = true;
    }

    // --- search ---

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_restoringSearch)
        {
            return;
        }
        RecomputeSearch();
    }

    // Snapshot the live search UI onto the tab being left.
    private void SaveSearchState(TabVm vm)
    {
        vm.SearchOpen = SearchBar.Visibility == Visibility.Visible;
        vm.SearchQuery = SearchBox.Text;
        vm.SearchFilter = FilterToggle.IsChecked == true;
        vm.SearchMatchIndex = _matchIndex;
    }

    // Bring the search UI to what the tab being entered had; recompute against its own buffer.
    private void RestoreSearchState(TabVm vm)
    {
        _restoringSearch = true;
        SearchBox.Text = vm.SearchQuery;
        FilterToggle.IsChecked = vm.SearchFilter;
        SearchBar.Visibility = vm.SearchOpen ? Visibility.Visible : Visibility.Collapsed;
        _restoringSearch = false;
        if (!vm.SearchOpen)
        {
            _matches = new List<(int, int)>();
            _matchIndex = -1;
            Term.SetSearchResults(null);
            Term.SetFilter(null);
            return;
        }
        RecomputeSearch();
        // RecomputeSearch parks on the last match; put the caret back on the match the tab was left at.
        if (vm.SearchMatchIndex >= 0 && vm.SearchMatchIndex < _matches.Count)
        {
            _matchIndex = vm.SearchMatchIndex;
            ApplySearch();
        }
    }

    // Full rescan (query changed, tab switched, scrollback prepended): throw away the incremental cache and scan from scratch.
    private void RecomputeSearch()
    {
        _stableMatches = new List<(int, int)>();
        _searchScanned = 0;
        _searchDropped = Active?.Screen.DroppedLines ?? 0;
        ScanSearch();
        _matchIndex = _matches.Count - 1;
        ApplySearch();
    }

    // Rebuild _matches: fold newly-committed scrollback lines into the stable set once, then rescan the volatile live screen each time. Cap trimming (DroppedLines changed) shifts indices, so start over then.
    private void ScanSearch()
    {
        var vm = Active;
        string query = SearchBox.Text;
        if (vm == null || query.Length == 0)
        {
            _stableMatches = new List<(int, int)>();
            _searchScanned = 0;
            _matches = new List<(int, int)>();
            return;
        }
        lock (vm.Screen.Sync)
        {
            if (vm.Screen.DroppedLines != _searchDropped)
            {
                _searchDropped = vm.Screen.DroppedLines;
                _stableMatches = new List<(int, int)>();
                _searchScanned = 0;
            }
            int scrollback = vm.Screen.ScrollbackCount;
            int total = vm.Screen.TotalLines;
            for (int i = _searchScanned; i < scrollback; i++)
            {
                AddLineMatches(vm.Screen.GetLine(i).GetText(), i, query, _stableMatches);
            }
            _searchScanned = Math.Max(_searchScanned, scrollback);
            var combined = new List<(int, int)>(_stableMatches);
            for (int i = scrollback; i < total; i++)
            {
                AddLineMatches(vm.Screen.GetLine(i).GetText(), i, query, combined);
            }
            _matches = combined;
        }
    }

    private static void AddLineMatches(string text, int line, string query, List<(int Line, int Col)> into)
    {
        int from = 0;
        while (true)
        {
            int at = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                break;
            }
            into.Add((line, at));
            from = at + Math.Max(1, query.Length);
        }
    }

    // Called from the render tick when the active tab produced output and search is open: re-scan cheaply and reflect new matches. If the caret sat on the very last match, it follows the newest one; otherwise it stays put (by identity) and the view doesn't move.
    private void UpdateSearchForNewOutput()
    {
        long now = Environment.TickCount64;
        if (now - _lastSearchUpdateTicks < SearchUpdateThrottleMs)
        {
            return;
        }
        _lastSearchUpdateTicks = now;
        var prev = _matches;
        bool wasOnLast = _matchIndex >= 0 && _matchIndex == prev.Count - 1;
        (int, int)? current = _matchIndex >= 0 && _matchIndex < prev.Count ? prev[_matchIndex] : null;
        ScanSearch();
        bool changed = _matches.Count != prev.Count || (_matches.Count > 0 && !_matches[^1].Equals(prev[^1]));
        if (!changed)
        {
            return;
        }
        if (_matches.Count == 0)
        {
            _matchIndex = -1;
        }
        else if (wasOnLast)
        {
            _matchIndex = _matches.Count - 1;
        }
        else if (current is { } c)
        {
            int idx = _matches.IndexOf(c);
            _matchIndex = idx >= 0 ? idx : Math.Clamp(_matchIndex, 0, _matches.Count - 1);
        }
        else
        {
            _matchIndex = _matches.Count - 1;
        }
        // Only follow the view when auto-advancing to the newest match; a passive highlight refresh must not yank the user's scroll.
        ApplySearch(scroll: wasOnLast && _matches.Count > 0);
    }

    private void ApplySearch(bool scroll = true)
    {
        if (_matches.Count == 0)
        {
            SearchCount.Text = SearchBox.Text.Length > 0 ? "0/0" : "";
            Term.SetSearchResults(null);
            Term.SetFilter(null);
            return;
        }
        int queryLen = SearchBox.Text.Length;
        var byLine = new Dictionary<int, List<SearchSpan>>();
        for (int i = 0; i < _matches.Count; i++)
        {
            var (line, col) = _matches[i];
            if (!byLine.TryGetValue(line, out var spans))
            {
                spans = new List<SearchSpan>();
                byLine[line] = spans;
            }
            spans.Add(new SearchSpan { Col = col, Len = queryLen, Current = i == _matchIndex });
        }
        SearchCount.Text = $"{_matchIndex + 1}/{_matches.Count}";
        Term.SetSearchResults(byLine);
        BuildFilter();
        if (scroll && _matchIndex >= 0)
        {
            Term.ScrollToAbsLine(_matches[_matchIndex].Line);
        }
    }

    private void FilterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _restoringSearch)
        {
            return;
        }
        BuildFilter();
        if (_matches.Count > 0)
        {
            Term.ScrollToAbsLine(_matches[_matchIndex].Line);
        }
    }

    // In filter mode, show only the matching lines plus the lines indented under each match (its subtree); blank lines don't break a subtree.
    private void BuildFilter()
    {
        var vm = Active;
        if (vm == null || FilterToggle.IsChecked != true || _matches.Count == 0)
        {
            Term.SetFilter(null);
            return;
        }
        var matchLines = new SortedSet<int>();
        foreach (var (line, _) in _matches)
        {
            matchLines.Add(line);
        }
        var show = new SortedSet<int>();
        lock (vm.Screen.Sync)
        {
            int total = vm.Screen.TotalLines;
            foreach (int m in matchLines)
            {
                if (m < 0 || m >= total)
                {
                    continue;
                }
                show.Add(m);
                int mIndent = LeadingIndent(vm.Screen.GetLine(m).GetText());
                for (int j = m + 1; j < total; j++)
                {
                    var text = vm.Screen.GetLine(j).GetText();
                    if (text.Length == 0)
                    {
                        show.Add(j);
                        continue;
                    }
                    if (LeadingIndent(text) > mIndent)
                    {
                        show.Add(j);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        Term.SetFilter(show.ToList());
    }

    private static int LeadingIndent(string text)
    {
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
        {
            i++;
        }
        return i;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _matches.Count > 0)
        {
            e.Handled = true;
            StepMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
    }

    // Step to the previous (up) or next (down) match and re-apply, keeping focus in the search box so keyboard navigation continues.
    private void StepMatch(bool backwards)
    {
        if (_matches.Count == 0)
        {
            return;
        }
        _matchIndex = ((_matchIndex + (backwards ? -1 : 1)) % _matches.Count + _matches.Count) % _matches.Count;
        ApplySearch();
    }

    private void SearchPrev_Click(object sender, RoutedEventArgs e)
    {
        StepMatch(true);
        SearchBox.Focus();
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e)
    {
        StepMatch(false);
        SearchBox.Focus();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e)
    {
        CloseSearch();
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        FilterToggle.IsChecked = false;
        Term.SetSearchResults(null);
        Term.SetFilter(null);
        // Closing search is per-tab: don't let this tab reopen it on the next switch back.
        if (Active is { } vm)
        {
            vm.SearchOpen = false;
            vm.SearchFilter = false;
            vm.SearchMatchIndex = -1;
        }
        FocusInput();
    }

    // --- top bar buttons ---

    private void NewTabBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenNewTab();
    }

    // Tight tab strip: the + rides inline right after the last tab, and only when the tabs overflow do the scroll arrows and a fixed always-visible + appear.
    private void TabScroller_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Wrap mode never scrolls — the capacity math guarantees fit, and the chrome must not flicker in on transient layout passes.
        bool overflow = _settings.SingleRowTabs && TabScroller.ScrollableWidth > 0.5;
        ScrollLeftBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        ScrollRightBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        NewTabBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _inlinePlus.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
    }

    // The + button and Ctrl+T are the same action: a new tab in the current tab's directory.
    private void OpenNewTab()
    {
        CreateTab(NewTabInfo(Active?.Info.Cwd is { Length: > 0 } cwd ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), activate: true, insertAfterActive: true);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    // Rebuild-and-reload: kicks off the repo's reload script, which builds, copies the output to a fresh instance folder, and starts a --takeover process for this window id. This window stays alive until the new build succeeds and kills it — a failed build costs nothing (check app.log for [reload] entries).
    private void ReloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BuildInfo.RepoRoot.Length == 0 || !Directory.Exists(BuildInfo.RepoRoot))
        {
            AppLog.Write("ui", "reload: repo root unavailable: " + BuildInfo.RepoRoot);
            return;
        }
        // Shift+Click skips the rebuild: take the window over with the newest already-staged build (the stable bat's target).
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            SaveState();
            var exe = StableLauncher.CurrentTarget();
            if (exe == null || !File.Exists(exe))
            {
                exe = Environment.ProcessPath!;
            }
            try
            {
                var fast = new ProcessStartInfo(exe) { UseShellExecute = false };
                fast.ArgumentList.Add("--window");
                fast.ArgumentList.Add(_windowId);
                fast.ArgumentList.Add("--resume");
                fast.ArgumentList.Add("--takeover");
                Process.Start(fast);
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "fast reload failed: " + ex);
            }
            return;
        }
        SaveState();
        SetReloadBuilding(true);
        StartReloadBuild();
    }

    private void SetReloadBuilding(bool building)
    {
        ReloadBtn.Content = building ? "⏳" : "⟳";
        ReloadBtn.IsEnabled = !building;
    }

    // Runs `yarn reload-window`, streaming its output into the build log (overwritten each run) for the button tooltip and the settings view. A successful build spawns a takeover process that kills this window; a failure, a spawn error, or a wedged build that hits the timeout all restore the button instead of leaving the hourglass stuck forever.
    private void StartReloadBuild()
    {
        var logPath = AppPaths.BuildLogFile;
        var header = $"=== reload started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===";
        var tail = new Queue<string>();
        var gate = new object();
        try
        {
            File.WriteAllText(logPath, header + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "build log init failed: " + ex.Message);
        }
        void AppendLine(string line)
        {
            lock (gate)
            {
                tail.Enqueue(line);
                while (tail.Count > BuildLogTooltipTailLines)
                {
                    tail.Dequeue();
                }
                try
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
                catch
                {
                }
                var tip = header + Environment.NewLine + string.Join(Environment.NewLine, tail);
                Dispatcher.BeginInvoke(() => ReloadBtn.ToolTip = tip);
            }
        }

        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = BuildInfo.RepoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("yarn");
        psi.ArgumentList.Add("reload-window");
        psi.ArgumentList.Add(_windowId);

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    AppendLine(e.Data);
                }
            };
            proc.Exited += (_, _) =>
            {
                AppendLine($"=== exited with code {proc.ExitCode} ===");
                Dispatcher.BeginInvoke(() => SetReloadBuilding(false));
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "reload spawn failed: " + ex);
            AppendLine("=== failed to start build: " + ex.Message + " ===");
            SetReloadBuilding(false);
            return;
        }

        // The timeout reads proc.HasExited (never disposed, so this stays valid) — a wedged build is killed tree-and-all so the button recovers.
        Task.Delay(TimeSpan.FromMinutes(ReloadTimeoutMinutes)).ContinueWith(_ =>
        {
            try
            {
                if (proc.HasExited)
                {
                    return;
                }
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            AppendLine($"=== TIMED OUT after {ReloadTimeoutMinutes} minutes — build killed ===");
            Dispatcher.BeginInvoke(() => SetReloadBuilding(false));
        });
    }

    private SettingsWindow OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings, id => _tabs.Any(t => t.Info.Id == id), id => AddShellTab(id), _predictor);
            if (IsLoaded)
            {
                _settingsWindow.Owner = this;
            }
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
        return _settingsWindow;
    }

    // The escape-rendering setting is per tab (right-click a tab to toggle); the global setting is only the default for newly created tabs.
    private void ApplySettings()
    {
        ApplyLlmUi();
        FpsText.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in _tabs)
        {
            tab.Screen.ScrollbackCap = _settings.ScrollbackLines;
            tab.ResourceRow.Visibility = _settings.ShowTabResources ? Visibility.Visible : Visibility.Collapsed;
        }
        RelayoutTabRows();
    }

    private void CenterOverWindow(IntPtr reference)
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double left;
            double top;
            if (reference != IntPtr.Zero && GetWindowRect(reference, out var rect) && rect.Right > rect.Left)
            {
                left = (rect.Left + rect.Right) / 2.0 / scale - Width / 2;
                top = (rect.Top + rect.Bottom) / 2.0 / scale - Height / 2;
            }
            else
            {
                left = (SystemParameters.WorkArea.Width - Width) / 2;
                top = (SystemParameters.WorkArea.Height - Height) / 2;
            }
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "center over foreground failed: " + ex.Message);
        }
    }

    // Windows refuses SetForegroundWindow to processes the user never interacted with, so a freshly spawned window (Cursor's Ctrl+Shift+C) can't take focus the polite way. This throws the whole launcher toolbox at it — Alt nudge (defeats the foreground lock), input-queue attach to the foreground thread, BringWindowToTop, SwitchToThisWindow — and retries on a timer, because the launcher often re-asserts its own focus right after spawning us and a single early attempt loses that race.
    private void ForceForeground()
    {
        var attemptDelaysMs = new[] { 0, 100, 250, 500, 1000 };
        foreach (var delay in attemptDelaysMs)
        {
            Task.Delay(delay).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero || GetForegroundWindow() == handle)
                {
                    return;
                }
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                var foreground = GetForegroundWindow();
                uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
                uint ourThread = GetCurrentThreadId();
                bool attached = foregroundThread != 0 && foregroundThread != ourThread && AttachThreadInput(ourThread, foregroundThread, true);
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
                SwitchToThisWindow(handle, true);
                if (attached)
                {
                    AttachThreadInput(ourThread, foregroundThread, false);
                }
                Activate();
                FocusInput();
            }));
        }
    }

    // Momentarily topmost, then not: lands above every normal window without activating (SWP_NOACTIVATE), so the launcher keeps keyboard focus.
    private void BringToTopWithoutFocus()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const int WM_COPYDATA = 0x004A;
    private const uint GA_ROOT = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fUnknown);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);
}
