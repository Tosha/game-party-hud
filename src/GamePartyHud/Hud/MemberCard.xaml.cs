using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace GamePartyHud.Hud;

public partial class MemberCard : UserControl
{
    private static readonly GridLength Zero = new(0);
    private static readonly GridLength OneStar = new(1, GridUnitType.Star);
    private static readonly GridLength TwoStar = new(2, GridUnitType.Star);
    private static readonly GridLength ThreeStar = new(3, GridUnitType.Star);

    private HudMember? _boundMember;

    public MemberCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// `RowDefinition.Height` does not respond to its child's Visibility in WPF —
    /// a collapsed child still occupies its row's allocated `*` share. We flip
    /// the row heights here so a Stamina-less or Mana-less sender's bar block
    /// collapses to just the visible rows.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundMember is not null)
        {
            _boundMember.PropertyChanged -= OnMemberPropertyChanged;
        }

        _boundMember = e.NewValue as HudMember;

        if (_boundMember is not null)
        {
            _boundMember.PropertyChanged += OnMemberPropertyChanged;
            UpdateRowHeights(_boundMember);
        }
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is HudMember m &&
            (e.PropertyName == nameof(HudMember.HasStamina) || e.PropertyName == nameof(HudMember.HasMana)))
        {
            UpdateRowHeights(m);
        }
    }

    private void UpdateRowHeights(HudMember m)
    {
        // Spec proportions (HP always dominates the bar):
        //   HP only             → HP 100%
        //   HP + one other      → HP 75%, other 25%  (3 : 1 star ratio)
        //   HP + Stamina + Mana → HP 50%, S 25%, M 25%  (2 : 1 : 1 star ratio)
        // The HP row's star factor swings 1 → 3 → 2 depending on how many other
        // bars are present, so HP is never less than half the height.
        int otherCount = (m.HasStamina ? 1 : 0) + (m.HasMana ? 1 : 0);
        HpRowDef.Height = otherCount switch
        {
            2 => TwoStar,
            1 => ThreeStar,
            _ => OneStar,
        };
        StaminaRowDef.Height = m.HasStamina ? OneStar : Zero;
        ManaRowDef.Height    = m.HasMana    ? OneStar : Zero;
    }
}
