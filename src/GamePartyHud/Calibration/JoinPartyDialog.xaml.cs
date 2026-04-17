using System.Windows;

namespace GamePartyHud.Calibration;

public partial class JoinPartyDialog : Window
{
    public string? PartyId { get; private set; }

    public JoinPartyDialog(string? prefill = null)
    {
        InitializeComponent();
        Input.Text = prefill ?? "";
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        PartyId = Input.Text.Trim().ToUpperInvariant();
        DialogResult = PartyId.Length > 0;
    }
}
