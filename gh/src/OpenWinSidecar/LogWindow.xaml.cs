using System.Windows;

namespace OpenWinSidecar;

/// <summary>
/// Separate, resizable log viewer so the main Console window can stay compact.
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    /// <summary>Appends a line to the log. Safe to call from any thread.</summary>
    public void Append(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Append(line));
            return;
        }

        TxtLog.AppendText(line);
        // Keep the document bounded: a wrapping TextBox re-layouts its entire content on every
        // append, so an unbounded log made each server log line progressively more expensive.
        if (TxtLog.Text.Length > 500_000)
        {
            var text = TxtLog.Text;
            TxtLog.Text = text.Substring(text.Length - 250_000);
        }
        if (ChkAutoScroll.IsChecked == true)
            TxtLog.ScrollToEnd();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLog.Text))
            System.Windows.Clipboard.SetText(TxtLog.Text);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();
}
