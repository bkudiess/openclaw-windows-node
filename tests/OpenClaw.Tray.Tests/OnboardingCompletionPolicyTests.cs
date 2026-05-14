using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class OnboardingCompletionPolicyTests
{
    [Fact]
    public void Decide_ReadyWithSetupStillRequired_BlocksCompletion()
    {
        var outcome = OnboardingCompletionPolicy.Decide(OnboardingRoute.Ready, setupStillRequired: true);

        Assert.Equal(OnboardingCompletionOutcome.BlockIncompleteReady, outcome);
    }

    [Fact]
    public void Decide_ReadyWithSetupComplete_AllowsCompletion()
    {
        var outcome = OnboardingCompletionPolicy.Decide(OnboardingRoute.Ready, setupStillRequired: false);

        Assert.Equal(OnboardingCompletionOutcome.Complete, outcome);
    }

    [Fact]
    public void Decide_NonReadyRoute_PreservesExistingCompletionBehavior()
    {
        var outcome = OnboardingCompletionPolicy.Decide(OnboardingRoute.Wizard, setupStillRequired: true);

        Assert.Equal(OnboardingCompletionOutcome.Complete, outcome);
    }
}
