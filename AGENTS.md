# AGENTS.md

This file describes the structure, conventions, and verification workflow for **DiskGo**.

## Project Overview

DiskGo is a Windows disk space analyzer and file management utility written in C# (.NET 10, `net10.0-windows10.0.26100.0`). It supports both an interactive WinUI 3 desktop interface and a CLI mode (`Program.cs`, `Cli/`).

### Solution Layout

- `DiskGo.sln` — Visual Studio solution containing the desktop app, core engine, and test suite.
- `DiskGo.csproj` — WinUI 3 desktop application (`OutputType=WinExe`, `WindowsPackageType=None`, Win2D via `Microsoft.Graphics.Win2D`, Windows App SDK 2.7).
- `DiskGo.Core/DiskGo.Core.csproj` — Platform-independent/Win32 core scanning, tree modeling, filtering, treemap layout, export/import, monitoring, and file actions library (`AllowUnsafeBlocks=true`).
- `DiskGo.Tests/DiskGo.Tests.csproj` — xUnit test suite covering directory scanning, MFT parsing, tree building, treemap layout, duplicate detection, search queries, file actions, and CSV/TSV export/import.

## Architecture & Key Modules

### `DiskGo.Core` (Scan & Analysis Engine)
- **Model (`Model/`)**:
  - `FolderNode.cs`: Tree node rolling up logical size, allocated size, own sizes, counts, and timestamps; lazy `_children` dictionary allocation and `TrimExcess()` on `Seal()`.
  - `FileEntry.cs`: Stores `DirectoryNode` reference and derives `FullPath` lazily to avoid retaining duplicate full-path strings across millions of files.
  - `ScanResult.cs`: Holds the tree, flat file list, extension totals, drive space, and warnings; `FindFolder`/`FindFile` traverse the tree directly without retaining full-path dictionaries.
  - `DriveSpace.cs`, `ExtensionTotal.cs`.
- **Scanning (`Scanning/`)**:
  - `DiskScanner.cs`: Coordinates single-target and multi-target scans, volume mount traversal, portable device scanning (`Portable/`), and fallback from NTFS MFT to `DirectoryScanner.cs`.
  - `DirectoryScanner.cs`, `TreeBuilder.cs`, `ResultCombiner.cs`, `ReparsePoint.cs`, `ScanOptions.cs`, `ScanProgress.cs`.
- **Target Discovery (`Discovery/`)**:
  - `ScanTargetList.cs`, `ScanTargets.cs`, `ScanTarget.cs`, `NetworkDrives.cs`, `SubstDrives.cs`, `PortableDevices.cs`, `VolumeMounts.cs`, `VolumeSerial.cs`, `SessionLists.cs`.
- **NTFS MFT Reader (`Ntfs/`)**:
  - `VolumeReader.cs`, `MftScanner.cs`, `MftFileRecord.cs`, `FileNameRecord.cs`, `RunList.cs`, `RecordResolver.cs`, `AttributeList.cs`, `MftLayout.cs`, `MftDump.cs`, `NtfsTime.cs`, `NtfsUpcase.cs`, `RecordSizes.cs`, `RunReader.cs`, `VolumeBootRecord.cs`.
- **Portable Devices (`Portable/`)**:
  - `PortableDeviceApi.cs`, `PortableDeviceScanner.cs`.
- **Query & Analysis (`Query/`)**:
  - `PathFilter.cs`, `SearchQuery.cs`, `ResultFilter.cs`, `DuplicateFinder.cs`, `DuplicateName.cs`, `ExtensionSummary.cs`.
- **Export & Import (`Export/`)**:
  - `CsvExporter.cs`, `CsvImporter.cs`, `ExtensionExporter.cs`.
- **Monitoring (`Monitoring/`)**:
  - `ScanMonitor.cs`, `DirectoryWatcher.cs`, `FileChange.cs`.
- **File Operations (`Actions/`)**:
  - `FileActions.cs`: Shell-backed recycle/permanent delete, rename, copy, move, and incremental `ScanResult` updates.
- **Layout (`Layout/`)**:
  - `TreemapLayout.cs`: Pure squarified treemap item collection and rectangle layout engine decoupled from pixel rendering.

### `DiskGo` (Entry Point, CLI & WinUI 3 Desktop UI)
- **Entry & CLI (`Program.cs`, `Cli/`)**:
  - `Program.cs`: Custom `[STAThread] Main` routing WinUI startup vs. CLI mode.
  - `Cli/CliOptions.cs`: Command-line argument parsing and usage text.
  - `Cli/CliRunner.cs`: Headless scan, watch, MFT dump, import, export, and action runner.
