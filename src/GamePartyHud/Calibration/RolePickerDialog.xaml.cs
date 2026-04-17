using System;
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Calibration;

public partial class RolePickerDialog : Window
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
