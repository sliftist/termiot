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
        public int Dirty;
        public bool Win32Input;
        public bool LoggedFirstRender;
        public string PendingInput = "";
        public string LastCommand = "";
        public bool Running;
        // (command, absolute line index) pairs from termiot-cmd markers; guarded by Screen.Sync (appended during parsing, read for LLM context).
        public List<(string Cmd, int Line)> CommandMarks = new();
        // Set when the shell process names itself via OSC title sequences; empty = use our automatic "folder + command" title.
        public string CustomTitle = "";
        public Border Header = null!;
        public TextBlock HeaderText = null!;
        public TextBox TitleEditor = null!;
        public TextBlock AutoResumeButton = null!;
        public TextBlock RevertTitleButton = null!;
        public TextBlock Win32Badge = null!;
    }

    private readonly string _windowId;
    private readonly AppSettings _settings;
    private readonly CommandHistory _history;
    private readonly List<TabVm> _tabs = new();
    private int _active = -1;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _syncTimer;
    private List<(int Line, int Col)> _matches = new();
    private int _matchIndex = -1;
    private const int YarnNamesCacheMs = 5000;

    private string _yarnNamesCwd = "";
    private long _yarnNamesTick;
    private List<string> _yarnNames = new();
    private readonly LlmPredictor _predictor;
    private readonly DispatcherTimer _llmTimer;
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

    private const int LlmDebounceMs = 400;
    private const int LlmMaxPairs = 20;
    private const int LlmMaxOutputChars = 3000;
    private const int LlmMaxOutputLines = 40;
    private bool _settingInputText;
    private bool _closedBecauseEmpty;
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
        _settings = AppSettings.Load();
        _history = new CommandHistory();
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
        _syncTimer.Tick += (_, _) => SyncTabsFromWindowFile();
        _syncTimer.Start();

        _predictor = new LlmPredictor(_settings);
        _predictor.Updated += () => Dispatcher.BeginInvoke(() =>
        {
            if (UseLlmFor(InputBox.Text))
            {
                _candidates = _predictor.Suggestions.ToList();
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
        BuildTimeText.Text = BuildInfo.Display;
        WindowNameBox.Text = state.Name;
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
        Term.CellSizeChanged += OnCellSizeChanged;
        Term.KeyDown += Term_KeyDown;
        Term.TextInput += Term_TextInput;
        PreviewKeyDown += Window_PreviewKeyDown;
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        // Tab adoption between our windows and open-tab-here requests arrive as WM_COPYDATA — a private channel, no OLE drag-drop involved.
        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            LastActiveWindow.Save(_windowId, handle);
        };
        Closing += (_, _) =>
        {
            if (_closedBecauseEmpty)
            {
                Termiot.WindowState.Delete(_windowId);
            }
            else
            {
                SaveState(closedCleanly: !Program.SessionEnding);
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
        foreach (var info in state.LoadTabs())
        {
            bool alive = HostInfo.IsShellAlive(info.Id);
            // A shell that never ran (no host.json) is a fresh seed, not a terminated session — start it outright instead of asking to resume.
            bool everRan = File.Exists(AppPaths.HostInfoFile(info.Id));
            var vm = CreateTab(info, activate: false, start: alive || !everRan);
            if (!alive && everRan && (_settings.AutoResumeShells || forceResume))
            {
                ResumeTab(vm, runAutoResumeCommand: true);
            }
            else if (!alive && !everRan)
            {
                // Fresh seeds with a pre-written AUTORESUME.cmd (--ensure launches) run their command immediately.
                RunAutoResumeCommand(vm);
            }
        }
        if (_tabs.Count > 0)
        {
            ActivateTab(Math.Clamp(state.ActiveIndex, 0, _tabs.Count - 1));
        }
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

    private TabVm? Active => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    private bool RawMode => RawToggle.IsChecked.GetValueOrDefault();

    private static TabInfo NewTabInfo(string cwd)
    {
        return new TabInfo { Id = Program.NewShellId(), Cwd = cwd, Title = DefaultTabTitle };
    }

    private TabVm CreateTab(TabInfo info, bool activate, bool start = true, bool insertAfterActive = false, int insertIndex = -1)
    {
        var screen = new TermScreen(120, 30);
        var parser = new VtParser(screen) { ShowEscapes = _settings.ShowEscapeSequences };
        var vm = new TabVm { Info = info, Screen = screen, Parser = parser, PendingInput = info.PendingInput };
        parser.OnTitle = title => Dispatcher.BeginInvoke(() =>
        {
            title = title.Trim();
            // cmd constantly re-announces its own path as the title; only deliberate titles (e.g. the `title` command) count as custom.
            vm.CustomTitle = title.Length == 0 || title.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ? "" : title;
            RefreshTabTitle(vm);
            if (vm == Active)
            {
                UpdateWindowTitle();
            }
        });
        parser.OnCommandMarker = cmd => vm.CommandMarks.Add((cmd, vm.Screen.ScrollbackCount + vm.Screen.CursorY));
        parser.OnWin32InputMode = on =>
        {
            vm.Win32Input = on;
            Dispatcher.BeginInvoke(() =>
            {
                RefreshTabTitle(vm);
                if (vm == Active)
                {
                    UpdateWindowTitle();
                }
            });
        };
        BuildTabHeader(vm);
        RefreshTabTitle(vm);
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
        var session = ShellSession.Create(vm.Info.Id, string.IsNullOrEmpty(vm.Info.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : vm.Info.Cwd, _windowId, vm.Screen, vm.Parser);
        vm.Session = session;
        session.OutputReceived += () => Interlocked.Exchange(ref vm.Dirty, 1);
        session.Exited += _ => Dispatcher.BeginInvoke(() =>
        {
            // A stale Exited from a replaced session (e.g. after a shell restart) must not mark the new one dead.
            if (vm.Session != session)
            {
                return;
            }
            vm.Dead = true;
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
        vm.Session?.ShutdownHost();
        vm.Session = null;
        vm.Dead = false;
        vm.Running = false;
        RefreshTabTitle(vm);
        Task.Delay(ShellRestartDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (vm.Session != null || !_tabs.Contains(vm))
            {
                return;
            }
            bool hasAutoResume = File.Exists(AppPaths.AutoResumeFile(vm.Info.Id));
            if (hasAutoResume && _settings.AutoResumeShells)
            {
                ResumeTab(vm, runAutoResumeCommand: true);
                return;
            }
            if (!hasAutoResume)
            {
                StartSession(vm);
                RefreshTabTitle(vm);
            }
            if (vm == Active)
            {
                UpdateResumeOverlay();
                ApplyInputMode();
                UpdateWindowTitle();
            }
        }));
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
        if (_uiReady)
        {
            ScheduleSave();
        }
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
        if (vm.Session != null)
        {
            vm.Session.ShutdownHost();
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

    private void UpdateResumeOverlay()
    {
        var vm = Active;
        bool show = vm is { Session: null };
        ResumeOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }
        string autoResumePath = AppPaths.AutoResumeFile(vm!.Info.Id);
        string content = "";
        try
        {
            if (File.Exists(autoResumePath))
            {
                content = File.ReadAllText(autoResumePath).Trim();
            }
        }
        catch
        {
        }
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
            ToolTip = "Restart shell (Alt+Pause) — same directory, keeps history",
        };
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
        panel.Children.Add(win32Badge);
        panel.Children.Add(autoRun);
        panel.Children.Add(refresh);
        panel.Children.Add(close);
        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(12, 6, 10, 6),
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
        var pos = e.GetPosition(TabScroller);
        if (pos.X >= 0 && pos.X <= TabScroller.ActualWidth && pos.Y >= 0 && pos.Y <= TabScroller.ActualHeight)
        {
            ShowDropIndicator(TabInsertIndexAt(e.GetPosition(TabStrip).X));
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
        var stripPos = e.GetPosition(TabScroller);
        bool overOwnStrip = stripPos.X >= 0 && stripPos.X <= TabScroller.ActualWidth && stripPos.Y >= 0 && stripPos.Y <= TabScroller.ActualHeight;
        int stripIndex = TabInsertIndexAt(e.GetPosition(TabStrip).X);
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
            var local = TabScroller.PointFromScreen(new Point(x, y));
            if (local.X >= 0 && local.X <= TabScroller.ActualWidth && local.Y >= 0 && local.Y <= TabScroller.ActualHeight)
            {
                insertIndex = TabInsertIndexAt(TabStrip.PointFromScreen(new Point(x, y)).X);
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

    // A green line drawn on an overlay canvas between the tabs — a pure preview; nothing in the strip moves until the drop.
    private void ShowDropIndicator(int index)
    {
        double x = 0;
        for (int i = 0; i < Math.Min(index, _tabs.Count); i++)
        {
            x += _tabs[i].Header.ActualWidth;
        }
        Canvas.SetLeft(DropIndicator, Math.Max(0, x - 1.5));
        Canvas.SetTop(DropIndicator, 0);
        DropIndicator.Height = Math.Max(10, TabStrip.ActualHeight);
        DropIndicator.Visibility = Visibility.Visible;
    }

    private int TabInsertIndexAt(double x)
    {
        double acc = 0;
        for (int i = 0; i < _tabs.Count; i++)
        {
            double width = _tabs[i].Header.ActualWidth;
            if (x < acc + width / 2)
            {
                return i;
            }
            acc += width;
        }
        return _tabs.Count;
    }

    private void RefreshTabHeaders()
    {
        TabStrip.Children.Clear();
        for (int i = 0; i < _tabs.Count; i++)
        {
            var vm = _tabs[i];
            // Dark theme: the selected tab goes DARKER (pure black, merging with the terminal below), not lighter; inactive tabs alternate two shades so their boundaries are visible.
            vm.Header.Background = vm == Active ? Brushes.Black : (i % 2 == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)));
            TabStrip.Children.Add(vm.Header);
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
        _active = index;
        var vm = _tabs[index];
        Term.ShowTermCursor = RawMode && !vm.Dead && vm.Session != null;
        Term.Attach(vm.Screen);
        CwdLabel.Text = vm.Info.Cwd;
        SetInputText(vm.PendingInput);
        // The tab strip scrolls; an activated tab must be visible.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => vm.HeaderText.BringIntoView());
        RefreshCandidates();
        UpdateResumeOverlay();
        UpdateWindowTitle();
        RefreshTabHeaders();
        if (SearchBar.Visibility == Visibility.Visible)
        {
            RecomputeSearch();
        }
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
        vm.Session?.ShutdownHost();
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

    private void RenderDirtyTabs()
    {
        foreach (var vm in _tabs)
        {
            if (Interlocked.Exchange(ref vm.Dirty, 0) == 0)
            {
                continue;
            }
            UpdateCwd(vm);
            if (vm == Active)
            {
                Term.RenderFrame();
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
        menu.Items.Add(escapes);
        menu.Items.Add(rename);
        menu.IsOpen = true;
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
        try
        {
            var autoResumePath = AppPaths.AutoResumeFile(vm.Info.Id);
            if (File.Exists(autoResumePath))
            {
                vm.AutoResumeButton.Visibility = Visibility.Visible;
                vm.AutoResumeButton.ToolTip = File.ReadAllText(autoResumePath).Trim();
            }
            else
            {
                vm.AutoResumeButton.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            vm.AutoResumeButton.Visibility = Visibility.Collapsed;
        }
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

    private void SyncTabsFromWindowFile()
    {
        try
        {
            var state = Termiot.WindowState.Load(_windowId);
            bool added = false;
            foreach (var id in state.Shells)
            {
                if (_tabs.Any(t => t.Info.Id == id) || !Directory.Exists(AppPaths.ShellDir(id)))
                {
                    continue;
                }
                var info = ShellInfo.Load(id) ?? new ShellInfo();
                var tabInfo = new TabInfo { Id = id, Cwd = info.Cwd, Title = info.Title, ForcedTitle = info.ForcedTitle, PendingInput = info.PendingInput, EnsureOrder = info.EnsureOrder };
                // Ordered (--ensure --order) tabs land at their sorted position; unordered ones append.
                int insertAt = tabInfo.EnsureOrder != 0 ? _tabs.TakeWhile(t => t.Info.EnsureOrder <= tabInfo.EnsureOrder).Count() : -1;
                CreateTab(tabInfo, activate: false, start: HostInfo.IsShellAlive(id), insertIndex: insertAt);
                added = true;
                AppLog.Write("ui", $"sync: adopted shell {id} listed in window {_windowId}");
            }
            if (added)
            {
                SaveState();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "window file sync failed: " + ex.Message);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState(bool closedCleanly = false)
    {
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
            ClosedAtTicks = closedCleanly ? DateTime.UtcNow.Ticks : 0,
        }.Save(_windowId);
        foreach (var tab in _tabs)
        {
            ShellInfo.Save(tab.Info);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _draggingVm is { } dragging)
        {
            e.Handled = true;
            CancelTabDrag(dragging.Header);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.T)
        {
            e.Handled = true;
            OpenNewTab();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
        {
            e.Handled = true;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.N)
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
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.W)
        {
            e.Handled = true;
            if (Active is { } vm)
            {
                CloseTab(vm);
            }
        }
        else if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control && _tabs.Count > 0)
        {
            e.Handled = true;
            ActivateTab((_active + 1) % _tabs.Count);
        }
        else if (key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _tabs.Count > 0)
        {
            e.Handled = true;
            ActivateTab((_active - 1 + _tabs.Count) % _tabs.Count);
        }
        else if (key == Key.Pause && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            if (Active is { } restartVm)
            {
                RestartShell(restartVm);
            }
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
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("ui", "clipboard paste failed: " + ex.Message);
                }
            }
        }
        else if (!RawMode && !SearchBox.IsKeyboardFocused && ShouldForwardToShell(key, Keyboard.Modifiers) && Active is { Dead: false, Session: { } session } forwardVm)
        {
            var encoded = forwardVm.Win32Input ? InputEncoder.EncodeWin32Key(key, Keyboard.Modifiers) : InputEncoder.Encode(key, Keyboard.Modifiers);
            if (encoded != null)
            {
                e.Handled = true;
                session.SendInput(encoded);
                Term.ScrollToBottom();
            }
        }
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
            _candidates = BuildTabCandidates(InputBox.Text);
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
        // Any programmatic text change ends a history walk; the Up/Down handlers re-assign the index right after when the change IS the walk.
        _histIndex = -1;
        if (Active is { } vm)
        {
            vm.PendingInput = text;
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            {
                e.Handled = true;
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
                    session.SendText(text + "\r");
                    _history.Add(text);
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
                    _histEntries = _history.Match("");
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
        if (_candidates.Count == 0 && !UseLlmFor(InputBox.Text))
        {
            _candidates = BuildTabCandidates(InputBox.Text);
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

    // Regular (non-LLM) candidates: history commands matching the whole input first, then yarn scripts when applicable, then entries of the current directory whose name starts with the last space-separated token.
    private List<string> BuildTabCandidates(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (text.Length > 0)
        {
            foreach (var entry in _history.Match(text))
            {
                if (!string.Equals(entry, text, StringComparison.OrdinalIgnoreCase) && seen.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }
        int lastSpace = text.LastIndexOf(' ');
        string head = lastSpace >= 0 ? text[..(lastSpace + 1)] : "";
        string token = text[(lastSpace + 1)..];
        int sepIndex = token.LastIndexOfAny(new[] { '\\', '/' });
        string dirPart = sepIndex >= 0 ? token[..(sepIndex + 1)] : "";
        string prefix = token[(sepIndex + 1)..];
        string cwd = Active?.Info.Cwd ?? "";
        if (cwd.Length > 0 && head.Trim().Equals("yarn", StringComparison.OrdinalIgnoreCase))
        {
            AddYarnCandidates(head, token, cwd, result, seen);
        }
        // Filesystem entries only once something is typed — an empty input suggests pure command history — and they always rank below history and yarn candidates.
        if (cwd.Length > 0 && text.Length > 0)
        {
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

    // --- raw keystroke mode ---

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
            Term.ScrollToBottom();
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
        e.Handled = true;
    }

    // --- search ---

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RecomputeSearch();
    }

    private void RecomputeSearch()
    {
        var vm = Active;
        string query = SearchBox.Text;
        _matches = new List<(int, int)>();
        if (vm != null && query.Length > 0)
        {
            lock (vm.Screen.Sync)
            {
                int total = vm.Screen.TotalLines;
                for (int i = 0; i < total; i++)
                {
                    var text = vm.Screen.GetLine(i).GetText();
                    int from = 0;
                    while (true)
                    {
                        int at = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                        if (at < 0)
                        {
                            break;
                        }
                        _matches.Add((i, at));
                        from = at + Math.Max(1, query.Length);
                    }
                }
            }
        }
        _matchIndex = _matches.Count - 1;
        ApplySearch();
    }

    private void ApplySearch()
    {
        if (_matches.Count == 0)
        {
            SearchCount.Text = SearchBox.Text.Length > 0 ? "0/0" : "";
            Term.SetSearchResults(null);
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
        Term.ScrollToAbsLine(_matches[_matchIndex].Line);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _matches.Count > 0)
        {
            e.Handled = true;
            bool backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _matchIndex = ((_matchIndex + (backwards ? -1 : 1)) % _matches.Count + _matches.Count) % _matches.Count;
            ApplySearch();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e)
    {
        CloseSearch();
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        Term.SetSearchResults(null);
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
        bool overflow = TabScroller.ScrollableWidth > 0.5;
        ScrollLeftBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        ScrollRightBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        NewTabBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        NewTabInlineBtn.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
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
        SaveState();
        ReloadBtn.Content = "⏳";
        ReloadBtn.IsEnabled = false;
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = BuildInfo.RepoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("yarn");
        psi.ArgumentList.Add("reload-window");
        psi.ArgumentList.Add(_windowId);
        try
        {
            Process.Start(psi);
            // Still being alive after this long means the build failed (success kills this process) — restore the button so reload can be retried.
            Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                ReloadBtn.Content = "⟳";
                ReloadBtn.IsEnabled = true;
            }));
        }
        catch (Exception ex)
        {
            AppLog.Write("ui", "reload spawn failed: " + ex);
            ReloadBtn.Content = "⟳";
            ReloadBtn.IsEnabled = true;
        }
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
