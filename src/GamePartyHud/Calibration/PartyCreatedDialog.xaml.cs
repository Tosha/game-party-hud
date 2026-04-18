using System.Windows;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public partial class PartyCreatedDialog : FluentWindow
{
    public PartyCreatedDialog(string partyId)
    {
        InitializeComponent();
        IdText.Text = partyId;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(IdText.Text);
            StatusText.Text = "Copied to clipboard — ready to paste into Discord or wherever.";
        }
        catch
        {
            StatusText.Text = "Couldn't copy to clipboard. Select the text above and press Ctrl+C.";
        }
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
