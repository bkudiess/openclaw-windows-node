---
name: windows-notifications
description: Show Windows 10/11 toast notifications from PowerShell using the BurntToast PSGallery module. Use when the user asks to "ping me when this finishes", "send me a desktop notification", "show a Windows toast", "notify me on completion", or wants buttons / images / deep links in their notification. Covers BurntToast install, simple toasts, toasts with images and action buttons, deep-linking to apps or URLs, reading history (Get-BTHistory), clearing notifications (Remove-BTNotification), and a worked example that fires a real toast.
metadata:
  openclaw:
    emoji: "🔔"
    os: ["win32"]
    requires:
      bins: ["pwsh"]
      psModules: ["BurntToast"]
---

# Windows Notifications (BurntToast)

Use the **BurntToast** PowerShell module to show Windows 10/11 toast notifications. Toasts are the right primitive when a long-running task finishes, when something needs the user's attention without stealing focus, or when you want a clickable deep-link to launch an app or URL.

## When to Use

✅ **USE this skill when:**

- User says "ping me when this finishes" / "notify me when done"
- User wants a desktop pop-up with a button that opens a URL or app
- User wants progress / completion feedback for a background script
- User wants to clear or list previously-shown toasts

## When NOT to Use

❌ **DON'T use this skill when:**

- User wants a modal dialog blocking their work → use `MessageBox` via `[System.Windows.Forms.MessageBox]`
- User wants an inline shell prompt → just write to stdout
- User is on macOS → use `terminal-notifier`, `osascript -e 'display notification'`, or the relevant macOS skill
- Sending to a phone / chat → use the appropriate channel plugin (Slack, Discord, iMessage, Telegram)

## Setup

Install BurntToast once per user from PSGallery — no admin required:

```powershell
Install-Module -Name BurntToast -Scope CurrentUser -Force
Import-Module BurntToast
```

Verify:

```powershell
Get-Module BurntToast -ListAvailable | Select-Object Name, Version
```

If PSGallery is untrusted on this machine, either trust it once:

```powershell
Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
```

…or override the prompt for one call:

```powershell
Install-Module -Name BurntToast -Scope CurrentUser -Force -Confirm:$false
```

`-Force` alone on `Install-Module` does **not** bypass the untrusted-repo prompt — combine `-Force -Confirm:$false`, or trust the repo first.

## Simple Toast

```powershell
New-BurntToastNotification -Text 'Build finished', 'All tests passed in 42s.'
```

Two-element `-Text` array becomes title + body. Single element becomes title only.

## Toast With an Image

```powershell
New-BurntToastNotification `
    -Text 'Coffee break', 'Pomodoro complete.' `
    -AppLogo 'C:\path\to\icon.png'
```

`-AppLogo` is the small round icon (52x52). For a hero banner, build the toast with `New-BTImage -HeroImage`.

## Toast With Action Buttons (deep links)

Buttons can launch a URL, a `myapp://` protocol handler, or another script.

```powershell
$open  = New-BTButton -Content 'Open repo'   -Arguments 'https://github.com/openclaw/openclaw' -ActivationType Protocol
$skip  = New-BTButton -Content 'Dismiss'     -Arguments 'dismiss'
New-BurntToastNotification `
    -Text 'OpenClaw', 'A new release is available.' `
    -Button $open, $skip
```

`-ActivationType Protocol` runs the URL through the default protocol handler — works for `https://`, `mailto:`, `vscode://`, custom schemes registered by installed apps, etc.

## Identifying / Updating / Clearing a Toast

Give the toast a **tag** so you can update or remove it later:

```powershell
New-BurntToastNotification `
    -Text 'Backup', 'Uploading… 30%' `
    -UniqueIdentifier 'backup-job'

# Later, replace it with a new one under the same tag
New-BurntToastNotification `
    -Text 'Backup', 'Done.' `
    -UniqueIdentifier 'backup-job'

# Or remove it
Remove-BTNotification -Tag 'backup-job'
```

`-UniqueIdentifier` maps to both the `Tag` and `Group` on the underlying toast notification.

## Reading Notification History

```powershell
Get-BTHistory | Select-Object Tag, Group, ExpirationTime
```

