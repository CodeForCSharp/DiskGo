using DiskGo.Core;
using Xunit;

namespace DiskGo.Tests;

/// <summary>
/// Covers the three additions that sit on top of a scan: selecting more than
/// one folder, exporting the file-type table, and reading an exported CSV back.
/// </summary>
public class SelectionTests
{
    [Fact]
    public void SeveralFolders_AreCombinedWithoutCountingTwice()
    {
        using var sandbox = new Sandbox();
        File.WriteAllBytes(sandbox.File("one", "a.bin"), new byte[1000]);
        File.WriteAllBytes(sandbox.File("two", "b.bin"), new byte[2000]);

        ScanResult result = DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Combine("one"), sandbox.Combine("two")],
        });

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(3000, result.Root.Size);
        Assert.Equal(2, result.Root.Children.Count);
    }

    [Fact]
    public void OverlappingFolders_AreRejected()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Combine("parent", "child"));

        ArgumentException error = Assert.Throws<ArgumentException>(() => DiskScanner.Scan(new ScanOptions
        {
            Paths = [sandbox.Combine("parent"), sandbox.Combine("parent", "child")],
        }));

        Assert.Contains("overlap", error.Message);
    }

    [Fact]
    public void TheSamePortableDevice_CannotBeSelectedTwice()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DiskScanner.Scan(new ScanOptions
        {
            Paths = ["mtp:device-1", "MTP:device-1"],
        }));

        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void APortableDevice_DoesNotOverlapAnotherThatSharesItsPrefix()
    {
        // mtp:phone must not be read as a drive whose root swallows mtp:phone-two.
        string[] targets = ScanTargets.Normalize(["mtp:phone", "mtp:phone-two"]);

        Assert.Equal(2, targets.Length);
        Assert.False(ScanTargets.Contains(targets[0], targets[1]));
    }

    [Fact]
    public void ExtensionExport_ListsTheLargestTypeFirst()
    {
        using var sandbox = new Sandbox();
        File.WriteAllBytes(sandbox.File("a.jpg"), new byte[500]);
        File.WriteAllText(sandbox.File("b.txt"), "x");
        File.WriteAllText(sandbox.File("c.txt"), "yy");

        ScanResult result = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string csv = sandbox.Combine("types.csv");
        ExtensionExporter.Export(result, csv);

        string[] lines = File.ReadAllLines(csv);
        Assert.Equal("Extension,Files,Size,Allocated,% of Total", lines[0]);
        Assert.StartsWith(".jpg,", lines[1]);
        Assert.Contains(".txt,2,", string.Join('\n', lines));
    }

    [Fact]
    public void CsvRoundTrip_RebuildsTheTreeAndTheSizes()
    {
        using var sandbox = new Sandbox();
        File.WriteAllText(sandbox.File("docs", "odd, name.txt"), "hello");
        File.WriteAllBytes(sandbox.File("photos", "a.jpg"), new byte[4096]);

        ScanResult scanned = DiskScanner.Scan(new ScanOptions { Paths = [sandbox.Root] });
        string csv = sandbox.Combine("scan.csv");
        CsvExporter.Export(scanned, csv, new CsvExportOptions { IncludeAllDates = true, IncludeOwnSize = true });

        ScanResult imported = CsvImporter.Import(csv);

        Assert.Equal(2, imported.Files.Count);
        Assert.Equal(scanned.Root.Size, imported.Root.Size);
        Assert.Contains(imported.Files, file => file.Name == "odd, name.txt" && file.Size == 5);
        Assert.Contains(imported.Files, file => file.Name == "a.jpg" && file.Size == 4096);
        Assert.Equal(2, imported.Extensions.Count);
    }

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "diskgo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

        public string File(params string[] segments)
        {
            string path = Combine(segments);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
