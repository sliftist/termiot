using System.Windows.Controls;
using System.Windows.Input;

namespace Termiot.Ui;

// Standard "click selects everything" behavior for inputs. The mouse-down interception is required: without it, WPF sets focus on mouse-down and then places the caret on mouse-up, which immediately destroys the selection made in GotKeyboardFocus.
public static class FocusSelectAll
{
    public static void Attach(TextBox box)
    {
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!box.IsKeyboardFocusWithin)
            {
                box.Focus();
                e.Handled = true;
            }
        };
    }
}
