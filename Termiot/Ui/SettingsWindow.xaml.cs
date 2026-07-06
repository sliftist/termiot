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
        ConfigPathBox.Text = settings.OpenRouterConfigPath;
        ContextTokensBox.Text = settings.LlmContextTokens.ToString();
        MultiCountBox.Text = settings.MultiCompleteCount.ToString();
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
            RefreshLlmInfo();
            RefreshDefaultTerminal();
        };
        RefreshDefaultTerminal();
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
        // Most recently used first: output.log's write time is the shell's last activity (falls back to the folder timestamp for shells that never produced output).
        Array.Sort(dirs, (a, b) => LastUsed(b).CompareTo(LastUsed(a)));
        if (dirs.Length == 0)
        {
            ShellsPanel.Children.Add(new TextBlock { Text = "No shells have been run yet.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });
            return;
        }
        foreach (var dir in dirs)
        {
            var id = Path.GetFileName(dir);
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
                Text = string.IsNullOrEmpty(info.Title) ? id : info.Title,
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(status);
            row.Children.Add(title);
            row.Children.Add(cwd);
            row.Children.Add(button);
            ShellsPanel.Children.Add(row);
        }
    }
}
