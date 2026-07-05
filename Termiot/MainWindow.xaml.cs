using System.Diagnostics;
using System.IO;
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
    private const string ShellDragFormat = "termiot-shell-id";
    private const double DragStartThresholdPx = 6;
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
        public bool LoggedFirstRender;
        public string PendingInput = "";
        public string LastCommand = "";
        public bool Running;
        // (command, absolute line index) pairs from termiot-cmd markers; guarded by Screen.Sync (appended during parsing, read for LLM context).
        public List<(string Cmd, int Line)> CommandMarks = new();
        public Border Header = null!;
        public TextBlock HeaderText = null!;
    }

    private readonly string _windowId;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CommandHistory _history = new();
    private readonly List<TabVm> _tabs = new();
    private int _active = -1;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _renderTimer;
    private List<(int Line, int Col)> _matches = new();
    private int _matchIndex = -1;
    private List<string>? _cycleMatches;
    private int _cyclePos = -1;
    private string _cyclePrefix = "";
    private const int YarnNamesCacheMs = 5000;

    private List<string>? _tabCandidates;
    private int _tabPos = -1;
    private string _tabBase = "";
    private string? _suggestion;
    private string _yarnNamesCwd = "";
    private long _yarnNamesTick;
    private List<string> _yarnNames = new();
    private readonly LlmPredictor _predictor;
    private readonly DispatcherTimer _llmTimer;
    private readonly List<TextBlock> _suggestionRows = new();
    private int _llmCycleIndex = -1;
    private string _llmBase = "";

    private const int LlmDebounceMs = 400;
    private const int LlmMaxPairs = 20;
    private const int LlmMaxOutputChars = 3000;
    private const int LlmMaxOutputLines = 40;
    private bool _settingInputText;
    private bool _closedBecauseEmpty;
    private Point _dragStart;
    private TabVm? _dragCandidate;
    private bool _dragCancelled;
    private bool _dropWasSelf;

    public MainWindow(string windowId, Termiot.WindowState state)
    {
        _windowId = windowId;
        InitializeComponent();
        if (state.X is { } x && state.Y is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
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

        _predictor = new LlmPredictor(_settings);
        _predictor.Updated += () => Dispatcher.BeginInvoke(UpdateLlmUi);
        _llmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LlmDebounceMs) };
        _llmTimer.Tick += (_, _) =>
        {
            _llmTimer.Stop();
            RequestLlmPrediction();
        };
        LlmToggle.IsChecked = _settings.LlmEnabled;
        MultiToggle.IsChecked = _settings.LlmMultiComplete;
        ApplyLlmUi();
        RawToggle.IsChecked = _settings.RawInput;
        ApplyInputMode();
        // This is a command line: typing must always land in it. Keyboard focus is denied to everything except the input box, the search box, and the terminal in raw-keys mode — buttons and tabs still work from mouse clicks, which don't need keyboard focus.
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, InputBox) || ReferenceEquals(e.NewFocus, SearchBox) || (RawMode && ReferenceEquals(e.NewFocus, Term)))
            {
                return;
            }
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusInput);
        };
        Activated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusInput);
        Term.CellSizeChanged += OnCellSizeChanged;
        Term.KeyDown += Term_KeyDown;
        Term.TextInput += Term_TextInput;
        PreviewKeyDown += Window_PreviewKeyDown;
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        AllowDrop = true;
        DragOver += Window_DragOver;
        Drop += Window_Drop;
        Closing += (_, _) =>
        {
            if (_closedBecauseEmpty)
            {
                Termiot.WindowState.Delete(_windowId);
            }
            else
            {
                SaveState();
            }
            _settingsWindow?.Close();
            foreach (var tab in _tabs)
            {
                tab.Session?.Detach();
            }
        };

        // Restore: a shell host that survived (window closed or crashed moments ago) is reused as-is; a terminated one gets a tab with a resume confirmation instead of silently starting a new process — unless auto-resume is enabled, which resumes immediately (running the shell's AUTORESUME.cmd if present).
        foreach (var info in state.LoadTabs())
        {
            bool alive = HostInfo.IsShellAlive(info.Id);
            var vm = CreateTab(info, activate: false, start: alive);
            if (!alive && _settings.AutoResumeShells)
            {
                ResumeTab(vm, runAutoResumeCommand: true);
            }
        }
        if (_tabs.Count > 0)
        {
            ActivateTab(Math.Clamp(state.ActiveIndex, 0, _tabs.Count - 1));
        }
        Loaded += (_, _) => FocusInput();
    }

    private TabVm? Active => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    private bool RawMode => RawToggle.IsChecked.GetValueOrDefault();

    private static TabInfo NewTabInfo(string cwd)
    {
        return new TabInfo { Id = Program.NewShellId(), Cwd = cwd, Title = DefaultTabTitle };
    }

    private TabVm CreateTab(TabInfo info, bool activate, bool start = true)
    {
        var screen = new TermScreen(120, 30);
        var parser = new VtParser(screen) { ShowEscapes = _settings.ShowEscapeSequences };
        var vm = new TabVm { Info = info, Screen = screen, Parser = parser };
        parser.OnTitle = title => Dispatcher.BeginInvoke(() =>
        {
            vm.Info.Title = string.IsNullOrWhiteSpace(title) ? DefaultTabTitle : title;
            RefreshTabTitle(vm);
            ScheduleSave();
        });
        parser.OnCommandMarker = cmd => vm.CommandMarks.Add((cmd, vm.Screen.ScrollbackCount + vm.Screen.CursorY));
        BuildTabHeader(vm);
        RefreshTabTitle(vm);
        _tabs.Add(vm);
        if (start)
        {
            StartSession(vm);
        }
        RefreshTabHeaders();
        ScheduleSave();
        if (activate)
        {
            ActivateTab(_tabs.Count - 1);
        }
        return vm;
    }

    private void StartSession(TabVm vm)
    {
        vm.Session = ShellSession.Create(vm.Info.Id, string.IsNullOrEmpty(vm.Info.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : vm.Info.Cwd, _windowId, vm.Screen, vm.Parser);
        vm.Session.OutputReceived += () => Interlocked.Exchange(ref vm.Dirty, 1);
        vm.Session.Exited += _ => Dispatcher.BeginInvoke(() =>
        {
            vm.Dead = true;
            if (vm == Active)
            {
                Term.ShowTermCursor = RawMode && !vm.Dead;
                Term.RenderFrame();
            }
        });
        vm.Session.Begin();
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

    private void ResumeTab(TabVm vm, bool runAutoResumeCommand)
    {
        if (vm.Session != null)
        {
            return;
        }
        StartSession(vm);
        var autoResumePath = AppPaths.AutoResumeFile(vm.Info.Id);
        if (runAutoResumeCommand && File.Exists(autoResumePath) && vm.Session is { } session)
        {
            // The script executes via its path; input is queued in the pty, so cmd runs it as soon as its prompt is ready.
            string command = $"\"{autoResumePath}\"";
            session.SendCommandMarker(command);
            session.SendText(command + "\r");
            vm.LastCommand = command;
            vm.Running = true;
            RefreshTabTitle(vm);
        }
        if (vm == Active)
        {
            UpdateResumeOverlay();
            ApplyInputMode();
            UpdateWindowTitle();
            FocusInput();
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
    public void AddShellTab(string shellId)
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
        CreateTab(new TabInfo { Id = shellId, Cwd = info.Cwd, Title = info.Title }, activate: true);
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
            if (_dragCandidate != vm || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) > DragStartThresholdPx || Math.Abs(pos.Y - _dragStart.Y) > DragStartThresholdPx)
            {
                _dragCandidate = null;
                StartTabDrag(vm, border);
            }
        };
        border.MouseLeftButtonUp += (_, _) => _dragCandidate = null;
        border.QueryContinueDrag += (_, e) =>
        {
            if (e.EscapePressed)
            {
                _dragCancelled = true;
            }
        };
        vm.Header = border;
        vm.HeaderText = text;
    }

    // Drag a tab: dropped on another Termiot window → that window adopts the shell and this one detaches (the shell process never notices beyond a watcher swap). Dropped on empty space → a new window process is created around the shell. Esc cancels.
    private void StartTabDrag(TabVm vm, Border border)
    {
        _dragCancelled = false;
        _dropWasSelf = false;
        var data = new DataObject(ShellDragFormat, vm.Info.Id);
        var result = DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
        if (_dragCancelled || !_tabs.Contains(vm))
        {
            return;
        }
        if (result == DragDropEffects.Move)
        {
            if (!_dropWasSelf)
            {
                RemoveTabForMove(vm);
            }
            return;
        }
        SpawnWindowAroundShell(vm);
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
        RemoveTabForMove(vm);
        Program.SpawnWindowProcess(newWindowId);
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

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ShellDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ShellDragFormat) is not string shellId)
        {
            return;
        }
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        if (_tabs.Any(t => t.Info.Id == shellId))
        {
            _dropWasSelf = true;
            return;
        }
        // Delayed so the source window's Detach lands before we dial the shell's pipe.
        Task.Delay(DragHandoffDelayMs).ContinueWith(_ => Dispatcher.BeginInvoke(() => AddShellTab(shellId)));
    }

    private void RefreshTabHeaders()
    {
        TabStrip.Children.Clear();
        foreach (var vm in _tabs)
        {
            // Dark theme: the selected tab goes DARKER (pure black, merging with the terminal below), not lighter.
            vm.Header.Background = vm == Active ? Brushes.Black : Brushes.Transparent;
            TabStrip.Children.Add(vm.Header);
        }
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
        _cycleMatches = null;
        _cyclePos = -1;
        _tabCandidates = null;
        _llmCycleIndex = -1;
        SetInputText(vm.PendingInput);
        ScheduleLlmPrediction();
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

    private void RefreshTabTitle(TabVm vm)
    {
        var text = vm.HeaderText;
        text.Inlines.Clear();
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

    private void UpdateWindowTitle()
    {
        var vm = Active;
        if (vm == null || vm.Info.Cwd.Length == 0)
        {
            Title = "Termiot";
            return;
        }
        if (vm.LastCommand.Length == 0)
        {
            Title = vm.Info.Cwd;
        }
        else if (vm.Running)
        {
            Title = $"{vm.Info.Cwd} — {vm.LastCommand}";
        }
        else
        {
            Title = $"{vm.Info.Cwd} — ({vm.LastCommand})";
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

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        var self = Process.GetCurrentProcess();
        new Termiot.WindowState
        {
            Shells = _tabs.Select(t => t.Info.Id).ToList(),
            ActiveIndex = _active,
            X = Finite(Left),
            Y = Finite(Top),
            Width = Finite(ActualWidth),
            Height = Finite(ActualHeight),
            OwnerPid = self.Id,
            OwnerStartTicks = self.StartTime.Ticks,
        }.Save(_windowId);
        foreach (var tab in _tabs)
        {
            ShellInfo.Save(tab.Info);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.T)
        {
            e.Handled = true;
            CreateTab(NewTabInfo(Active?.Info.Cwd is { Length: > 0 } cwd ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), activate: true);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
        {
            e.Handled = true;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
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
            if (RawMode && Active is { Dead: false, Session: { } pasteSession })
            {
                e.Handled = true;
                try
                {
                    var text = Clipboard.GetText();
                    if (text.Length > 0)
                    {
                        pasteSession.SendText(text);
                        Term.ScrollToBottom();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("ui", "clipboard paste failed: " + ex.Message);
                }
            }
        }
        else if (!RawMode && !SearchBox.IsKeyboardFocused && ShouldForwardToShell(key, Keyboard.Modifiers) && Active is { Dead: false, Session: { } session })
        {
            var encoded = InputEncoder.Encode(key, Keyboard.Modifiers);
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

    private void ApplyInputMode()
    {
        bool raw = RawMode;
        EditorArea.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        CwdLabel.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        Term.ShowTermCursor = raw && Active is { Dead: false, Session: not null };
        Term.RenderFrame();
    }

    private void RawToggle_Changed(object sender, RoutedEventArgs e)
    {
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
        if (Active is { } vm)
        {
            vm.PendingInput = InputBox.Text;
        }
        _cycleMatches = null;
        _cyclePos = -1;
        _tabCandidates = null;
        _llmCycleIndex = -1;
        ScheduleLlmPrediction();
        UpdateGhost();
    }

    private void UpdateGhost()
    {
        string text = InputBox.Text;
        _suggestion = null;
        if (text.Length > 0)
        {
            _suggestion = _history.Match(text).FirstOrDefault(m => !string.Equals(m, text, StringComparison.OrdinalIgnoreCase));
        }
        if (_suggestion == null && text.Length > 0)
        {
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace >= 0 && text[..(lastSpace + 1)].Trim().Equals("yarn", StringComparison.OrdinalIgnoreCase) && Active?.Info.Cwd is { Length: > 0 } cwd)
            {
                string prefix = text[(lastSpace + 1)..];
                var name = GetYarnNames(cwd).FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !n.Equals(prefix, StringComparison.OrdinalIgnoreCase));
                if (name != null)
                {
                    _suggestion = text[..(lastSpace + 1)] + name;
                }
            }
        }
        if (_suggestion == null)
        {
            GhostText.Text = "";
            return;
        }
        InputBox.UpdateLayout();
        var rect = InputBox.GetRectFromCharacterIndex(text.Length);
        GhostText.Text = _suggestion.Substring(text.Length);
        GhostText.Margin = new Thickness(double.IsInfinity(rect.X) || double.IsNaN(rect.X) ? 0 : rect.X, 0, 0, 0);
    }

    private void SetInputText(string text)
    {
        _settingInputText = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _settingInputText = false;
        if (Active is { } vm)
        {
            vm.PendingInput = text;
        }
        UpdateGhost();
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
                    _cycleMatches = null;
                    _cyclePos = -1;
                    _tabCandidates = null;
                    _llmCycleIndex = -1;
                    Term.ScrollToBottom();
                    ScheduleLlmPrediction();
                }
                break;
            }
            case Key.Tab:
            {
                e.Handled = true;
                bool backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (_settings.LlmEnabled && _predictor.Suggestions.Count > 0)
                {
                    CycleLlmSuggestions(backwards);
                }
                else if (!backwards)
                {
                    CycleTabCompletion();
                }
                break;
            }
            case Key.Up:
            case Key.Down:
            {
                e.Handled = true;
                CycleHistory(e.Key == Key.Up);
                break;
            }
        }
    }

    // --- LLM prediction ---

    private void LlmToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.LlmEnabled = LlmToggle.IsChecked.GetValueOrDefault();
        _settings.Save();
        ApplyLlmUi();
        if (_settings.LlmEnabled)
        {
            if (!_predictor.IsConfigured)
            {
                OpenSettings().SelectLlmTab();
            }
            ScheduleLlmPrediction();
        }
        else
        {
            _predictor.ClearSuggestions();
        }
    }

    private void MultiToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.LlmMultiComplete = MultiToggle.IsChecked.GetValueOrDefault();
        _settings.Save();
        ApplyLlmUi();
        ScheduleLlmPrediction();
    }

    private void ApplyLlmUi()
    {
        bool on = _settings.LlmEnabled;
        MultiToggle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SuggestionPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        int rows = on ? (_settings.LlmMultiComplete ? 3 : 1) : 0;
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
        LlmCostText.Text = _settings.LlmRequestCount > 0 || _settings.LlmTotalCostUsd > 0 ? "$" + _settings.LlmTotalCostUsd.ToString("0.####") : "";
        var suggestions = _predictor.Suggestions;
        for (int i = 0; i < _suggestionRows.Count; i++)
        {
            var row = _suggestionRows[i];
            row.Text = i < suggestions.Count ? suggestions[i] : " ";
            bool selected = _llmCycleIndex == i;
            row.Background = selected ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) : Brushes.Transparent;
            row.Foreground = new SolidColorBrush(selected ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x8A, 0x8A, 0x8A));
        }
    }

    private void ScheduleLlmPrediction()
    {
        if (!_settings.LlmEnabled || !_predictor.IsConfigured || RawMode)
        {
            return;
        }
        _llmTimer.Stop();
        _llmTimer.Start();
    }

    private void RequestLlmPrediction()
    {
        if (!_settings.LlmEnabled || !_predictor.IsConfigured || Active is not { Dead: false, Session: not null } vm)
        {
            return;
        }
        var (messages, display) = BuildLlmContext(vm, InputBox.Text, _settings.LlmMultiComplete);
        _predictor.Request(messages, display, _settings.LlmMultiComplete);
    }

    // Tab with LLM suggestions cycles through them, replacing the whole input (the model may rewrite the entire command); one extra step returns to what the user had typed.
    private void CycleLlmSuggestions(bool backwards)
    {
        var suggestions = _predictor.Suggestions;
        if (suggestions.Count == 0)
        {
            return;
        }
        if (_llmCycleIndex == -1 && !backwards)
        {
            _llmBase = InputBox.Text;
        }
        int slots = suggestions.Count + 1;
        int current = _llmCycleIndex == -1 ? suggestions.Count : _llmCycleIndex;
        current = ((current + (backwards ? -1 : 1)) % slots + slots) % slots;
        _llmCycleIndex = current == suggestions.Count ? -1 : current;
        int keep = _llmCycleIndex;
        string keepBase = _llmBase;
        SetInputText(_llmCycleIndex == -1 ? _llmBase : suggestions[_llmCycleIndex]);
        _llmCycleIndex = keep;
        _llmBase = keepBase;
        UpdateLlmUi();
    }

    private (List<LlmMessage> Messages, string Display) BuildLlmContext(TabVm vm, string typed, bool multi)
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
            new("system", multi
                ? "You predict the next shell command a user will run in cmd.exe on Windows. Respond with exactly 3 alternative complete command lines, one per line, most likely first. No commentary, no markdown, no numbering."
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
        messages.Add(new LlmMessage("user", $"Current directory: {vm.Info.Cwd}\n" + (typed.Length > 0 ? $"The user has typed so far: {typed}\nComplete or rewrite it into the full command they most likely want." : "Predict the next command the user will run.")));
        var display = string.Join("\n\n", messages.Select(m => $"[{m.Role}]\n{m.Content}"));
        return (messages, display);
    }

    // Tab cycles through completion candidates: history commands matching the whole input first, then entries of the current directory whose name starts with the last space-separated token. One extra step wraps back to the original text.
    private void CycleTabCompletion()
    {
        if (_tabCandidates == null)
        {
            _tabBase = InputBox.Text;
            _tabCandidates = BuildTabCandidates(_tabBase);
            _tabPos = -1;
            if (_tabCandidates.Count == 0)
            {
                _tabCandidates = null;
                return;
            }
        }
        _tabPos = (_tabPos + 1) % (_tabCandidates.Count + 1);
        var candidates = _tabCandidates;
        SetInputTextPreservingTabCycle(_tabPos == candidates.Count ? _tabBase : candidates[_tabPos]);
    }

    private void SetInputTextPreservingTabCycle(string text)
    {
        var candidates = _tabCandidates;
        int pos = _tabPos;
        string baseText = _tabBase;
        SetInputText(text);
        _tabCandidates = candidates;
        _tabPos = pos;
        _tabBase = baseText;
    }

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
        if (cwd.Length > 0)
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

    // Cached briefly because this runs on every keystroke for the ghost text.
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

    private void CycleHistory(bool older)
    {
        _tabCandidates = null;
        if (_cycleMatches == null)
        {
            _cyclePrefix = InputBox.Text;
            _cycleMatches = _history.Match(_cyclePrefix);
            _cyclePos = -1;
        }
        if (_cycleMatches.Count == 0)
        {
            return;
        }
        if (older)
        {
            _cyclePos = Math.Min(_cyclePos + 1, _cycleMatches.Count - 1);
        }
        else
        {
            _cyclePos--;
        }
        if (_cyclePos < 0)
        {
            _cyclePos = -1;
            SetInputText(_cyclePrefix);
            return;
        }
        SetInputText(_cycleMatches[_cyclePos]);
    }

    // --- raw keystroke mode ---

    private void Term_KeyDown(object sender, KeyEventArgs e)
    {
        if (!RawMode || Active is not { Dead: false, Session: { } session })
        {
            return;
        }
        var encoded = InputEncoder.Encode(e.Key, Keyboard.Modifiers);
        if (encoded != null)
        {
            e.Handled = true;
            session.SendInput(encoded);
            Term.ScrollToBottom();
        }
    }

    private void Term_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (!RawMode || Active is not { Dead: false, Session: { } session } || e.Text.Length == 0)
        {
            return;
        }
        session.SendText(e.Text);
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
        CreateTab(NewTabInfo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), activate: true);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private SettingsWindow OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings, id => _tabs.Any(t => t.Info.Id == id), AddShellTab, _predictor) { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
        return _settingsWindow;
    }

    private void ApplySettings()
    {
        foreach (var tab in _tabs)
        {
            tab.Parser.ShowEscapes = _settings.ShowEscapeSequences;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
