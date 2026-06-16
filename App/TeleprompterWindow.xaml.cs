using System.Windows;

namespace ReactionStudio;

public partial class TeleprompterWindow : Window
{
    public TeleprompterWindow()
    {
        InitializeComponent();
    }

    public void SetScript(string text) =>
        Dispatcher.Invoke(() => ScriptText.Text = text);
}
