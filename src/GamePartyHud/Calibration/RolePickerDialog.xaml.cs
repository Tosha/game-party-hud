using System;
using System.Windows;
using GamePartyHud.Party;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public partial class RolePickerDialog : FluentWindow
{
    public Role? Value { get; private set; }

    public RolePickerDialog(Role initial)
    {
        InitializeComponent();
        Combo.ItemsSource = Enum.GetValues<Role>();
        Combo.SelectedItem = initial;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Value = Combo.SelectedItem as Role?;
        DialogResult = Value.HasValue;
    }
}
