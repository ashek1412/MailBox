# MailBox Desktop — Project Memory

## What this is
A standalone WPF .NET 8 desktop email client for Windows.  
Single self-contained `.exe` (no installer, no PHP, no web server).  
Mirrors the Laravel/Livewire web version of MailBox but runs fully offline from local SQLite storage.

---

## Build & Run

```powershell
# SDK is in PATH at C:\Program Files\dotnet\dotnet.exe (v10)
# Project root: F:\dev\dotnet\MailBox

# Debug build (fast, for development)
dotnet build "F:\dev\dotnet\MailBox\MailBox\MailBox.csproj"

# Release single-file publish → F:\dev\dotnet\MailBox\publish\MailBox.exe
dotnet publish "F:\dev\dotnet\MailBox\MailBox\MailBox.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "F:\dev\dotnet\MailBox\publish"

# Restore packages (only needed after csproj changes)
dotnet restore "F:\dev\dotnet\MailBox\MailBox\MailBox.csproj"

# Run the published exe
Start-Process "F:\dev\dotnet\MailBox\publish\MailBox.exe"
```

**Output:** `F:\dev\dotnet\MailBox\publish\MailBox.exe` — ~74 MB, no dependencies needed on target machine.

---

## Project Structure

```
E:\MailBox\
├── CLAUDE.md                        ← this file
├── publish\
│   └── MailBox.exe                  ← latest published binary (~74 MB)
└── MailBox\
    ├── MailBox.csproj
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml / .cs
    ├── Assets\                      ← (placeholder: add mailbox.ico here)
    ├── Models\
    │   ├── AccountModel.cs
    │   ├── EmailModel.cs
    │   ├── AttachmentModel.cs
    │   ├── FolderInfo.cs
    │   └── MailLogModel.cs
    ├── Services\
    │   ├── AppPaths.cs
    │   ├── PasswordVault.cs
    │   ├── AccountRepository.cs
    │   ├── MailDataRepository.cs
    │   ├── ImapSyncService.cs
    │   ├── SmtpSendService.cs
    │   └── BackgroundSyncService.cs
    ├── ViewModels\
    │   ├── MainViewModel.cs
    │   ├── SidebarViewModel.cs      ← also contains AccountItemViewModel, FolderItemViewModel
    │   ├── EmailListViewModel.cs
    │   ├── EmailViewerViewModel.cs
    │   ├── ComposeViewModel.cs      ← also contains RecipientSuggestion record
    │   ├── AccountDialogViewModel.cs
    │   └── LogsViewModel.cs
    ├── Views\
    │   ├── SidebarView.xaml / .cs
    │   ├── EmailListView.xaml / .cs
    │   ├── EmailViewerView.xaml / .cs
    │   ├── LogsView.xaml / .cs
    │   ├── ComposeWindow.xaml / .cs
    │   └── AccountDialog.xaml / .cs
    ├── Converters\
    │   └── Converters.cs
    └── Themes\
        ├── Styles.xaml
        └── Converters.xaml
```

---

## Key NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| MailKit | 4.9.0 | IMAP/SMTP protocol (UID-range sync) |
| Microsoft.Data.Sqlite | 8.0.11 | Per-account SQLite mail storage |
| Dapper | 2.1.35 | Micro-ORM for SQLite queries |
| CommunityToolkit.Mvvm | 8.4.0 | `[ObservableProperty]`, `[RelayCommand]` source generators |
| Microsoft.Extensions.Hosting | 8.0.1 | Generic Host + DI container |
| Microsoft.Web.WebView2 | latest | HTML email rendering + compose editor |
| H.NotifyIcon.Wpf | 2.1.3 | System tray icon (`TaskbarIcon`) |
| HtmlSanitizer | 9.0.873 | Sanitize HTML email bodies before WebView2 display |

---

## Runtime Data Paths

