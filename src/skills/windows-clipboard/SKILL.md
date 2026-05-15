---
name: windows-clipboard
description: Read and write the Windows clipboard from PowerShell — plain text, images, and file drop lists. Use when the user asks to "copy this to my clipboard", "what's on my clipboard", "grab the screenshot from my clipboard and save it", "clear my clipboard", "paste the file list I copied", or to round-trip data between apps. Covers Get-Clipboard, Set-Clipboard, the System.Windows.Forms.Clipboard fallback for images and file lists on PowerShell 7, and pointers to Windows clipboard history (Win+V). Windows-only.
metadata:
  openclaw:
    emoji: "📋"
    os: ["win32"]
    requires:
      bins: ["pwsh"]
---

# Windows Clipboard

Read and write the Windows clipboard from PowerShell. The built-in `Get-Clipboard` / `Set-Clipboard` cmdlets cover plain text on both Windows PowerShell 5.1 and PowerShell 7. For images and file drop lists from PowerShell 7, fall back to `System.Windows.Forms.Clipboard` (the `-Format` and `-Path` switches were removed in PS 7).

## When to Use

✅ **USE this skill when:**

- User asks to copy text to the clipboard or read what is on it
- User pastes a screenshot into chat and asks to "save the clipboard image"
- User asks to copy a list of file paths so they can paste into Explorer
- User asks to clear the clipboard
- User asks about the Windows clipboard history (Win+V)

## When NOT to Use

❌ **DON'T use this skill when:**

- You need to capture a fresh screenshot — use the `snipping-tool` skill
- You need to fire a desktop notification — use the `windows-notifications` skill
- The user is on macOS — use `pbcopy`/`pbpaste` or the `peekaboo clipboard` subcommand
- The user wants to sync the clipboard between devices — point them at Windows Settings → System → Clipboard → Sync

## Setup

No install required. PowerShell 7 (`pwsh`) ships these commands; `System.Windows.Forms` and `System.Drawing` are part of the .NET runtime on Windows.

If you only have Windows PowerShell 5.1 (`powershell.exe`), the legacy `Get-Clipboard -Format Image|FileDropList` and `Set-Clipboard -Path` switches are also available and are simpler than the Forms fallback shown below.

## Read and Write Text

```powershell
# Write text
"Hello from PowerShell" | Set-Clipboard

# Read text
Get-Clipboard
# → Hello from PowerShell

# Append (read, mutate, write back)
$current = Get-Clipboard -Raw
"$current`nappended line" | Set-Clipboard
```

`-Raw` preserves newlines without splitting into an array of strings.

## Save a Clipboard Image to a PNG

PS 7 dropped `Get-Clipboard -Format Image`. Use the Forms API instead. This is how to convert a screenshot the user pasted into the chat / app into a file.

```powershell
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
    $img = [System.Windows.Forms.Clipboard]::GetImage()
    $out = Join-Path $env:USERPROFILE "Pictures\clipboard-$(Get-Date -Format yyyyMMdd-HHmmss).png"
    $img.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose()
    Write-Host "Saved $out"
} else {
    Write-Host "Clipboard does not contain an image."
}
```

Tested locally — saves a valid PNG (~150 KB for a full-screen capture).

## Put an Image onto the Clipboard

```powershell
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Image]::FromFile('C:\path\to\image.png')
[System.Windows.Forms.Clipboard]::SetImage($img)
$img.Dispose()
```

## Read a File Drop List (paths copied from Explorer)

When a user copies files in Explorer (`Ctrl+C` on a selection), the clipboard holds a file drop list, not text. Read it like this:

```powershell
Add-Type -AssemblyName System.Windows.Forms

if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
    [System.Windows.Forms.Clipboard]::GetFileDropList()
} else {
    Write-Host "No files on clipboard."
}
```

Returns a `StringCollection` of full paths.

## Put a File Drop List onto the Clipboard

So that pasting in Explorer / a file dialog drops those files.

```powershell
Add-Type -AssemblyName System.Windows.Forms

$paths = New-Object System.Collections.Specialized.StringCollection
$paths.Add('C:\path\to\file1.txt') | Out-Null
$paths.Add('C:\path\to\file2.txt') | Out-Null
[System.Windows.Forms.Clipboard]::SetFileDropList($paths)
```

## Clear the Clipboard

```powershell
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Clipboard]::Clear()
```

`Set-Clipboard -Value $null` also clears text content but does not always remove other formats (image, file list). Prefer `Clipboard::Clear()` for a full reset.

## Inspect What Is on the Clipboard

```powershell
Add-Type -AssemblyName System.Windows.Forms

$data = [System.Windows.Forms.Clipboard]::GetDataObject()
$data.GetFormats()
# e.g. UnicodeText, Text, HTML Format, Rich Text Format, Bitmap, FileDrop, ...
```

Useful when you do not know whether the clipboard holds text, an image, files, or HTML — pick the right reader based on the formats present.

## Windows Clipboard History (Win+V)

Windows 10/11 has a system-wide clipboard history feature reachable via the `Win+V` hotkey. It is opt-in (Settings → System → Clipboard → Clipboard history). There is **no first-party PowerShell cmdlet** that programmatically reads or replays clipboard history entries — the history lives in `clipuser.exe` user state and is intentionally not exposed.

What you can do:

- Toggle the feature: Settings → System → Clipboard → Clipboard history.
- Open the UI: send `Win+V` from any focused app (the user has to pick the entry).
- For headless multi-entry storage, keep your own ring buffer: poll `Get-Clipboard` on a timer and persist results yourself.

## Safety Rules

1. **Treat clipboard content as untrusted input.** It may contain shell metacharacters, paths outside the workspace, or sensitive data the user did not mean to share. Do not feed it directly into `Invoke-Expression`.
2. **Confirm before overwriting.** `Set-Clipboard` and `Clipboard::Clear()` discard whatever was there. Read first, ask, then write.
3. **Be careful with file drop lists.** Pasting them into Explorer triggers a move/copy depending on the destination — confirm the user's intent before dropping the paths into the clipboard.
4. **Do not log clipboard contents** to disk or to chat without the user's say-so. Credentials, 2FA codes, and private addresses commonly transit through the clipboard.

## Notes

- Windows-only. `System.Windows.Forms` is not loaded on .NET on Linux/macOS.
- `System.Windows.Forms.Clipboard` requires an STA thread. PowerShell 7's default host is **MTA**, so the WinForms paths (image / file-drop / inspect) will throw `Current thread must be set to single thread apartment (STA) mode` from a vanilla `pwsh` invocation. The fix is to run the snippet via `pwsh -STA -Command "..."`, or to marshal the call onto an STA thread via `[Threading.Thread]::new(...)` with `SetApartmentState('STA')`. `Get-Clipboard` / `Set-Clipboard` for plain text don't have this requirement on either runtime.
- `Set-Clipboard` does **not** survive PowerShell process exit on its own — the clipboard is owned by the user session, so it persists until something else overwrites it.
- If you need clipboard automation in a service / scheduled task running as `SYSTEM`, the clipboard is not accessible; run as an interactive user instead.
