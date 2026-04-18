using System.Windows;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public enum PartyChoice { Create, Join, Skip }

public partial class PartyChoiceDialog : FluentWindow
{
    public PartyChoice Choice { get; private set; } = PartyChoice.Skip;

    public PartyChoiceDialog()
    {
        InitializeComponent();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        Choice = PartyChoice.Create;
        DialogResult = true;
        Close();
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        Choice = PartyChoice.Join;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Choice = PartyChoice.Skip;
        DialogResult = true;
        Close();
    }
}
