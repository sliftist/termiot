using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Termiot.Ui;

// Reusable type-to-filter dropdown: the list filters live as you type (prefix matches ranked before substring matches), Enter commits the highlighted item (or the first one), Tab / Shift+Tab move the highlight forward / back, Escape closes. Feed it items with SetItems; Committed fires with the chosen value.
public sealed class AutoCompleteBox : Grid
{
    private const int MaxVisibleItems = 12;

    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private List<string> _items = new();
    private bool _settingText;

    public event Action<string>? Committed;

    public string Text
    {
        get => _input.Text;
        set
        {
            _settingText = true;
            _input.Text = value;
            _input.CaretIndex = value.Length;
            _settingText = false;
        }
    }

    public AutoCompleteBox()
    {
        _input = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontFamily = new FontFamily("Consolas"),
        };
        _list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontFamily = new FontFamily("Consolas"),
            MaxHeight = 240,
        };
        _popup = new Popup
        {
            PlacementTarget = _input,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = _list,
        };
        Children.Add(_input);
        Children.Add(_popup);

        FocusSelectAll.Attach(_input);
        _input.TextChanged += (_, _) =>
        {
            if (!_settingText)
            {
                RefreshList();
            }
        };
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.LostKeyboardFocus += (_, _) => _popup.IsOpen = false;
        _list.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_list.SelectedItem is string item)
            {
                Commit(item);
            }
        };
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items = items.ToList();
    }

    private void RefreshList()
    {
        string filter = _input.Text;
        var matches = _items
            .Where(i => i.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
            .Take(MaxVisibleItems)
            .ToList();
        _list.ItemsSource = matches;
        _list.Width = Math.Max(_input.ActualWidth, 200);
        _popup.IsOpen = matches.Count > 0;
        if (matches.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                RefreshList();
            }
            return;
        }
        int count = _list.Items.Count;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_list.SelectedItem is string selected)
                {
                    Commit(selected);
                }
                else if (count > 0)
                {
                    Commit((string)_list.Items[0]);
                }
                break;
            case Key.Tab:
            {
                e.Handled = true;
                int delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                _list.SelectedIndex = ((_list.SelectedIndex + delta) % count + count) % count;
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            }
            case Key.Down:
                e.Handled = true;
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case Key.Up:
                e.Handled = true;
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case Key.Escape:
                e.Handled = true;
                _popup.IsOpen = false;
                break;
        }
    }

    private void Commit(string value)
    {
        Text = value;
        _popup.IsOpen = false;
        Committed?.Invoke(value);
    }
}
