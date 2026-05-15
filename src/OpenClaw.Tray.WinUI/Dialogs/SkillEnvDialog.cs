using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Minimal ContentDialog that prompts for an env-var value (e.g. an API key) for a skill.
/// Mirrors the role of <c>EnvEditorView</c> in <c>apps/macos/Sources/OpenClaw/SkillsSettings.swift</c>.
/// </summary>
public sealed class SkillEnvDialog : ContentDialog
{
    private readonly PasswordBox _input;

    public string EnteredValue => _input.Password;

    public SkillEnvDialog(string skillName, string envKey, string? homepage)
    {
        Title = $"Set {envKey}";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{skillName} needs {envKey} to run.",
            FontSize = 13,
        });

        if (!string.IsNullOrEmpty(homepage) && Uri.TryCreate(homepage, UriKind.Absolute, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            var link = new HyperlinkButton
            {
                Content = "Get your key →",
                NavigateUri = url,
                Padding = new Thickness(0),
            };
            stack.Children.Add(link);
        }

        _input = new PasswordBox
        {
            PlaceholderText = envKey,
            Width = 320,
        };
        stack.Children.Add(_input);

        stack.Children.Add(new TextBlock
        {
            Text = $"Saved to openclaw.json under skills.entries.{skillName}.env.{envKey}.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        Content = stack;
    }
}
