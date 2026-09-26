using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace HitCam.Desktop.Views;

/// <summary>
/// A feature on the experiments tab: title, hint, an arrow that shows or hides its settings and a switch that turns it
/// on. The settings (the content) are greyed out while it is off. The look is in FeatureSection.axaml.
/// </summary>
public sealed class FeatureSection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<FeatureSection, string?>(nameof(Title));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<FeatureSection, string?>(nameof(Hint));

    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<FeatureSection, bool>(nameof(IsOn), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<FeatureSection, bool>(nameof(IsExpanded), defaultBindingMode: BindingMode.TwoWay);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }
}
