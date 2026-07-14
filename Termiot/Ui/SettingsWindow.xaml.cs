using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Termiot.Ui;

public partial class SettingsWindow : Window
{
    private const int ShellListRefreshMs = 2000;

    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private readonly Func<string, bool> _isOpenInWindow;
    private readonly Action<string> _resurrect;
    private readonly LlmPredictor _predictor;
    private readonly DispatcherTimer _refreshTimer;
    private readonly AutoCompleteBox _modelBox;
    private Window? _contextWindow;
    private bool _loaded;
    private bool _modelsRequested;
    private string _buildLogShown = "";
    // Frozen shell display order, computed once on first view so the list doesn't reshuffle live as shells' last-activity times change.
    private List<string>? _shellOrder;

    public SettingsWindow(AppSettings settings, Action onChanged, Func<string, bool> isOpenInWindow, Action<string> resurrect, LlmPredictor predictor)
    {
        _settings = settings;
        _onChanged = onChanged;
        _isOpenInWindow = isOpenInWindow;
        _resurrect = resurrect;
        _predictor = predictor;
        InitializeComponent();
        ShowEscapesBox.IsChecked = settings.ShowEscapeSequences;
        AutoResumeBox.IsChecked = settings.AutoResumeShells;
        WriteLogImmediatelyBox.IsChecked = settings.WriteLogImmediately;
        ReopenOnStartupBox.IsChecked = settings.ReopenOnStartup;
        ShowTabResourcesBox.IsChecked = settings.ShowTabResources;
        SingleRowTabsBox.IsChecked = settings.SingleRowTabs;
        AlwaysWin32InputBox.IsChecked = settings.AlwaysWin32Input;
        SwapEnterBox.IsChecked = settings.SwapEnterSubmit;
        ShowFpsBox.IsChecked = settings.ShowFps;
        ConfigPathBox.Text = settings.OpenRouterConfigPath;
        ContextTokensBox.Text = settings.LlmContextTokens.ToString();
        LlmTriggerBox.IsChecked = settings.LlmTriggerEnabled;
        LlmTriggerPhrasesBox.Text = settings.LlmTriggerPhrases;
        MultiCountBox.Text = settings.MultiCompleteCount.ToString();
        ScrollbackBox.Text = settings.ScrollbackLines.ToString();
        BuildTimeText.Text = BuildInfo.Display;
        _modelBox = new AutoCompleteBox { Text = settings.LlmModel };
        _modelBox.Committed += model =>
        {
            _settings.LlmModel = model;
            _settings.Save();
        };
        ModelBoxHost.Content = _modelBox;
        FocusSelectAll.Attach(ConfigPathBox);
        FocusSelectAll.Attach(ContextTokensBox);
        FocusSelectAll.Attach(LlmTriggerPhrasesBox);
        FocusSelectAll.Attach(ScrollbackBox);
        _loaded = true;
        // Stock WPF never defocuses a TextBox when blank space is clicked (panels aren't focusable), so do it by hand: any unhandled click lands here and clears keyboard focus.
        MouseDown += (_, _) => Keyboard.ClearFocus();
        RefreshShellList();
        RefreshLlmInfo();
        LoadModels();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShellListRefreshMs) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshShellList();
            RefreshWindowList();
            RefreshLlmInfo();
            RefreshDefaultTerminal();
            RefreshCursorExec();
            RefreshStartMenu();
            RefreshContextMenu();
            RefreshClaudeHook();
            RefreshBuildLog();
            RefreshStartupTrace();
            RefreshProfiling();
        };
        RefreshBuildLog();
        RefreshStartupTrace();
        RefreshProfiling();
        RefreshWindowList();
        RefreshDefaultTerminal();
        RefreshCursorExec();
        RefreshStartMenu();
        RefreshContextMenu();
        RefreshClaudeHook();
        BuildHotkeysPanel();
        PreviewKeyDown += HotkeyCapture_PreviewKeyDown;
        _refreshTimer.Start();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _contextWindow?.Close();
        };
    }

    private void ShowEscapesBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.ShowEscapeSequences = ShowEscapesBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private void AutoResumeBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.AutoResumeShells = AutoResumeBox.IsChecked.GetValueOrDefault();
        _settings.Save();
    }

    private const int MinScrollbackLines = 100;
    private const int MaxScrollbackLines = 10_000_000;

    private void ScrollbackBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        if (int.TryParse(ScrollbackBox.Text.Trim(), out int lines) && lines >= MinScrollbackLines && lines <= MaxScrollbackLines)
        {
            _settings.ScrollbackLines = lines;
            _settings.Save();
            _onChanged();
        }
    }

    // Hotkey rebinding: click a gesture button, press the new combo (Esc cancels); ⟲ restores the default. Only overrides are persisted.
    private HotkeyDef? _capturingHotkey;
    private Button? _capturingButton;

    private void BuildHotkeysPanel()
    {
        HotkeysPanel.Children.Clear();
        HotkeysPanel.Children.Add(new TextBlock
        {
            Text = "Click a shortcut, then press the new key combination. Esc cancels; ⟲ restores the default.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var def in Hotkeys.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var gesture = Hotkeys.GestureFor(_settings, def.Id);
            bool overridden = gesture != def.DefaultGesture;
            var reset = new Button
            {
                Content = "⟲",
                Width = 26,
                Margin = new Thickness(6, 0, 0, 0),
                IsEnabled = overridden,
                ToolTip = "Reset to " + def.DefaultGesture,
            };
            var button = new Button
            {
                Content = gesture,
                MinWidth = 140,
                Padding = new Thickness(8, 2, 8, 2),
                FontFamily = new FontFamily("Consolas"),
                Tag = def,
            };
            button.Click += (_, _) =>
            {
                CancelHotkeyCapture();
                _capturingHotkey = def;
                _capturingButton = button;
                button.Content = "press keys…";
            };
            reset.Click += (_, _) =>
            {
                _settings.Hotkeys.Remove(def.Id);
                _settings.Save();
                BuildHotkeysPanel();
            };
            DockPanel.SetDock(button, Dock.Right);
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(reset);
            row.Children.Add(button);
            row.Children.Add(new TextBlock
            {
                Text = def.Label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            HotkeysPanel.Children.Add(row);
        }
    }

    private void CancelHotkeyCapture()
    {
        if (_capturingHotkey != null && _capturingButton != null)
        {
            _capturingButton.Content = Hotkeys.GestureFor(_settings, _capturingHotkey.Id);
        }
        _capturingHotkey = null;
        _capturingButton = null;
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey == null)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Hotkeys.IsModifierKey(key))
        {
            return;
        }
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }
        var def = _capturingHotkey;
        var gesture = Hotkeys.Format(key, Keyboard.Modifiers);
        if (gesture == def.DefaultGesture)
        {
            _settings.Hotkeys.Remove(def.Id);
        }
        else
        {
            _settings.Hotkeys[def.Id] = gesture;
        }
        _settings.Save();
        _capturingHotkey = null;
        _capturingButton = null;
        BuildHotkeysPanel();
    }

    private void LlmTriggerBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.LlmTriggerEnabled = LlmTriggerBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private void LlmTriggerPhrasesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.LlmTriggerPhrases = LlmTriggerPhrasesBox.Text;
        _settings.Save();
    }

    private void MultiCountBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        if (int.TryParse(MultiCountBox.Text.Trim(), out int count) && count >= 1 && count <= 100)
        {
            _settings.MultiCompleteCount = count;
            _settings.Save();
            _onChanged();
        }
    }

    private void WriteLogImmediatelyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.WriteLogImmediately = WriteLogImmediatelyBox.IsChecked.GetValueOrDefault();
        _settings.Save();
    }

    private void ShowTabResourcesBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.ShowTabResources = ShowTabResourcesBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private void SwapEnterBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.SwapEnterSubmit = SwapEnterBox.IsChecked.GetValueOrDefault();
        _settings.Save();
    }

    private void SingleRowTabsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.SingleRowTabs = SingleRowTabsBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private void ShowFpsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.ShowFps = ShowFpsBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private void AlwaysWin32InputBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.AlwaysWin32Input = AlwaysWin32InputBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        _onChanged();
    }

    private static string StartupBatPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Termiot-reopen.bat");

    private void ReopenOnStartupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.ReopenOnStartup = ReopenOnStartupBox.IsChecked.GetValueOrDefault();
        _settings.Save();
        try
        {
            if (_settings.ReopenOnStartup)
            {
                StableLauncher.EnsureNewest();
                File.WriteAllText(StartupBatPath, $"@echo off\r\ncall \"{StableLauncher.BatPath}\" --reopen\r\n");
            }
            else if (File.Exists(StartupBatPath))
            {
                File.Delete(StartupBatPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("settings", "startup entry update failed: " + ex.Message);
        }
    }

    // Opens a folder in Explorer; when selectPath points at a file, opens its containing folder with that file highlighted so the user can open it with whatever they like.
    private static void OpenInExplorer(string folder, string? selectPath = null)
    {
        try
        {
            if (selectPath != null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("settings", "open in explorer failed: " + ex.Message);
        }
    }

    private static Button FolderButton(string content, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Content = content,
            Width = 62,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = tooltip,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void RefreshStartupTrace()
    {
        var text = Termiot.StartupTrace.Format(Environment.NewLine);
        if (StartupTraceBox.Text != text)
        {
            StartupTraceBox.Text = text;
        }
    }

    private void RefreshProfiling()
    {
        var text = Termiot.PerfLog.Format(Environment.NewLine);
        if (ProfilingBox.Text != text)
        {
            ProfilingBox.Text = text;
        }
    }

    private void RefreshBuildLog()
    {
        string text;
        try
        {
            // Shared read: a build triggered from another window's process may be appending to this file right now.
            using var fs = new FileStream(AppPaths.BuildLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            text = sr.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            text = "";
        }
        catch (Exception ex)
        {
            text = "(build log unavailable: " + ex.Message + ")";
        }
        if (text == _buildLogShown)
        {
            return;
        }
        _buildLogShown = text;
        BuildLogBox.Text = text.Length > 0 ? text : "No rebuild has run yet. Click the ⟳ button in a window's title bar to rebuild; its output appears here.";
        BuildLogBox.ScrollToEnd();
    }

    // All windows known on disk, most recent first — running or not — resurrectable like shells, but at the window level.
    private void RefreshWindowList()
    {
        WindowsPanel.Children.Clear();
        string[] files;
        try
        {
            files = Directory.GetFiles(AppPaths.WindowsDir, "*.json");
        }
        catch (Exception ex)
        {
            AppLog.Write("settings", "window list failed: " + ex.Message);
            return;
        }
        var entries = files
            .Select(f => (Id: Path.GetFileNameWithoutExtension(f), State: Termiot.WindowState.Load(Path.GetFileNameWithoutExtension(f)), Written: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(w => w.State.ClosedAtTicks > 0 ? new DateTime(w.State.ClosedAtTicks, DateTimeKind.Utc) : w.Written)
            .ToList();
        if (entries.Count == 0)
        {
            WindowsPanel.Children.Add(new TextBlock { Text = "No windows recorded yet.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });
            return;
        }
        foreach (var entry in entries)
        {
            bool running = entry.State.OwnerPid != 0 && HostInfo.ProcessAlive(entry.State.OwnerPid, entry.State.OwnerStartTicks);
            // LoadTabs applies the same died-near-close filter the reopen path uses, so the list shows exactly what "Reopen" will restore.
            var tabs = entry.State.LoadTabs();
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock
            {
                Text = running ? "● running" : "○ closed",
                Foreground = new SolidColorBrush(running ? Color.FromRgb(0x4E, 0xC9, 0x4E) : Color.FromRgb(0x88, 0x88, 0x88)),
                Width = 70,
                VerticalAlignment = VerticalAlignment.Center,
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"{tabs.Count} tab{(tabs.Count == 1 ? "" : "s")}",
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Width = 60,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var button = new Button
            {
                Content = running ? "Running" : "Reopen",
                IsEnabled = !running,
                Width = 80,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var windowId = entry.Id;
            button.Click += (_, _) =>
            {
                Program.SpawnWindowProcess(windowId);
                RefreshWindowList();
            };
            headerPanel.Children.Add(button);
            headerPanel.Children.Add(new TextBlock
            {
                Text = windowId + ".json",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            });
            var windowFile = AppPaths.WindowFile(windowId);
            headerPanel.Children.Add(FolderButton("Folder", $"Open {AppPaths.WindowsDir} with {windowId}.json selected", () => OpenInExplorer(AppPaths.WindowsDir, windowFile)));

            var tabList = new StackPanel { Margin = new Thickness(24, 2, 0, 4) };
            foreach (var info in tabs)
            {
                var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                tabRow.Children.Add(new TextBlock
                {
                    Text = (info.ForcedTitle.Length > 0 ? info.ForcedTitle : info.Title) + "  " + info.Cwd,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Width = 360,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"{info.Cwd}\nid: {info.Id}",
                });
                var tabShellDir = AppPaths.ShellDir(info.Id);
                tabRow.Children.Add(FolderButton("Folder", $"Open {tabShellDir}", () => OpenInExplorer(tabShellDir)));
                tabList.Children.Add(tabRow);
            }
            var expander = new Expander
            {
                Header = headerPanel,
                Content = tabList,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                Margin = new Thickness(0, 2, 0, 2),
            };
            WindowsPanel.Children.Add(expander);
        }
    }

    private void RefreshDefaultTerminal()
    {
        var state = DefaultTerminal.GetState();
        var text = state.Description;
        if (state.ServerPath.Length > 0)
        {
            text += "\n" + state.ServerPath;
        }
        DefaultTerminalStatus.Text = text;
        MakeDefaultBtn.IsEnabled = !state.IsThisExe;
        MakeDefaultBtn.Content = state.IsThisExe ? "✓ Termiot is the default terminal" : "Make Termiot the default terminal";
    }

    private void MakeDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DefaultTerminal.MakeDefault();
        }
        catch (Exception ex)
        {
            AppLog.Write("defterm", "make default failed: " + ex);
        }
        RefreshDefaultTerminal();
    }

    private void ResetDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DefaultTerminal.ResetDefault();
        }
        catch (Exception ex)
        {
            AppLog.Write("defterm", "reset default failed: " + ex);
        }
        RefreshDefaultTerminal();
    }

    private void RefreshCursorExec()
    {
        if (!CursorIntegration.CursorInstalled)
        {
            CursorExecStatus.Text = "Cursor settings not found (" + CursorIntegration.SettingsPath + ")";
            CursorExecBtn.IsEnabled = false;
            return;
        }
        bool extInstalled = CursorExtension.IsInstalled;
        var current = CursorIntegration.GetCurrent();
        bool isExe = string.Equals(current, StableLauncher.BatPath, StringComparison.OrdinalIgnoreCase);
        CursorExecStatus.Text = (current is { Length: > 0 } ? current : "(windowsExec not set)")
            + (extInstalled ? "\nextension installed — Ctrl+Shift+C goes to the extension" : "");
        // Mutually exclusive: the extension's keybinding overrides windowsExec entirely, so "use the executable" must uninstall it.
        CursorExtBtn.IsEnabled = !extInstalled;
        CursorExtBtn.Content = extInstalled ? "✓ Using the extension (instant, no shell window)" : "Use the extension (instant, no shell window)";
        CursorExecBtn.IsEnabled = extInstalled || !isExe;
        CursorExecBtn.Content = !extInstalled && isExe ? "✓ Using the launcher (auto-updates to newest build)" : "Use the launcher (auto-updates to newest build)";
    }

    private void CursorExtBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CursorExtension.Install();
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor", "extension install failed: " + ex);
        }
        RefreshCursorExec();
    }

    private void CursorExecBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CursorExtension.Uninstall();
            CursorIntegration.SetToStableLauncher();
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor", "switch to executable failed: " + ex);
        }
        RefreshCursorExec();
    }

    private void RefreshContextMenu()
    {
        var current = ExplorerContextMenu.GetCurrentExe();
        bool isThis = string.Equals(current, StableLauncher.BatPath, StringComparison.OrdinalIgnoreCase);
        ContextMenuStatus.Text = current is { Length: > 0 } ? current : "(not installed)";
        ContextMenuBtn.IsEnabled = !isThis;
        ContextMenuBtn.Content = isThis ? "✓ Explorer opens Termiot (via launcher)" : "Add 'Open in Termiot' to Explorer";
        ContextMenuRemoveBtn.IsEnabled = current != null;
    }

    private void ContextMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExplorerContextMenu.Install();
        }
        catch (Exception ex)
        {
            AppLog.Write("explorer", "context menu install failed: " + ex);
        }
        RefreshContextMenu();
    }

    private void ContextMenuRemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExplorerContextMenu.Remove();
        }
        catch (Exception ex)
        {
            AppLog.Write("explorer", "context menu removal failed: " + ex);
        }
        RefreshContextMenu();
    }

    private void RefreshClaudeHook()
    {
        if (!ClaudeIntegration.ClaudeInstalled)
        {
            ClaudeHookStatus.Text = "(Claude Code not found — no ~\\.claude directory)";
            ClaudeHookBtn.IsEnabled = false;
            return;
        }
        bool installed = ClaudeIntegration.IsInstalled;
        ClaudeHookStatus.Text = installed ? ClaudeIntegration.HookPath : "(hook not installed)";
        ClaudeHookBtn.IsEnabled = !installed;
        ClaudeHookBtn.Content = installed ? "✓ Auto-resume hook added to Claude Code" : "Add auto-resume hook to Claude Code";
    }

    private void ClaudeHookBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClaudeIntegration.Install();
        }
        catch (Exception ex)
        {
            AppLog.Write("claude", "hook install failed: " + ex);
        }
        RefreshClaudeHook();
    }

    private void RefreshStartMenu()
    {
        var target = StartMenu.GetCurrentTarget();
        bool isThis = string.Equals(target, StableLauncher.BatPath, StringComparison.OrdinalIgnoreCase);
        StartMenuStatus.Text = target is { Length: > 0 } ? target : "(no Start menu shortcut)";
        StartMenuBtn.IsEnabled = !isThis;
        StartMenuBtn.Content = isThis ? "✓ Start menu opens Termiot (via launcher)" : "Add Termiot to the Start menu";
    }

    private void StartMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartMenu.PointAtStableLauncher();
        }
        catch (Exception ex)
        {
            AppLog.Write("startmenu", "shortcut update failed: " + ex);
        }
        RefreshStartMenu();
    }

    public void SelectLlmTab()
    {
        Tabs.SelectedItem = LlmTab;
    }

    // --- LLM ---

    // Just a convenient way to fill the path input — the input itself stays freely editable.
    private void BrowseConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select OpenRouter config file",
            Filter = "All files (*.*)|*.*",
        };
        if (File.Exists(ConfigPathBox.Text.Trim()))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(ConfigPathBox.Text.Trim());
        }
        if (dialog.ShowDialog(this).GetValueOrDefault())
        {
            ConfigPathBox.Text = dialog.FileName;
        }
    }

    private void ConfigPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        _settings.OpenRouterConfigPath = ConfigPathBox.Text.Trim();
        _settings.Save();
        _modelsRequested = false;
        LoadModels();
    }

    private void ContextTokensBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        if (int.TryParse(ContextTokensBox.Text.Trim(), out int tokens) && tokens > 0)
        {
            _settings.LlmContextTokens = tokens;
            _settings.Save();
        }
    }

    private async void LoadModels()
    {
        if (_modelsRequested || OpenRouter.LoadKey(_settings.OpenRouterConfigPath) == null)
        {
            ModelListStatus.Text = OpenRouter.LoadKey(_settings.OpenRouterConfigPath) == null ? "Set a valid config file to load the live model list." : ModelListStatus.Text;
            return;
        }
        _modelsRequested = true;
        ModelListStatus.Text = "Loading model list…";
        try
        {
            var models = await _predictor.GetModelsAsync();
            _modelBox.SetItems(models.Select(m => m.Id));
            ModelListStatus.Text = $"{models.Count} models loaded — type to filter, Enter selects, Tab cycles.";
        }
        catch (Exception ex)
        {
            _modelsRequested = false;
            ModelListStatus.Text = "Model list failed: " + ex.Message;
            AppLog.Write("settings", "model list failed: " + ex.Message);
        }
    }

    private void RefreshLlmInfo()
    {
        double avgLatency = _settings.LlmRequestCount > 0 ? (double)_settings.LlmTotalLatencyMs / _settings.LlmRequestCount : 0;
        LlmStatsText.Text =
            $"cost:        ${_settings.LlmTotalCostUsd:0.######}\n" +
            $"input tok:   {_settings.LlmInputTokens:n0}\n" +
            $"output tok:  {_settings.LlmOutputTokens:n0}\n" +
            $"requests:    {_settings.LlmRequestCount:n0}\n" +
            $"avg latency: {avgLatency:0}ms" +
            (_predictor.LastError.Length > 0 ? $"\nlast error:  {_predictor.LastError}" : "");
    }

    private void ShowContextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_contextWindow != null)
        {
            _contextWindow.Activate();
            return;
        }
        var box = new TextBox
        {
            Text = _predictor.LastContext.Length > 0 ? _predictor.LastContext : "(no prediction has been made yet)",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8),
        };
        _contextWindow = new Window
        {
            Title = "LLM context",
            Width = 700,
            Height = 500,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            Content = box,
        };
        _contextWindow.Closed += (_, _) => _contextWindow = null;
        _contextWindow.Show();
    }

    private static DateTime LastUsed(string shellDir)
    {
        try
        {
            var logPath = Path.Combine(shellDir, "output.log");
            return File.Exists(logPath) ? File.GetLastWriteTimeUtc(logPath) : Directory.GetLastWriteTimeUtc(shellDir);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void RefreshShellList()
    {
        ShellsPanel.Children.Clear();
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(AppPaths.ShellsDir);
        }
        catch (Exception ex)
        {
            AppLog.Write("settings", "shell list failed: " + ex.Message);
            return;
        }
        var byId = new Dictionary<string, string>();
        foreach (var d in dirs)
        {
            byId[Path.GetFileName(d)] = d;
        }
        if (_shellOrder == null)
        {
            // First view: most recently used first (output.log's write time is the shell's last activity, falling back to the folder timestamp), then frozen so it doesn't reshuffle live.
            Array.Sort(dirs, (a, b) => LastUsed(b).CompareTo(LastUsed(a)));
            _shellOrder = dirs.Select(d => Path.GetFileName(d)!).ToList();
        }
        else
        {
            _shellOrder.RemoveAll(id => !byId.ContainsKey(id));
            // Shells created since the list froze go to the top without disturbing the order of the rest.
            var fresh = byId.Keys.Where(id => !_shellOrder.Contains(id)).ToList();
            fresh.Sort((a, b) => LastUsed(byId[b]).CompareTo(LastUsed(byId[a])));
            _shellOrder.InsertRange(0, fresh);
        }
        if (_shellOrder.Count == 0)
        {
            ShellsPanel.Children.Add(new TextBlock { Text = "No shells have been run yet.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });
            return;
        }
        foreach (var id in _shellOrder)
        {
            var dir = byId[id];
            var info = ShellInfo.Load(id) ?? new ShellInfo();
            bool alive = HostInfo.IsShellAlive(id);
            bool open = _isOpenInWindow(id);

            var status = new TextBlock
            {
                Text = alive ? "● alive" : "○ dead",
                Foreground = new SolidColorBrush(alive ? Color.FromRgb(0x4E, 0xC9, 0x4E) : Color.FromRgb(0x88, 0x88, 0x88)),
                Width = 60,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var title = new TextBlock
            {
                // Prefer the user's renamed title (ForcedTitle) over the automatic one, same as the Windows tab.
                Text = info.ForcedTitle.Length > 0 ? info.ForcedTitle : (string.IsNullOrEmpty(info.Title) ? id : info.Title),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Width = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cwd = new TextBlock
            {
                Text = info.Cwd,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontFamily = new FontFamily("Consolas"),
                Width = 200,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{info.Cwd}\nid: {id}",
            };
            var button = new Button
            {
                Content = open ? "Open here" : (alive ? "Attach" : "Resurrect"),
                IsEnabled = !open,
                Width = 80,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var shellId = id;
            button.Click += (_, _) =>
            {
                _resurrect(shellId);
                RefreshShellList();
            };
            var shellDir = dir;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(status);
            row.Children.Add(title);
            row.Children.Add(cwd);
            row.Children.Add(button);
            row.Children.Add(FolderButton("Folder", $"Open {shellDir}", () => OpenInExplorer(shellDir)));
            ShellsPanel.Children.Add(row);
        }
    }
}
