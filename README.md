# LogMan

LogMan is a Windows Event Log viewer built with WPF and `.NET 10`. It can merge multiple `.evtx` files into a single timeline, monitor selected Windows logs live, and inspect event message text plus raw XML details from one UI.

## Current Capabilities

- Open one or more `.evtx` files and merge them into a single newest-first view.
- Monitor local or remote `Application`, `System`, `Security`, and `Setup` logs in real time.
- Filter by free-text search plus severity toggles for `Critical`, `Error`, `Warning`, `Info`, and `Verbose`.
- Export the current filtered view to `CSV` or `JSON`.
- Highlight matching entries by `Event ID`.
- Copy a selected event to the clipboard.
- Search Bing for the selected event ID or message preview from the context menu.
- View both formatted event text and raw XML for the selected row.

## Implementation Notes

- Event access is provided through `System.Diagnostics.Eventing.Reader`.
- File imports are read in batches and then merged into a single sorted list.
- Imported records initially load without full message text; message previews are prefetched in the background.
- Selecting a row loads full message text and raw XML when needed.
- Live-captured events include an immediate preview and can load full details on selection.

## Requirements

- Windows 10 or Windows 11
- `.NET 10 SDK` for local development

## Run Locally

```powershell
dotnet restore
dotnet run --project .\LogMan.csproj
```

## Build

```powershell
dotnet build .\LogMan.csproj
```

## Publish

The installer script expects publish output under `bin\Release\net10.0-windows\win-x64\publish`.

Example publish command:

```powershell
dotnet publish .\LogMan.csproj -c Release -r win-x64
```

An Inno Setup script is included at `LogManSetup.iss` for packaging a Windows installer.

## Usage

### Load `.evtx` files

1. Click `Open`.
2. Select one or more `.evtx` files.
3. LogMan merges them into a single grid sorted by timestamp.

### Live monitoring

1. Use the `Monitor` menu to choose which logs to watch.
2. Optionally add a remote machine name in `Add Remote`.
3. Toggle `Live` on to start watching the selected sources.

Notes:

- Remote monitoring depends on Windows Event Log access to the target machine.
- Some logs, especially `Security`, may require elevated permissions.

### Filtering and inspection

- The search box matches event message text when available, plus source, level, machine name, and event ID.
- Severity checkboxes filter the visible rows.
- The lower pane shows the selected event's formatted message and raw XML details.

### Context menu actions

- `Highlight Event` toggles highlighting for entries with the same event ID.
- `Clear Highlights` removes all highlight groups.
- `Search Bing for Event ID` opens a browser search using the provider name and event ID.
- `Search Bing for Message` searches using the visible message preview.
- `Copy Event` copies key fields, message text, and raw XML when available.

## Test Data

`GenerateTestLogs.ps1` writes sample entries to the Windows `Application` and `System` logs and triggers a `Security` audit event. Run it from an elevated PowerShell session.

## Project Layout

- `MainWindow.xaml`: main WPF UI
- `ViewModels/MainViewModel.cs`: view state, commands, filtering, export, and live-capture orchestration
- `Services/EvtxLogProvider.cs`: file loading, live watching, message lookup, and raw XML retrieval
- `Models/LogEntry.cs`: UI model for an event row
- `LogManSetup.iss`: installer definition

## Icon Attribution

[Trunk icons created by Freepik - Flaticon](https://www.flaticon.com/free-icons/trunk)

## License

MIT
