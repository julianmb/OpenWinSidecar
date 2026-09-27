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
            // BeginInvoke, not Invoke: this is called from the streaming and capture threads
            // for every server log line. Invoke blocks the producer until the UI thread
            // drains its queue, so a busy log turned into head-of-line blocking on the
            // capture path.
            Dispatcher.BeginInvoke(() => Append(line));
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

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"openwinsidecar-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".log",
                Title = "Save log"
            };
            if (dlg.ShowDialog(this) != true) return;
            // Write off the UI thread: a 500 KB document write is fast, but there is no
            // reason to block the dispatcher for it.
            var text = TxtLog.Text;
            System.Threading.Tasks.Task.Run(() => System.IO.File.WriteAllText(dlg.FileName, text));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Could not save the log:\n" + ex.Message,
                "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();
}
