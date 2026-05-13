using OpenClaw.Chat;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.FunctionalUI.Hosting;

namespace OpenClawTray.Chat;

/// <summary>
/// Helper for hosting the <see cref="OpenClawChatRoot"/> FunctionalUI tree
/// inside an existing XAML window/page. The FunctionalUI host renders
/// into a target <see cref="Border"/>
/// rather than replacing <see cref="Window.Content"/>, so the surrounding
/// XAML chrome (TitleBar, NavigationView, popup header, ...) is preserved.
/// </summary>
public static class FunctionalChatHostExtensions
{
    /// <summary>
    /// Builds an "post to UI thread" callback suitable for
    /// <see cref="OpenClawChatDataProvider"/>'s <c>post</c> argument from
    /// the supplied window's dispatcher queue.
    /// </summary>
    public static Action<Action> AsPost(this DispatcherQueue dispatcher) =>
        action =>
        {
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            if (!dispatcher.TryEnqueue(() => action()))
                System.Diagnostics.Debug.WriteLine("Dropped chat UI update because DispatcherQueue rejected the work item.");
        };

    /// <summary>
    /// Mount <see cref="OpenClawChatRoot"/> into <paramref name="target"/>.
    /// Returns an <see cref="IDisposable"/> that releases the FunctionalUI host
    /// when the page/window unloads.
    /// </summary>
    public static IDisposable MountFunctionalChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        var host = new FunctionalHostControl();
        host.Mount(new OpenClawChatRoot(provider, initialThreadId, onReadAloud));
        target.Child = host;
        return new MountedFunctionalHost(target, host);
    }

    private sealed class MountedFunctionalHost(Border target, FunctionalHostControl host) : IDisposable
    {
        public void Dispose()
        {
            host.Dispose();
            if (ReferenceEquals(target.Child, host))
                target.Child = null;
        }
    }
}