All data lives under `%APPDATA%\MailBox\` (never next to the .exe):

```
%APPDATA%\MailBox\
├── accounts.db          ← SQLite: accounts table + mail_logs table
├── maildata\
│   ├── user@gmail.com.sqlite    ← per-account mail store (emails + attachments)
│   └── work@company.com.sqlite
├── mail\
│   └── {accountId}\{emailId}\  ← attachment binary files
└── logs\
```

**`AppPaths.cs`** provides static helpers:
- `AppPaths.Root` — `%APPDATA%\MailBox`
- `AppPaths.AccountsDb` — accounts.db path
- `AppPaths.MailDataFile(string email)` — per-account .sqlite path
- `AppPaths.AttachmentDir(int accountId, int emailId)` — attachment folder
- `AppPaths.EnsureAll()` — creates all directories (called on startup)

---

## Architecture Overview

### Generic Host + DI (`App.xaml.cs`)
```
Host
 ├── AccountRepository     (singleton)
 ├── ImapSyncService       (singleton)
 ├── SmtpSendService       (singleton)
 ├── BackgroundSyncService (singleton + IHostedService → auto-starts)
 ├── MainViewModel         (singleton)
 └── MainWindow            (singleton)
```
`BackgroundSyncService` runs every 5 minutes, fires `NewMailArrived` event.

### 3-Panel Layout (`MainWindow.xaml`)
```
[SidebarView 240px] | splitter | [EmailListView 340px] | splitter | [ContentControl *]
                                                                          ↓
                                                         DataTemplate routes to:
                                                         - EmailViewerViewModel → EmailViewerView
                                                         - LogsViewModel        → LogsView
                                                         - null                 → "Select an email" empty state
```

### MVVM Pattern
- Uses **CommunityToolkit.Mvvm** source generators
- `[ObservableProperty]` on `_camelCase` backing fields → generates PascalCase public properties
- **CRITICAL**: Always reference generated property (e.g., `Sidebar.Method()`) NOT the backing field (`_sidebar.Method()`) — MVVMTK0034 error
- `[RelayCommand]` on methods → generates `XxxCommand` properties

---

## Models

### `AccountModel.cs`
```csharp
int Id, string Name, Email, ImapHost, ImapPort, ImapEncryption,
string SmtpHost, SmtpPort, SmtpEncryption, Username, EncryptedPassword,
string Color, SyncStateJson, LastSyncedAt, SyncError
bool InitialSyncDone

int LastSyncedUid(string folder)   // parses SyncStateJson JSON
void SetSyncedUid(string folder, int uid)
```

### `EmailModel.cs`
Mirrors SQLite schema. Computed: `IsReadBool`, `IsFlaggedBool`, `HasAttachmentBool`, `DisplaySender`, `AvatarLetter`, `FormattedDate`

### `AttachmentModel.cs`
`int Id, EmailId, long Size, string Filename, MimeType, DiskPath`  
`string FormattedSize` computed. `string AbsolutePath(string storageRoot)`.

### `FolderInfo.cs`
`string FullName, Name, Icon, int UnreadCount`  
`static string IconFor(string fullName)` — emoji icons for INBOX, Sent, Drafts, Trash, Spam, etc.

---

## Services

### `PasswordVault.cs`
Windows **DPAPI** encryption — passwords are machine+user specific.
```csharp
string PasswordVault.Encrypt(string plaintext)  // → base64
string PasswordVault.Decrypt(string base64)     // → plaintext
```
⚠ DPAPI-encrypted passwords from the Laravel app (which uses Laravel Crypt with APP_KEY) are NOT compatible — users must re-enter passwords.

### `AccountRepository.cs`
Manages `accounts.db`:
- Tables: `accounts`, `mail_logs`
- `GetAll()`, `GetById(int)`, `Insert(AccountModel): int`, `Update`, `Delete(int)`
- `UpdateSyncState(int id, string json, string lastSyncedAt, bool initialDone)`
- `InsertLog(MailLogModel)`, `GetLogs(type, status, page, perPage)`, `ClearLogs(...)`

### `MailDataRepository.cs`
Per-account SQLite (path from `AppPaths.MailDataFile(email)`).  
**Identical schema to Laravel's `MailDataService`** — cross-compatible.
```
emails(id, folder, uid, message_id, subject, from_name, from_email,
       to_addresses, cc_addresses, sent_at, is_read, is_flagged,
       has_attachment, body_html, body_text, synced_at)
       UNIQUE(folder, uid)