- **Windows & Pages**:
  - `MainWindow.xaml(.cs)`: Extended `TitleBar` (`MicaBackdrop`), global commands (Export, Settings, MFT dump, New Window, Elevate), and window geometry persistence.
  - `MainPage.xaml` + partial classes:
    - `MainPage.xaml.cs`: Fields, initialization, settings sync, and formatting helpers.
    - `MainPage.Scanning.cs`: Target picker, drag-drop import/scan, scan execution, and monitoring coordination.
    - `MainPage.Tree.cs`: Folder/file tree rows, expansion, breadcrumb, file types sidebar, and capacity display.
    - `MainPage.Files.cs`: File search, duplicate mode, virtualized file list refresh, column menus, sorting, and unit switching.
    - `MainPage.Treemap.cs`: Treemap sizing, bitmap rendering, hit testing, pane visibility, and PNG export.
    - `MainPage.Actions.cs`: Context menus, clipboard (`CF_HDROP`), rename, delete, export/settings/MFT dialogs.
    - `MainPage.Shortcuts.cs`: Pane splitters and keyboard shortcut / type-ahead handlers.
- **UI Components (`Ui/`)**:
  - `Ui/Controls/Treemap.cs`: BGRA pixel-buffer renderer (`WriteableBitmap` + Win2D labels), hit testing, remainder color mosaic, and PNG export backed by `TreemapLayout`.
  - `Ui/Controls/SegmentedBar.cs`: Pill-shaped disk usage breakdown bar (`UserControl`).
  - `Ui/ViewModels/TreeRow.cs`: `TreeRow`, `TypeRow`, and `TreeSortColumn`.
  - `Ui/ViewModels/BatchObservableCollection.cs`: `ObservableCollection<T>` with batched `InsertRange`/`RemoveRange`/`ReplaceAll`/`RefreshAll` (`Reset` notifications).
  - `Ui/ViewModels/VirtualFileList.cs`: Virtualized `IList` / `INotifyCollectionChanged` adapter (`FileRow`, `FileColumnState`, `FileSortColumn`).
  - `Ui/Support/AppSettings.cs`: JSON settings persistence under `%LOCALAPPDATA%\DiskGo\settings.json`.
  - `Ui/Support/Dialogs.cs`: Modal dialogs for export, treemap PNG export, multi-target selection, settings, MFT dump, and warnings.
  - `Ui/Support/ShellHelpers.cs`: Win32 P/Invoke helpers for Explorer reveal, properties, clipboard, and UAC elevation.
  - `Ui/Support/SizeText.cs` & `Ui/Support/TypeColors.cs`: Formatting and deterministic 16-color extension palette.

## Build & Test Commands

Run from the repository root:

- **Run unit tests**:
  ```powershell
  dotnet test DiskGo.Tests/DiskGo.Tests.csproj
  ```
- **Build WinUI 3 desktop app (x64)**:
  ```powershell
  dotnet build DiskGo.csproj -p:Platform=x64
  ```
- **Build to an isolated verification directory** (useful when an active `DiskGo.exe` instance locks default `bin/` outputs):
  ```powershell
  dotnet build DiskGo.csproj -p:Platform=x64 -o <temp-output-dir>
  ```

## Coding & Change Guidelines

1. **Keep `DiskGo.Core` UI-agnostic**: Core scanning, filtering, layout calculation, exporting, and file actions must not depend on WinUI types.
2. **Preserve memory efficiency**: Full-volume scans can contain millions of `FileEntry` and hundreds of thousands of `FolderNode` objects. Avoid retaining per-file `FullPath` strings, large lookup dictionaries, or redundant temporary collections on hot paths.
3. **WinUI 3 & NativeAOT constraints**:
   - `DiskGo.csproj` uses `WindowsPackageType=None`, enables `AllowUnsafeBlocks=true` (required by CsWinRT AOT marshalling), and enables `PublishAot=True` in `Release` configuration (`DiskGo.Core.csproj` sets `IsAotCompatible=true`).
   - Keep `MainPage.xaml` `DataTemplate`s on compiled `{x:Bind}` with `x:DataType`, keep `AppSettings` on `System.Text.Json` source generation (`AppSettingsJsonContext`), and avoid `[ComImport]` or reflection-based APIs so `Release` NativeAOT builds stay warning-free.
   - Custom XAML controls referenced via `local:` in `MainPage.xaml` (such as `SegmentedBar`) should inherit `UserControl` so the XAML compiler resolves `local:` types reliably.
   - Never block the UI thread during scans, exports, or file operations; keep updates marshalled via `DispatcherQueue`.
4. **Verify every change**: Run both the `DiskGo` x64 build and `DiskGo.Tests` suite after code modifications, and clean up any temporary verification build folders.
