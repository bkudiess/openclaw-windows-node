---
name: windows-ui
description: Automate the Windows desktop UI from PowerShell using the built-in UI Automation framework (`System.Windows.Automation` / UIA). Enumerate top-level windows, find controls by AutomationId / Name / ControlType, click via InvokePattern or SelectionItemPattern, type into text boxes via ValuePattern or SendWait, focus and resize windows, and read window titles. Use when the user says "click the X button", "type into Notepad", "open the File menu", "find a window titled Y", "automate this dialog", "screenshot the current window", or any Windows desktop GUI automation that does not have a CLI. Works out of the box on Windows (no install). For heavy-duty automation, FlaUI (UIA3 NuGet wrapper) is recommended — documented below.
metadata:
  openclaw:
    emoji: "🪟"
    os: ["win32"]
    requires:
      bins: ["pwsh"]
---

# Windows UI Automation

Drive the Windows desktop UI from PowerShell using **UI Automation (UIA)** — the same accessibility framework screen readers use. It ships with .NET on every supported Windows; no install required.

This is the Windows analog of the macOS `peekaboo` skill: enumerate windows, find elements, click, type, take screenshots. For screenshots specifically, prefer the dedicated `snipping-tool` skill if it's available; this skill focuses on **interaction** (click / type / focus / inspect).

## When to Use

✅ **USE this skill when:**

- User says "click the Save button", "open the File menu", "press OK in that dialog"
- User says "type ... into Notepad / Calc / <app>"
- User says "find a window titled ...", "focus the X window", "list open windows"
- User says "automate this dialog", "fill in this form"
- The app has **no CLI / no API** — the only handle is the GUI

## When NOT to Use

❌ **DON'T use this skill when:**

- The app has a real CLI or REST API — use that instead (`gh`, `winget`, `mgc`, etc.)
- User wants to drive a **web page** in a browser — use Playwright / Selenium
- User wants to record a macro for replay — Power Automate Desktop is better
- The target is a **DirectX / OpenGL game** or other custom-rendered surface — UIA can't see those elements
- macOS — use `peekaboo`

## Setup

Nothing to install — `UIAutomationClient.dll` and `UIAutomationTypes.dll` are part of .NET on Windows. Load them at the top of every script:

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms   # only if you'll use SendWait
```

## Core building blocks

### Get the root and enumerate windows

```powershell
$root = [System.Windows.Automation.AutomationElement]::RootElement

$windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)

$windows = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $windowCondition)

$windows | ForEach-Object {
    [pscustomobject]@{
        ProcessId = $_.Current.ProcessId
        Title     = $_.Current.Name
        Class     = $_.Current.ClassName
        Bounds    = $_.Current.BoundingRectangle
    }
}
```

### Find a specific window by title

```powershell
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "Untitled - Notepad")
$ctlCond  = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)
$combined = New-Object System.Windows.Automation.AndCondition($nameCond, $ctlCond)

$notepad = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $combined)
```

For a partial / fuzzy match, enumerate all windows and filter with `-match` on `.Current.Name`.

### Find a child element

Three identifiers, in order of robustness:

1. **AutomationId** — the developer-supplied stable id. Most reliable. Inspect with **Accessibility Insights for Windows** or **inspect.exe** from the Windows SDK.
2. **Name** — the visible label. Locale-sensitive.
3. **ClassName** — Win32 class. Brittle across versions.

```powershell
$byId = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "1001")

