using AirFerry.Windows.Bundle;
using Xunit;

namespace AirFerry.Windows.Tests;

public class ContinuousSaverTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "AirFerry.ContinuousSaverTests",
            Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_EmptyDir_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ContinuousSaver(" "));
    }

    [Fact]
    public void SaveSingle_WritesFileWithSanitizedName()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var report = saver.SaveSingle("报告<>2026.pdf", [1, 2, 3]);

            Assert.Equal(ContinuousSaveStatus.Saved, report.Status);
            Assert.True(File.Exists(Path.Combine(root, "报告__2026.pdf")));
            Assert.Equal(new byte[] { 1, 2, 3 },
                File.ReadAllBytes(Path.Combine(root, "报告__2026.pdf")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveSingle_SameContentDifferentName_Skips()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var first = saver.SaveSingle("a.txt", [7, 8, 9]);
            var second = saver.SaveSingle("b.txt", [7, 8, 9]);

            Assert.Equal(ContinuousSaveStatus.Saved, first.Status);
            Assert.Equal(ContinuousSaveStatus.SkippedDuplicate, second.Status);
            Assert.False(File.Exists(Path.Combine(root, "b.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveSingle_SameNameDifferentContent_AppendsCounter()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            saver.SaveSingle("a.txt", [1]);
            var second = saver.SaveSingle("a.txt", [2]);

            Assert.Equal(ContinuousSaveStatus.Saved, second.Status);
            Assert.Equal("a(1).txt", Path.GetFileName(second.FinalPath));
            Assert.Equal(new byte[] { 2 },
                File.ReadAllBytes(Path.Combine(root, "a(1).txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveSingle_PreExistingSameContentFile_SkipsAcrossRuns()
    {
        // A file already sitting in the folder from a previous run with the
        // same name AND content must not be duplicated.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.txt"), [4, 5]);
            var saver = new ContinuousSaver(root);
            var report = saver.SaveSingle("a.txt", [4, 5]);

            Assert.Equal(ContinuousSaveStatus.SkippedDuplicate, report.Status);
            Assert.Equal("a.txt", Path.GetFileName(Directory.GetFiles(root)[0]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveText_WritesUtf8WithoutBom()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var report = saver.SaveText("消息.txt", "你好");

            Assert.Equal(ContinuousSaveStatus.Saved, report.Status);
            byte[] written = File.ReadAllBytes(report.FinalPath!);
            Assert.Equal(new byte[] { 0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD }, written);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_CreatesSubfolderWithMembers()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile>
            {
                new("x.jpg", new byte[] { 1 }),
                new("y.jpg", new byte[] { 2, 3 }),
            };
            var report = saver.SaveBundle(files, title: "发送_0816_120000");

            Assert.Equal(ContinuousSaveStatus.Saved, report.Status);
            Assert.Equal("发送_0816_120000", report.BundleDirName);
            string sub = Path.Combine(root, "发送_0816_120000");
            Assert.True(File.Exists(Path.Combine(sub, "x.jpg")));
            Assert.True(File.Exists(Path.Combine(sub, "y.jpg")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_SameMembersAgain_SkipsWholeBundle()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile> { new("x.jpg", new byte[] { 9 }) };
            var first = saver.SaveBundle(files, title: "包A");
            var second = saver.SaveBundle(files, title: "包B");

            Assert.Equal(ContinuousSaveStatus.Saved, first.Status);
            Assert.Equal(ContinuousSaveStatus.SkippedDuplicate, second.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "包B")));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_ReplayAfterRestart_SkipsViaMarkerFile()
    {
        // The in-memory digest set dies with the saver (app restart); the
        // marker file inside the bundle folder is what makes replayed
        // bundles skip across runs.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var files = new List<BundleFile> { new("x.jpg", new byte[] { 5 }) };
            var first = new ContinuousSaver(root).SaveBundle(files, title: "包一");
            var second = new ContinuousSaver(root).SaveBundle(files, title: "包二");

            Assert.Equal(ContinuousSaveStatus.Saved, first.Status);
            Assert.Equal(ContinuousSaveStatus.SkippedDuplicate, second.Status);
            Assert.Single(Directory.GetDirectories(root));
            Assert.True(File.Exists(
                Path.Combine(root, "包一", ".airferry-bundle-id")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_MemberDeletedAfterSave_ReplaySavesFreshCopy()
    {
        // A skip decision must re-verify the folder's actual bytes: with a
        // member deleted, a replayed bundle has to save a fresh copy instead
        // of being swallowed as a duplicate.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile>
            {
                new("a.txt", new byte[] { 1 }),
                new("b.txt", new byte[] { 2 }),
            };
            Assert.Equal(ContinuousSaveStatus.Saved,
                saver.SaveBundle(files, title: "包").Status);

            File.Delete(Path.Combine(root, "包", "b.txt"));

            var replay = saver.SaveBundle(files, title: "包");
            Assert.Equal(ContinuousSaveStatus.Saved, replay.Status);
            // Fresh intact copy in a new folder; the user's deletion in the
            // old folder is left untouched.
            Assert.False(File.Exists(Path.Combine(root, "包", "b.txt")));
            Assert.Equal(2, Directory.GetDirectories(root).Length);
            Assert.True(File.Exists(Path.Combine(replay.FinalPath!, "a.txt")));
            Assert.True(File.Exists(Path.Combine(replay.FinalPath!, "b.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_MemberTamperedAfterSave_ReplaySavesFreshCopy()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile> { new("a.txt", new byte[] { 1 }) };
            Assert.Equal(ContinuousSaveStatus.Saved,
                saver.SaveBundle(files, title: "包").Status);

            File.WriteAllBytes(Path.Combine(root, "包", "a.txt"), new byte[] { 9 });

            var replay = saver.SaveBundle(files, title: "包");
            Assert.Equal(ContinuousSaveStatus.Saved, replay.Status);
            // Old folder keeps the user's edit; the new copy is pristine.
            Assert.Equal(new byte[] { 9 },
                File.ReadAllBytes(Path.Combine(root, "包", "a.txt")));
            Assert.Equal(new byte[] { 1 },
                File.ReadAllBytes(Path.Combine(replay.FinalPath!, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_MemberDeletedAfterRestart_ReplaySavesFreshCopy()
    {
        // Restart variant: the in-memory record is gone, so the marker scan
        // must itself re-verify the manifest and refuse the stale match.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var files = new List<BundleFile>
            {
                new("a.txt", new byte[] { 1 }),
                new("b.txt", new byte[] { 2 }),
            };
            Assert.Equal(ContinuousSaveStatus.Saved,
                new ContinuousSaver(root).SaveBundle(files, title: "包").Status);

            File.Delete(Path.Combine(root, "包", "b.txt"));

            var replay = new ContinuousSaver(root).SaveBundle(files, title: "包");
            Assert.Equal(ContinuousSaveStatus.Saved, replay.Status);
            Assert.Equal("包(1)", replay.BundleDirName);
            Assert.True(File.Exists(Path.Combine(root, "包(1)", "b.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveSingle_FileDeletedAfterSave_ReplaySavesAgain()
    {
        // The same integrity rule for single files: a deleted file must not
        // be skipped as an in-memory duplicate on replay.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            Assert.Equal(ContinuousSaveStatus.Saved,
                saver.SaveSingle("a.txt", new byte[] { 3, 4 }).Status);

            File.Delete(Path.Combine(root, "a.txt"));

            var replay = saver.SaveSingle("a.txt", new byte[] { 3, 4 });
            Assert.Equal(ContinuousSaveStatus.Saved, replay.Status);
            Assert.Equal(new byte[] { 3, 4 },
                File.ReadAllBytes(Path.Combine(root, "a.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MoveVerifiedFile_TargetDeletedAfterSave_ReplaysMove()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = [13, 14];
            string sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            var saver = new ContinuousSaver(root);

            string src1 = Path.Combine(root, "src1.bin");
            File.WriteAllBytes(src1, data);
            Assert.Equal(ContinuousSaveStatus.Saved,
                saver.MoveVerifiedFile("big.zip", src1, sha).Status);

            File.Delete(Path.Combine(root, "big.zip"));

            string src2 = Path.Combine(root, "src2.bin");
            File.WriteAllBytes(src2, data);
            var replay = saver.MoveVerifiedFile("big.zip", src2, sha);
            Assert.Equal(ContinuousSaveStatus.Saved, replay.Status);
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(root, "big.zip")));
            Assert.False(File.Exists(src2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_SanitizedNameCollisions_DoNotOverwriteEachOther()
    {
        // "a:b.txt" and "a*b.txt" both sanitize to "a_b.txt" — the second
        // member must land on "a_b(1).txt", never overwrite the first.
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile>
            {
                new("a:b.txt", new byte[] { 1 }),
                new("a*b.txt", new byte[] { 2 }),
            };
            var report = saver.SaveBundle(files, title: "包");

            Assert.Equal(ContinuousSaveStatus.Saved, report.Status);
            string sub = Path.Combine(root, "包");
            Assert.Equal(new byte[] { 1 },
                File.ReadAllBytes(Path.Combine(sub, "a_b.txt")));
            Assert.Equal(new byte[] { 2 },
                File.ReadAllBytes(Path.Combine(sub, "a_b(1).txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_LeavesNoStagingTempBehind()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            saver.SaveBundle([new BundleFile("x.txt", new byte[] { 1 })], title: "包");

            // The staging dir is renamed into place; no *.tmp sibling remains
            // and exactly one directory exists.
            Assert.Empty(Directory.GetDirectories(root, "*.tmp"));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveBundle_WriteFailure_KeepsFolderCleanAndAllowsRetry()
    {
        // POSIX-only: a read-only parent forces the staged member write to
        // fail. The folder must stay clean (no partial bundle, no .tmp), the
        // digest must NOT be registered, and a retry after restoring
        // permissions must succeed.
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var saver = new ContinuousSaver(root);
            var files = new List<BundleFile> { new("x.txt", new byte[] { 7 }) };
            Chmod(root, "555");
            Assert.ThrowsAny<Exception>(() => saver.SaveBundle(files, title: "包"));
            Chmod(root, "755");

            Assert.Empty(Directory.GetDirectories(root));
            var retry = saver.SaveBundle(files, title: "包");
            Assert.Equal(ContinuousSaveStatus.Saved, retry.Status);
        }
        finally
        {
            Chmod(root, "755");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Chmod(string path, string mode)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            "chmod", $" {mode} \"{path}\"")
        {
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi)?.WaitForExit();
    }

    [Fact]
    public void MoveVerifiedFile_MovesAndDeduplicatesByKnownHash()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = [11, 22, 33];
            string sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

            var saver = new ContinuousSaver(root);
            string src1 = Path.Combine(root, "src1.bin");
            File.WriteAllBytes(src1, data);
            var first = saver.MoveVerifiedFile("big.zip", src1, sha);

            Assert.Equal(ContinuousSaveStatus.Saved, first.Status);
            Assert.False(File.Exists(src1));
            Assert.Equal(data, File.ReadAllBytes(first.FinalPath!));

            // Same verified hash again (replayed transfer): skipped, source
            // left for the caller's ledger cleanup.
            string src2 = Path.Combine(root, "src2.bin");
            File.WriteAllBytes(src2, data);
            var second = saver.MoveVerifiedFile("big.zip", src2, sha);
            Assert.Equal(ContinuousSaveStatus.SkippedDuplicate, second.Status);
            Assert.True(File.Exists(src2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MoveVerifiedFile_InvalidHash_Throws()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            string src = Path.Combine(root, "src.bin");
            File.WriteAllBytes(src, [1]);
            var saver = new ContinuousSaver(root);
            Assert.Throws<ArgumentException>(
                () => saver.MoveVerifiedFile("a", src, "nothex"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class AppSettingsEscapeTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("D:\\接收\\文件夹", "D:\\\\接收\\\\文件夹")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("D:\\a\"b\\", "D:\\\\a\\\"b\\\\")]
    public void EscapeJsonString_EscapesBackslashAndQuote(string raw, string escaped)
    {
        Assert.Equal(escaped, Services.AppSettings.EscapeJsonString(raw));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("D:\\\\接收\\\\文件夹", "D:\\接收\\文件夹")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("trailing\\\\", "trailing\\")]
    public void UnescapeJsonString_RoundTrips(string escaped, string raw)
    {
        Assert.Equal(raw, Services.AppSettings.UnescapeJsonString(escaped));
        Assert.Equal(raw, Services.AppSettings.UnescapeJsonString(
            Services.AppSettings.EscapeJsonString(raw)));
    }

    [Fact]
    public void UnescapeJsonString_DanglingBackslashKeptLiteral()
    {
        Assert.Equal("a\\", Services.AppSettings.UnescapeJsonString("a\\"));
    }
}
