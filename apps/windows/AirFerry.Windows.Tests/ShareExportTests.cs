using AirFerry.Windows.Bundle;
using Xunit;

namespace AirFerry.Windows.Tests;

public class ShareExportTests
{
    [Fact]
    public void ExportFile_PreservesLogicalNameAndExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "AirFerry.ShareExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string blob = Path.Combine(root, new string('a', 64));
            File.WriteAllBytes(blob, [1, 2, 3]);

            string exported = ShareExport.ExportFile(blob, "报告 2026.pdf", root);

            Assert.Equal("报告 2026.pdf", Path.GetFileName(exported));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(exported));
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains(
                    "ZoneId=3",
                    File.ReadAllText(exported + ":Zone.Identifier"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExportFiles_DeduplicatesNamesWithoutDroppingExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "AirFerry.ShareExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dir = ShareExport.ExportFiles(
                [("photo.jpg", new byte[] { 1 }), ("photo.jpg", new byte[] { 2 })],
                root);

            Assert.True(File.Exists(Path.Combine(dir, "photo.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "photo(1).jpg")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PruneExpired_RemovesOnlyOldOwnedDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "AirFerry.ShareExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = DateTime.UtcNow;
            string oldDir = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
            string recentDir = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
            string unrelated = Directory.CreateDirectory(Path.Combine(root, "do-not-delete")).FullName;
            Directory.SetLastWriteTimeUtc(oldDir, now - TimeSpan.FromHours(2));
            Directory.SetLastWriteTimeUtc(recentDir, now);
            Directory.SetLastWriteTimeUtc(unrelated, now - TimeSpan.FromDays(30));

            int removed = ShareExport.PruneExpired(root, TimeSpan.FromHours(1), now);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(recentDir));
            Assert.True(Directory.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExportFiles_RemovesPartialDirectoryOnFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "AirFerry.ShareExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ShareExport.ExportFiles(FailingFiles(), root));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<(string Name, byte[] Data)> FailingFiles()
    {
        yield return ("first.txt", new byte[] { 1 });
        throw new InvalidOperationException("synthetic export failure");
    }
}
