# DiskGo

DiskGo is a fast, lightweight Windows disk space analyzer and file management tool built with **.NET 10** and **WinUI 3**. It reads NTFS volumes directly from the Master File Table (`$MFT`) when elevated and seamlessly falls back to multi-threaded Win32 directory enumeration for folders, network shares, portable devices, and non-NTFS drives.

## Features

- **High-Speed Disk Scanning**
  - Direct raw-volume NTFS `$MFT` streaming with adaptive overlapped I/O pipelining.
  - Multi-threaded Win32 `GetFileInformationByHandleEx` directory walking for non-NTFS volumes, network paths, mounted volumes, `subst` drives, and portable (MTP) devices.
  - Accurate logical size and on-disk allocated size accounting (handling sparse files, compressed units, resident MFT data, hard links, and reparse points/junctions).
  - Support for scanning multiple targets simultaneously without double-counting overlapping paths.

- **Interactive WinUI 3 Workspace**
  - **Folder & File Tree**: Hierarchical size breakdown showing both subfolders and direct files, percentage-of-parent bars, file/folder counts, timestamps, and column sorting/customization.
  - **File Type Breakdown**: Per-folder extension statistics with deterministic 16-color palettes and click-to-filter integration.
  - **Squarified Treemap**: Hardware-independent pixel-buffer treemap renderer (`WriteableBitmap` + Win2D labels) with DPI-aware rendering, interactive drill-down/navigation, proportional remainder mosaics for dense small files, and high-resolution PNG export (HD / Full HD / 2K / 4K).
  - **On-Demand File Search & Duplicate Finder**: Full-scan instant search (`Ctrl+F`) with wildcard/size/date/attribute query support and fast name-normalized duplicate detection, powered by a virtualized list source for large directories.
  - **Live Shell Actions**: Open, reveal in Explorer, cut/copy (`CF_HDROP`), rename (`F2`), recycle or permanently delete, and view properties with incremental in-memory tree updates.

- **Import, Export & Snapshots**
  - Export scan results or extension summaries to **CSV** or **TSV** with configurable columns, depth limits, UTC timestamps, and selection scoping.
  - Dump raw volume `$MFT` snapshots (`.mft`) for offline inspection and re-import CSV/TSV/MFT files directly into the UI or CLI.

- **CLI Mode**
  - Run headless scans, filter queries, duplicate checks, folder watching, file actions, and exports directly from the command line.

## Project Structure

- `DiskGo.csproj` — WinUI 3 desktop application (`Program.cs`, `Cli/`, `MainWindow.xaml(.cs)`, `MainPage.xaml` + partial classes, `Ui/Controls/`, `Ui/ViewModels/`, `Ui/Support/`).
- `DiskGo.Core/DiskGo.Core.csproj` — Core engine organized into `Model/`, `Scanning/`, `Discovery/`, `Ntfs/`, `Portable/`, `Query/`, `Export/`, `Monitoring/`, `Actions/`, and `Layout/`.
- `DiskGo.Tests/DiskGo.Tests.csproj` — xUnit test suite covering scanning, MFT record parsing, treemap layout, filtering, duplicate detection, file actions, and round-trip exports.
- `Interface.md` — UI layout, interaction, and keyboard shortcut specification.
- `AGENTS.md` — Architecture notes, memory model conventions, and build/test guidelines.

## Requirements

- Windows 10 (version 1809 / build 17763 or later) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/) with Windows App SDK build tools
- Administrator privileges (optional, required only for raw `$MFT` volume reading)

## Building & Testing

Run commands from the repository root:

```powershell
# Run unit tests
dotnet test DiskGo.Tests/DiskGo.Tests.csproj

# Build the desktop application (x64)
dotnet build DiskGo.csproj -p:Platform=x64
```

## Command-Line Usage

Launching `DiskGo.exe` without arguments opens the desktop UI. Passing paths or switches runs DiskGo in CLI mode:

```powershell
# Scan a drive, detect duplicates by name and size, and export to CSV
DiskGo.exe C:\ --duplicates name --export report.csv

# Scan a folder with wildcard filters and export extension totals
DiskGo.exe D:\Projects --include "*.pdb;*.log" --export-types types.csv

# Dump an NTFS volume MFT snapshot for offline analysis
DiskGo.exe --dump-mft C C_snapshot.mft

# Import an MFT dump or CSV file and export a filtered summary
DiskGo.exe --import C_snapshot.mft --volume C --export summary.csv
```

Run `DiskGo.exe --help` for the complete list of CLI options.