> **Caveat:** `Get-BTHistory` issues a warning that "the output from this function in some versions of PowerShell is not useful." The `Tag` / `Group` / `ExpirationTime` columns are reliable; `Content` may be empty depending on PS version. Use it to know **which** toasts are live, not to reconstruct their text.

## Worked Example — Long-Running Task with Toast on Completion

```powershell
Import-Module BurntToast

$tag = 'long-job'
New-BurntToastNotification -Text 'Job started', 'Running…' -UniqueIdentifier $tag

try {
    # ... do the work ...
    Start-Sleep -Seconds 5

    $openLog = New-BTButton -Content 'View log' `
        -Arguments "$env:TEMP\job.log" -ActivationType Protocol

    New-BurntToastNotification `
        -Text 'Job complete', "Finished in 5s." `
        -Button $openLog `
        -UniqueIdentifier $tag
}
catch {
    New-BurntToastNotification `
        -Text 'Job failed', $_.Exception.Message `
        -UniqueIdentifier $tag
    throw
}
```

The completion toast **replaces** the "started" toast because both share `-UniqueIdentifier $tag`.

## Clear All Toasts

```powershell
# All toasts produced by the current AppId (PowerShell host by default)
Remove-BTNotification
```

Pass `-Tag` / `-Group` to scope to a specific toast.

## Custom AppId (advanced)

Toasts inherit the PowerShell host's `AppId`, which shows up as the source name in Action Center. **BurntToast 1.x does not ship a `New-BTAppId` cmdlet** and `New-BurntToastNotification` has no `-AppId` parameter — to brand toasts as a specific app you have to register a Start-menu shortcut whose target carries the AppUserModelID, then launch toasts from that AppId by writing the raw XML through `New-BTContent` + `Submit-BTNotification` (or shell into `[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)` directly).

The minimal flow:

```powershell
# 1. Register a Start-menu shortcut whose target carries our AppUserModelID.
$appId   = 'OpenClaw.Skill.Demo'
$lnkPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\OpenClawDemo.lnk'
New-BTShortcut -AppId $appId -ShortcutPath $lnkPath -DisplayName 'OpenClaw Demo' `
               -ExecutablePath (Get-Process -Id $PID).Path

# 2. Build the toast and submit it under that AppId via the WinRT API.
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime] | Out-Null

$content = New-BTContent -Visual (New-BTVisual -BindingGeneric (New-BTBinding -Children (New-BTText -Text 'Hello from OpenClaw')))
$xml     = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($content.GetContent())
$toast   = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
```

Until the shortcut exists, toasts attributed to your custom AppId are silently dropped — register the shortcut first, sign out / sign back in (or wait a few seconds for the AppUserModelID to register), then call `Show($toast)`.

## Safety Rules

1. **Do not spam toasts.** Coalesce repeated events under a single `-UniqueIdentifier`; rapid-fire toasts are intrusive and Windows may rate-limit you.
2. **Sanitize text.** Toast bodies render plain text, but if you ever extend to `-Header` / XML payloads, escape `<`, `>`, `&` first.
3. **Validate URLs for buttons.** `-ActivationType Protocol` will happily launch `cmd:` / `powershell:` style schemes registered by malware. Stick to known schemes (`https`, `mailto`, `vscode`, your own app).
4. **Toasts require an interactive session.** Scheduled tasks running under `SYSTEM` or with "Run whether user is logged on or not" cannot show toasts — the user sees nothing. Run as the interactive user (see `scheduled-tasks` skill).
5. **Focus Assist suppresses toasts.** If the user has Do Not Disturb / Focus Assist on, your toast goes straight to Action Center without a banner. That is by design — do not work around it.

## Notes

- Windows 10 1809+ and Windows 11 supported. Earlier builds need the legacy ToastTemplateType API.
- BurntToast is an active community module, MIT-licensed. Source: https://github.com/Windos/BurntToast.
- For PowerShell 5.1 (Windows PowerShell) the install command is identical.
- If toasts silently do nothing, check: Settings → System → Notifications → "Get notifications from apps and other senders" is **on**, and the PowerShell / your custom AppId entry is not muted.