$btn = $notepad.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
```

`TreeScope::Descendants` walks the whole subtree — slow on huge windows (Word, Excel). Use `Children` then drill in one level at a time when performance matters.

## Acting on elements (patterns)

UIA exposes capabilities through **control patterns** rather than universal methods. Check what's supported, then use the right pattern.

```powershell
$btn.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
```

### Click a button — InvokePattern

```powershell
$invoke = $btn.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
```

### Toggle a checkbox — TogglePattern

```powershell
$toggle = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$toggle.Toggle()
```

### Select a list item / tab — SelectionItemPattern

```powershell
$sel = $item.GetCurrentPattern(
    [System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
```

### Set a text box — ValuePattern (preferred over typing)

`ValuePattern.SetValue` writes the whole string atomically — no key-by-key races, no IME quirks. Always prefer this over `SendWait` if the control supports it.

```powershell
$val = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$val.SetValue("hello from UIA")
```

### Type into a focused control — SendWait fallback

For controls that don't expose `ValuePattern` (some rich-edit boxes, terminals):

```powershell
$tb.SetFocus()
Start-Sleep -Milliseconds 100
[System.Windows.Forms.SendKeys]::SendWait("Hello{ENTER}")
```

Caveats:

- `SendKeys` types into **whatever has focus**, not your `$tb` reference. Make sure focus stuck.
- The following characters are **modifier prefixes**, not literals: `+` = Shift, `^` = Ctrl, `%` = Alt, `~` = Enter. Bare `+a` sends `Shift+a` (capital A), `^s` is `Ctrl+S`. To send a literal `+` `^` `%` `~` `(` `)` `{` `}` `[` `]`, wrap in braces, e.g. `"{+}"` for a literal plus.
- **Never feed user-supplied text directly into `SendWait`.** Pre-process by braces-escaping every reserved character or you'll silently fire hotkeys (e.g. a stray `^o` opens a file dialog).
- Newline is `{ENTER}`, tab is `{TAB}`, escape is `{ESC}`.

### Focus / activate a window — WindowPattern

```powershell
$win = $notepad.GetCurrentPattern(
    [System.Windows.Automation.WindowPattern]::Pattern)
$win.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
$notepad.SetFocus()
```

### Resize / move — TransformPattern

```powershell
$xf = $notepad.GetCurrentPattern(
    [System.Windows.Automation.TransformPattern]::Pattern)
if ($xf.Current.CanMove) { $xf.Move(100, 100) }
if ($xf.Current.CanResize) { $xf.Resize(1024, 768) }
```

## Worked example — open Notepad, type, save

This is the canonical end-to-end test:

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

# 1. Launch Notepad and wait for its main window
$np = Start-Process notepad -PassThru
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)

$window = $null
for ($i = 0; $i -lt 50 -and -not $window; $i++) {
    Start-Sleep -Milliseconds 100
    $window = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $pidCond)
}
if (-not $window) { throw "Notepad window never appeared" }

# 2. Find the edit area (Notepad's editor exposes ValuePattern). The Document
#    element may not appear immediately — retry the same way we waited for the
#    window.
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Document)

$edit = $null
for ($i = 0; $i -lt 50 -and -not $edit; $i++) {
    Start-Sleep -Milliseconds 100
    $edit = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $editCond)
}
if (-not $edit) { throw "Notepad edit Document element never appeared" }

# 3. Write text atomically
$val = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$val.SetValue("Hello from UIA at $(Get-Date -Format o)`r`nLine two")

# 4. Save with Ctrl+S, then handle the Save As dialog
$edit.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait("^s")
Start-Sleep -Milliseconds 500

$saveAs = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Save As")))
# ... locate the File-name box (AutomationId 1001 on classic dialog), SetValue, Invoke "Save"
```

## FlaUI — recommended next step for serious automation

The built-in `System.Windows.Automation` API is verbose and only exposes the older UIA2 surface. For real production work, install **FlaUI** (a UIA3 wrapper) into a .NET project:

```powershell
# In a .NET project directory:
dotnet add package FlaUI.UIA3
```

FlaUI gives you:

- Modern fluent API: `app.GetMainWindow(automation).FindFirstDescendant(cf => cf.ByAutomationId("1001"))`
- UIA3 (newer Windows, better property coverage)
- Higher-level patterns (`AsButton().Click()`, `AsTextBox().Enter("text")`)
- Built-in wait helpers, retry loops, screenshot capture

PowerShell can consume FlaUI too via `Add-Type -Path FlaUI.Core.dll, FlaUI.UIA3.dll` — but the C# / F# surface is cleaner. Reach for FlaUI once you outgrow the snippets in this file.

## Diagnostics: inspecting the UI tree

When automating an app for the first time, you need an inspector:

- **Accessibility Insights for Windows** (Microsoft, free) — best modern option. Live highlights, pattern listings, AutomationId reveal.
- **inspect.exe** — ships with the Windows SDK (`%WindowsSdkDir%\bin\<ver>\x64\inspect.exe`). Older but always available.
- **FlaUInspect** (FlaUI's inspector) — useful if you're already in FlaUI land.

Workflow: open the inspector, hover the target control, note its `AutomationId` and `ControlType`, then write the `PropertyCondition` against those.

## Safety Rules

1. **Confirm before destructive UI clicks.** Buttons labeled "Delete", "Send", "Submit", "Format Drive", "Buy Now", "OK" inside a "Confirm permanent deletion" dialog — *always* read the surrounding window title back to the user first.
2. **Don't blindly invoke unknown buttons.** Read the button's `Name` first; if it's empty, refuse and ask the user for an `AutomationId` instead.
3. **`SendKeys.SendWait` types into whatever has focus.** If your `SetFocus()` failed silently, your text lands somewhere unexpected — possibly a password field, a chat window, or a terminal that runs commands. Verify focus stuck by re-reading the focused element after `SetFocus`:
   ```powershell
   $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
   if ($focused.Current.AutomationId -ne $expected) { throw "Focus did not land where expected" }
   ```
4. **No global hotkeys.** Avoid `^c` (copy) without confirming the user understands you'll overwrite the clipboard.
5. **Don't loop click-find at high frequency.** A tight `FindFirst` loop can spike CPU and freeze the target app. Sleep ≥50 ms between retries.
6. **Stay out of other users' sessions.** UIA only sees your interactive session's windows — but check `ProcessId` against trusted apps before driving them.

## Notes

- **Permissions.** Some elevated apps (UAC-prompted) are not accessible from a non-elevated PowerShell. Re-run elevated if you see empty trees on an admin app.
- **WPF / WinForms / Win32** all expose UIA — they just expose different properties. WPF tends to have rich `AutomationId`s; classic Win32 dialogs often only expose `Name` and numeric `AutomationId` like `"1"`, `"2"`.
- **UWP / WinUI3** apps are also UIA-discoverable but live under their app frame host — search descendants of the `Window` named after the app.
- **Threading.** UIA calls are blocking on the UI; if you script against an app that's modal-dialog-spammy, expect long waits or use cached patterns (`CacheRequest`).
- **Screenshots.** For capture, prefer the dedicated `snipping-tool` skill or `[System.Drawing.Graphics]::CopyFromScreen(...)`. UIA can give you a control's `BoundingRectangle` to clip to.
- **Headless = no.** UIA needs an interactive desktop session. RDP works; Session 0 / scheduled tasks under SYSTEM do not see user apps.
