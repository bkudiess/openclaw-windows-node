namespace OpenClawTray.Onboarding.Services;

public enum OnboardingCompletionOutcome
{
    Complete,
    BlockIncompleteReady
}

public static class OnboardingCompletionPolicy
{
    public static OnboardingCompletionOutcome Decide(OnboardingRoute currentRoute, bool setupStillRequired) =>
        currentRoute == OnboardingRoute.Ready && setupStillRequired
            ? OnboardingCompletionOutcome.BlockIncompleteReady
            : OnboardingCompletionOutcome.Complete;
}
