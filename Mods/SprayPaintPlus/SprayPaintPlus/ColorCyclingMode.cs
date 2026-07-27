using System.ComponentModel.DataAnnotations;

namespace SprayPaintPlus
{
    /// <summary>
    /// How the mouse wheel may change a spray can's color.
    ///
    /// The numeric values form a strictness ladder, lowest = strictest, and the
    /// client/server merge is a Math.Min over them so the more restrictive of the two
    /// always wins. See SettingsMerge.EffectiveColorCycling.
    ///
    /// Two rules for anyone editing this enum after release:
    ///
    /// 1. Never rename or reorder a member. StationeersLaunchPad renders the dropdown
    ///    from these members, but BepInEx stores the chosen value by member NAME, so a
    ///    rename silently resets every player who customised it, and a renumber inverts
    ///    the merge.
    /// 2. A fourth value is only safe if it genuinely slots into the ladder. Something
    ///    like "only colors you carry a can for" does not sit on this line and would
    ///    need a different mechanism, not a new member here.
    ///
    /// The [Display] attributes are load-bearing: StationeersLaunchPad reads member
    /// labels via EnumInfo&lt;T&gt;.ValueInfo, which falls back to the raw C# identifier
    /// when the attribute is absent. Without them the dropdown reads "CannotChange".
    /// See Research/Patterns/StationeersLaunchPadSettingsGrouping.md.
    /// </summary>
    public enum ColorCyclingMode
    {
        [Display(Name = "Can cannot change color")]
        CannotChange = 0,

        [Display(Name = "Cycles within paint family")]
        WithinFamily = 1,

        [Display(Name = "Cycles through all colors")]
        AllColors = 2,
    }
}
