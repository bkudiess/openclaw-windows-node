using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Onboarding.Widgets;

/// <summary>
/// Reusable theme-aware card with rounded corners and padding.
/// Props: the child <see cref="Element"/> to render inside the card.
/// </summary>
public sealed class OnboardingCard : Component<Element>
{
    public override Element Render()
    {
        return Border(
            Props
        )
        .CornerRadius(12)
        .BackgroundResource("CardBackgroundFillColorDefaultBrush")
        .Padding(20, 20, 20, 20);
    }
}
