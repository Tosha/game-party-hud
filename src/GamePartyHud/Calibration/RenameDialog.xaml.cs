using System.Windows;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public partial class RenameDialog : FluentWindow
{
    public string? Value { get; private set; }

    public RenameDialog(string initial)
    {
        InitializeComponent();
        Input.Text = initial;
        Input.SelectAll();
        Loaded += (_, _) => Input.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Value = Input.Text.Trim();
        DialogResult = !string.IsNullOrWhiteSpace(Value);
    }
}
