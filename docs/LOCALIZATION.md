# Localization Guide

OpenClaw Tray uses WinUI `.resw` resource files for localization. Windows automatically selects the correct language based on the OS locale — no user configuration needed.

## Currently Supported Languages

| Language | Locale | Resource File |
|----------|--------|---------------|
| English (US) | `en-us` | `Strings/en-us/Resources.resw` |
| Chinese (Simplified) | `zh-cn` | `Strings/zh-cn/Resources.resw` |

## Adding a New Language

1. **Copy the English resource file** as your starting point:

   ```
   src/OpenClaw.Tray.WinUI/Strings/en-us/Resources.resw
   ```

2. **Create a new folder** for your locale under `Strings/`:

   ```
   src/OpenClaw.Tray.WinUI/Strings/<locale>/Resources.resw
   ```

   Use the standard BCP-47 locale tag in lowercase (e.g., `de-de`, `fr-fr`, `ja-jp`, `ko-kr`, `pt-br`, `es-es`).

3. **Translate the `<value>` elements** — do not change the `name` attributes. Each entry looks like:

   ```xml
   <data name="SettingsSaveButton.Content" xml:space="preserve">
     <value>Save</value>   <!-- ← translate this -->
   </data>
   ```

4. **Keep format placeholders intact.** Some strings use `{0}`, `{1}`, etc. These must remain in the translation:

   ```xml
   <data name="Menu_SessionsFormat" xml:space="preserve">
     <value>Sessions ({0})</value>  <!-- {0} = session count -->
   </data>
   ```

5. **Do not translate resource key names** (the `name` attribute). Only translate `<value>` content.

6. **Submit a pull request** with just your new `Resources.resw` file. No code changes are needed — the build system automatically discovers new locale folders.

## How It Works

### XAML strings (automatic)
Elements with `x:Uid` attributes are automatically matched to resource keys:
```xml
<Button x:Uid="SettingsSaveButton" Content="Save" />
```
Maps to resource key `SettingsSaveButton.Content`.

### C# runtime strings (via LocalizationHelper)
Code uses `LocalizationHelper.GetString("key")` to load strings at runtime:
```csharp
Title = LocalizationHelper.GetString("WindowTitle_Settings");
```

### Language selection
Windows picks the language automatically based on the user's OS display language. No in-app language picker is needed.

## Testing a Language Locally

To test a specific locale without changing your Windows language:

1. Open `src/OpenClaw.Tray.WinUI/App.xaml.cs`
2. Add this line at the top of the `App()` constructor, **before** `InitializeComponent()`:
   ```csharp
   global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
   ```
3. Build and run. Remove the line when done testing.

## Resource Key Naming Conventions

| Pattern | Used For | Example |
|---------|----------|---------|
| `ComponentName.Property` | XAML `x:Uid` bindings | `SettingsSaveButton.Content` |
| `WindowTitle_Name` | Window title bars | `WindowTitle_Settings` |
| `Toast_Name` | Toast notification text | `Toast_NodePaired` |
| `Menu_Name` | Tray menu items | `Menu_Settings` |
| `Status_Name` | Status display text | `Status_Connected` |
| `TimeAgo_Format` | Relative time strings | `TimeAgo_MinutesFormat` |

## Validation

Both resource files must have the **same set of keys**. You can verify with:

```powershell
$en = (Select-String -Path "src\OpenClaw.Tray.WinUI\Strings\en-us\Resources.resw" -Pattern '<data name="' | Measure-Object).Count
$new = (Select-String -Path "src\OpenClaw.Tray.WinUI\Strings\<locale>\Resources.resw" -Pattern '<data name="' | Measure-Object).Count
Write-Host "en-us: $en keys | <locale>: $new keys | Match: $($en -eq $new)"
```
