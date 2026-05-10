using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace GamePartyHud.Hud;

public partial class MemberCard : UserControl
{
    private static readonly GridLength Zero = new(0);
    private static readonly GridLength OneStar = new(1, GridUnitType.Star);
    private static readonly GridLength TwoStar = new(2, GridUnitType.Star);

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
        // Spec proportions:
        //   HP only             → HP takes the full bar
        //   HP + one other      → HP = 2/3, other = 1/3
        //   HP + Stamina + Mana → 1/3 each
        // Achieved with GridLength.Star ratios: HpRow flexes between 1* (alone or 3 bars)
        // and 2* (exactly one of Stamina/Mana present).
        int otherCount = (m.HasStamina ? 1 : 0) + (m.HasMana ? 1 : 0);
        HpRowDef.Height      = otherCount == 1 ? TwoStar : OneStar;
        StaminaRowDef.Height = m.HasStamina    ? OneStar : Zero;
        ManaRowDef.Height    = m.HasMana       ? OneStar : Zero;
    }
}