attachments(id, email_id FK, filename, mime_type, size, disk_path)
```
Key methods: `GetEmails(folder, search, page, perPage)`, `InsertEmail`, `GetAttachments(emailId)`,  
`MarkAs(folder, uid, isRead, isFlagged?)`, `DeleteEmail(int)`, `GetUnreadCount(folder)`,  
`SuggestRecipients(query, ownEmail, limit)`

### `ImapSyncService.cs`
**UID-range incremental sync** using MailKit's `SearchQuery.Uids(new UniqueIdRange(lastUid+1, uint.MaxValue))` — much faster than downloading all headers.

```csharp
Task<SyncResult> SyncAsync(AccountModel, CancellationToken?)
static Task<ImapClient?> ConnectAsync(AccountModel)   // used by sidebar for folder listing
event Action<string>? Progress
```
`BodyCollector : MimeVisitor` (file-scoped class) extracts HTML/text from MIME tree.  
Saves attachment binaries to `AppPaths.AttachmentDir(accountId, emailId)`.

### `SmtpSendService.cs`
```csharp
Task SendAsync(AccountModel, ComposeRequest)
record ComposeRequest(List<string> To, Cc, Bcc, string Subject, Body, bool IsHtml, string? InReplyTo)
```
Logs success/failure to `AccountRepository`.

### `BackgroundSyncService.cs`
Extends `BackgroundService`. 30s initial delay, then every 5 minutes.
```csharp
event Action<AccountModel, int>? NewMailArrived
Task SyncAllAsync(CancellationToken?)  // also callable manually (SyncAll command)
```

---

## ViewModels

### `MainViewModel.cs`
Top-level VM. Owns `Sidebar`, `EmailList`, `RightPanel`.
```csharp
void OnFolderSelected(AccountModel, string folder)  // called by SidebarViewModel
void OnEmailSelected(AccountModel, EmailModel)       // called by EmailListViewModel
Commands: OpenLogsCommand, SyncAllCommand, ShowWindowCommand, ComposeNewCommand, ExitCommand
internal SmtpSendService SmtpService
internal AccountRepository AccountRepo
```

### `SidebarViewModel.cs`
Contains `ObservableCollection<AccountItemViewModel> AccountItems`.  
`AccountItemViewModel` — per-account: avatar, color, expand/collapse folders, sync, edit, delete.  
`FolderItemViewModel` — per-folder: icon, name, unread count, `SelectCommand`.

### `EmailListViewModel.cs`
`LoadEmails(account, folder)`, `Reload()`. Pagination (20/page).  
`event Action<AccountModel, EmailModel>? EmailSelected`

### `EmailViewerViewModel.cs`
Loads from `MailDataRepository`, sanitizes HTML with `HtmlSanitizer`.  
Commands: `ReplyCommand`, `ForwardCommand`, `ToggleFlagCommand`, `DeleteCommand`, `OpenAttachmentCommand`, `SaveAttachmentCommand`.  
Marks email as read in SQLite on open.

### `ComposeViewModel.cs`
```csharp
enum ComposeMode { New, Reply, Forward }
[ObservableProperty] string To, Cc, Bcc, Subject, Body
[ObservableProperty] bool IsSending, HasSuggestions
[ObservableProperty] ObservableCollection<RecipientSuggestion> Suggestions
event Action? SendSucceeded
```
`HasSuggestions` drives the autocomplete `Popup.IsOpen` — set true/false in `SearchRecipientsCommand`.  
`BuildQuote(EmailModel)` — Gmail-style left-border HTML blockquote.

`record RecipientSuggestion(string Email, string Name)` — `Label`, `AvatarLetter` computed.

### `AccountDialogViewModel.cs`
```csharp
public static readonly IReadOnlyList<string> ColorSwatches  // 10 hex colour strings
[ObservableProperty] string Color                            // currently selected colour
Commands: ApplyPresetCommand(string provider), TestConnectionCommand, SaveCommand, CancelCommand, SetColorCommand(string hex)
event Action<bool>? RequestClose
```
Presets: gmail, outlook, yahoo, icloud.

### `LogsViewModel.cs`
Filters: `TypeFilter` (all/imap/smtp), `StatusFilter` (all/success/error). Resets page on filter change.

---

## Views

### `AccountDialog.xaml` / `.cs`
- PasswordBox synced via `PasswordChanged="PasswordBox_PasswordChanged"` → `vm.Password = PasswordBox.Password`
- `RequestClose` event → sets `DialogResult` and closes window
- Colour swatches rendered from `AccountDialogViewModel.ColorSwatches`

### `ComposeWindow.xaml` / `.cs`
- **WebView2** contenteditable HTML editor (body field)
- `InitEditorAsync()` loads minimal HTML page, listens for `bodyChanged` web messages
- `ToBox_TextChanged` → calls `SearchRecipientsCommand`
- `Popup.IsOpen="{Binding HasSuggestions}"` — bool binding, NOT converter

### `EmailViewerView.xaml` / `.cs`
- **WebView2** renders sanitized HTML body
- `InitWebViewAsync()` blocks external HTTP requests (privacy)
- `NavigationStarting` cancels navigation, opens links in system browser

### `LogsView.xaml` / `.cs`
- `FilterPill` RadioButton style for type/status filters
- Code-behind `TypeFilter_Click` / `StatusFilter_Click` set VM properties

---

## Converters (Converters.cs + Themes/Converters.xaml)

| Key | Class | Notes |
|-----|-------|-------|
| `BoolToVisibility` | BoolToVisibilityConverter | true→Visible |
| `InverseBoolToVisibility` | InverseBoolToVisibilityConverter | true→Collapsed |
| `InverseBool` | InverseBoolConverter | true→false (for IsEnabled bindings) |
| `StringToVisibility` | StringToVisibilityConverter | non-empty→Visible |
| `NullToVisibility` | NullToVisibilityConverter | non-null→Visible |
| `BoolToFontWeight` | BoolToFontWeightConverter | true→SemiBold |
| `HexToBrush` | HexToBrushConverter | "#hex"→SolidColorBrush |
| `StringToColor` | StringToColorConverter | "#hex"→Color struct (for SolidColorBrush.Color) |
| `TestResultColor` | TestResultColorConverter | "✓…"→green, else red |
| `SaveButtonText` | SaveButtonTextConverter | bool isEdit + optional param "password" |
| `UnreadBadgeVisibility` | UnreadBadgeVisibilityConverter | int>0→Visible |
| `BoolToChevron` | BoolToChevronConverter | true→"▾", false→"▸" |
| `IntToVisibility` | IntToVisibilityConverter | int>0→Visible |
| `DateToString` | DateToStringConverter | ISO date string → "h:mm tt" / "Yesterday" / "ddd" / etc. |

---

## Styles (Themes/Styles.xaml)

| Key | TargetType | Purpose |
|-----|-----------|---------|
| `PrimaryButton` | Button | Blue filled, rounded corners |
| `SecondaryButton` | Button | Outlined, hover gray |
| `PresetButton` | Button | Pill-shaped preset selector |
| `LinkButton` | Button | Text-only, blue, underline on hover |
| `IconButton` | Button | Small square, emoji-friendly |
| `FieldLabel` | TextBlock | Small gray form label |
| `FieldBox` | TextBox | Rounded input, blue focus ring |
| `FieldPassword` | PasswordBox | Same as FieldBox |
| `FieldCombo` | ComboBox | Rounded dropdown |
| `SidebarItem` | Button | Full-width, hover gray bg |
| `EmailRow` | Button | Full-width, bottom border, hover blue tint |
| `FilterPill` | RadioButton | Pill toggle: checked=blue, unchecked=transparent |
| `DropShadow` | DropShadowEffect | Resource key for popup shadows |

**Important XAML rules:**
- `ControlTemplate.Triggers` must be INSIDE `<ControlTemplate>` but OUTSIDE the root element
- `xmlns:System="clr-namespace:System;assembly=mscorlib"` required for `<System:String>` resources
- `Run.Visibility` binding is NOT supported in WPF — use TextBlock.Visibility instead

---

## Known Gotchas & Fixes Applied

1. **`using System.IO;` must be explicit** in `AppPaths.cs`, `ImapSyncService.cs`, `EmailViewerViewModel.cs`, `SidebarViewModel.cs` — WPF temp XAML compiler project doesn't inherit global usings.

2. **MVVMTK0034** — CommunityToolkit.Mvvm source generators: never use backing fields (`_sidebar`) directly after assigning with `[ObservableProperty]`. Always use generated property (`Sidebar`).

3. **`Popup.IsOpen` is `bool`** — must bind to a bool property or use `InverseBool` converter. `IntToVisibilityConverter` returns `Visibility` which silently fails on `IsOpen`.

4. **`IsEnabled` is `bool`** — use `InverseBool` converter, NOT `InverseBoolToVisibility`.

5. **PasswordBox** has no MVVM binding support — sync via `PasswordChanged` event in code-behind.

6. **`TextTransform="Uppercase"`** does not exist in WPF — use hardcoded uppercase text strings.

7. **`Placeholder.PlaceholderText`** does not exist in WPF — use an overlay `TextBlock` that hides when text is non-empty.

8. **DPAPI passwords** are machine+user-scoped — passwords encrypted on one machine cannot be decrypted on another. Users must re-enter on first run.

9. **`DropShadowEffect` as resource** — needs `xmlns:System` in the ResourceDictionary that defines it.

10. **WebView2 in `EmailViewerView`** — always call `EnsureCoreWebView2Async()` before `NavigateToString()`. The `NavigationStarting` handler cancels all navigations and opens links in the system browser.

---

## Backup & Restore

### How it works
- **Backup** — zips the entire `%APPDATA%\MailBox\` folder into a single `.zip`:
  - `accounts.db` (account settings + logs)
  - `maildata/*.sqlite` (all per-account mail stores)
  - `mail/**/*` (all attachment binary files)
  - `manifest.json` (version tag + timestamp)
- **Restore** — extracts a `.zip` back over `%APPDATA%\MailBox\`, overwriting all files. Validates `manifest.json` presence before proceeding.

### Trigger from
- **Sidebar** bottom — "💾 Backup Data" and "📥 Restore Backup" buttons
- **System tray** context menu — same two items

### Files
| File | Role |
|------|------|
| `Services/BackupService.cs` | `BackupAsync(zipPath)` + `RestoreAsync(zipPath)` + `Progress` event |
| `Views/BackupRestoreWindow.xaml/.cs` | Progress modal — shows bar, status, Cancel/Close, "Open Folder" on success |
| `ViewModels/MainViewModel.cs` | `BackupCommand`, `RestoreCommand` — file dialogs + window orchestration |
| `Services/BackgroundSyncService.cs` | Added `Pause()`/`Resume()` — called around restore to prevent race |
| `ViewModels/EmailListViewModel.cs` | Added `Clear()` — called after restore to reset the list |

### Restore flow
1. `OpenFileDialog` → user picks `.zip`
2. Confirmation `MessageBox`
3. `_bgSync.Pause()` — auto-sync suspended
4. `BackupRestoreWindow` shown (modal)
5. On success: `Sidebar.LoadAccounts()`, `EmailList.Clear()`, `RightPanel = null`
6. `_bgSync.Resume()`

---

## Pending / Future Work

- [ ] **Tray icon image** — create `E:\MailBox\MailBox\Assets\mailbox.ico` and uncomment `<ApplicationIcon>` in `.csproj`. Set `IconSource` on `TaskbarIcon` in `MainWindow.xaml`.
- [ ] **Import wizard** — migrate existing mail from Laravel's `database.sqlite` + per-account `.sqlite` files. Passwords cannot be auto-imported (DPAPI vs Laravel Crypt).
- [ ] **NSIS / Squirrel installer** — for distributing to other machines.
- [ ] **Subject TextBox placeholder** — overlay TextBlock that collapses when Subject is non-empty.
- [ ] **Mark all as read** button in EmailList toolbar.
- [ ] **Draft saving** — auto-save compose body to a drafts folder periodically.
- [ ] **Attachment drag-drop** — drag files into ComposeWindow to attach them.
- [ ] **Search across all folders** — currently search is per-folder only.

---

## Laravel Web App (companion project)

Located at `E:\xampp\htdocs\mailbox` (XAMPP).  
Stack: Laravel 12 + Livewire 3 + Alpine.js.  
SQLite per-account files at `storage/app/maildata/{email}.sqlite` — **identical schema** to this desktop app's `MailDataRepository`.  
See `app/Services/MailDataService.php` and `app/Services/SyncService.php`.

---

## Session History Summary

Built from scratch in a single extended session:
1. Designed architecture (MVVM + Generic Host + per-account SQLite)
2. Implemented all models, services, viewmodels, views
3. Fixed ~15 XAML/C# compile errors (missing usings, MVVM backing field refs, duplicate templates, invalid WPF attributes)
4. Added all 14 value converters
5. Published single-file exe: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
6. Fixed final blockers: ColorSwatches, SetColorCommand, PasswordBox event, Popup bool binding, IsEnabled bool binding
7. Rebuilt and republished — **Build succeeded, 0 errors**
