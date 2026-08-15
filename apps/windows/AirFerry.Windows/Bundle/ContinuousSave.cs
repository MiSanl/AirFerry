using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirFerry.Windows.Bundle;

public enum ContinuousSaveStatus
{
    Saved,
    SkippedDuplicate,
    Failed,
}

/// <summary>Outcome of one continuous-mode save attempt.</summary>
/// <param name="FinalPath">Saved file path, or the bundle subfolder path.</param>
/// <param name="BundleDirName">Subfolder name for bundle saves, else null.</param>
/// <param name="Error">Failure reason when <see cref="ContinuousSaveStatus.Failed"/>.</param>
public sealed record ContinuousSaveReport(
    ContinuousSaveStatus Status,
    string? FinalPath,
    string DisplayName,
    long SizeBytes,
    string Sha256Hex,
    string? BundleDirName = null,
    string? Error = null)
{
    public static ContinuousSaveReport Failed(string displayName, string error) =>
        new(ContinuousSaveStatus.Failed, null, displayName, 0, string.Empty, null, error);
}

/// <summary>
/// Continuous-receive sink: saves recovered payloads straight into a
/// user-chosen folder (never the ContentStore — continuous mode's single
/// source of truth is the folder, so large files are not stored twice).
/// Deduplication is content-based and **always re-verified against the
/// folder's actual bytes** before a skip: users delete or edit received
/// files, and a stale record must never block recovering them again.
/// <list type="bullet">
/// <item>an in-memory digest→saved-path map covers this run;</item>
/// <item>when the target name already exists in the folder with the same
/// size and hash (e.g. from a previous run), the save is skipped too —
/// a genuinely different file still lands via the usual <c>name(1)</c>
/// numbering.</item>
/// </list>
/// Bundles go into their own uniquified subfolder and are deduplicated as a
/// whole by a digest over member names + contents. Each bundle folder
/// carries its digest **and a per-member manifest (final name / size /
/// SHA-256)** in a marker file (<c>.airferry-bundle-id</c>): a marker hit
/// re-hashes the actual members, so a replayed bundle skips only while the
/// previous copy is still intact — across app restarts too. Deleting or
/// tampering with a member breaks the match and the next replay saves a
/// fresh copy.
/// Single-threaded by design: the recovery pipeline runs one save at a time.
/// </summary>
public sealed class ContinuousSaver
{
    /// <summary>
    /// Marker file dropped inside every saved bundle folder carrying the
    /// bundle digest plus a per-member manifest. Dedup truth travels with
    /// the data: deleting a bundle folder removes its dedup entry, and a
    /// restarted app (fresh saver) still skips a replayed bundle by scanning
    /// these markers — but only after re-verifying the members on disk.
    /// </summary>
    private const string BundleMarkerFileName = ".airferry-bundle-id";

    /// <summary>Where a digest was previously saved, for re-verified skips.</summary>
    private sealed record SavedRecord(string Path, bool IsBundle);

    /// <summary>Marker payload: whole-bundle digest + per-member manifest.</summary>
    private sealed record BundleMarker(
        string Digest,
        IReadOnlyList<BundleMemberManifestEntry> Members);

    private sealed record BundleMemberManifestEntry(string Name, long Size, string Sha256);

    private readonly string _dir;
    private readonly Dictionary<string, SavedRecord> _saved = new(StringComparer.Ordinal);

    public ContinuousSaver(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new ArgumentException("持续接收目录不能为空", nameof(targetDir));
        }
        _dir = targetDir;
    }

    public string TargetDir => _dir;

    /// <summary>Save a recovered single file by its raw bytes.</summary>
    public ContinuousSaveReport SaveSingle(string displayName, byte[] bytes) =>
        SaveBytes(displayName, bytes);

    /// <summary>
    /// Save a text message as UTF-8 (no BOM) under the (already normalized)
    /// descriptor name — the same encoding ReceiveTextView's save-as uses.
    /// </summary>
    public ContinuousSaveReport SaveText(string displayName, string text) =>
        SaveBytes(displayName, new UTF8Encoding(false).GetBytes(text));

    /// <summary>
    /// Save a parsed bundle into its own subfolder (title defaults to the
    /// same 发送_MMdd_HHmmss pattern the ContentStore history uses).
    /// Transactional: members are staged in a hidden temp sibling directory
    /// and revealed with one rename, so a mid-bundle failure never leaves a
    /// normal-looking partial folder behind. Members whose sanitized names
    /// collide (e.g. "a:b.txt" and "a*b.txt" → "a_b.txt") get the usual
    /// "(N)" suffix instead of overwriting each other.
    /// Whole-bundle dedup survives restarts via <see cref="BundleMarkerFileName"/>
    /// and is re-verified against the members on disk on every hit.
    /// </summary>
    public ContinuousSaveReport SaveBundle(
        IReadOnlyList<BundleFile> files, string? title)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("文件包为空", nameof(files));
        }
        string name = string.IsNullOrWhiteSpace(title)
            ? $"发送_{DateTime.Now:MMdd_HHmmss}"
            : title!;
        long total = files.Sum(f => (long)f.Data.LongLength);
        string digest = BundleDigest(files);
        if (_saved.TryGetValue(digest, out SavedRecord? seen) &&
            VerifySavedRecord(seen, digest))
        {
            return Skip(name, total, digest);
        }
        _saved.Remove(digest); // stale record — the folder no longer matches
        // Cross-restart replay: a previous run's bundle folder still carries
        // its digest marker — skip only if every manifest member is still
        // intact on disk; otherwise save a fresh copy so the user can recover
        // what they deleted or edited.
        if (FindIntactBundleDir(digest) is { } existingDir)
        {
            _saved[digest] = new SavedRecord(existingDir, IsBundle: true);
            return Skip(name, total, digest);
        }
        string safe = FileNameUtil.Sanitize(name);
        string finalDir = UniqueDir(safe);
        string stagingDir = $"{finalDir}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(stagingDir);
            // Sanitize is not injective: track the names already used so a
            // second member with the same sanitized name lands on "(1)".
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifest = new List<BundleMemberManifestEntry>(files.Count);
            foreach (BundleFile f in files)
            {
                string safeMember = FileNameUtil.Sanitize(f.Name);
                string memberPath = used.Contains(safeMember)
                    ? FileNameUtil.UniqueTarget(stagingDir, safeMember)
                    : Path.Combine(stagingDir, safeMember);
                used.Add(Path.GetFileName(memberPath));
                WriteAtomic(memberPath, f.Data);
                manifest.Add(new BundleMemberManifestEntry(
                    Path.GetFileName(memberPath),
                    f.Data.LongLength,
                    ContentStoreSha256(f.Data)));
            }
            WriteBundleMarker(stagingDir, new BundleMarker(digest, manifest));
            Directory.Move(stagingDir, finalDir);
        }
        catch
        {
            TryDeleteDir(stagingDir);
            throw;
        }
        _saved[digest] = new SavedRecord(finalDir, IsBundle: true);
        return new ContinuousSaveReport(
            ContinuousSaveStatus.Saved, finalDir, safe, total, digest,
            BundleDirName: Path.GetFileName(finalDir));
    }

    /// <summary>
    /// Move an already-verified on-disk file (the &gt;256 MiB segmented
    /// archive path; the native decompression verified its root SHA-256)
    /// into the folder. On duplicate the source file is left for the caller
    /// (the segment task ledger deletes it with its directory).
    /// Never uses <c>File.Move</c> directly across volumes: the temp file
    /// lives in Documents while the target folder may be another drive /
    /// USB stick, where Move's copy+delete fallback can fail midway. Instead:
    /// copy to a temp file on the TARGET volume, flush, re-verify the SHA-256,
    /// atomically rename, and only then delete the source.
    /// </summary>
    public ContinuousSaveReport MoveVerifiedFile(
        string displayName, string sourcePath, string sha256Hex)
    {
        string hash = sha256Hex.ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("Invalid SHA-256 hash", nameof(sha256Hex));
        }
        long size = new FileInfo(sourcePath).Length; // throws if the file vanished
        if (_saved.TryGetValue(hash, out SavedRecord? seen) &&
            VerifySavedRecord(seen, hash))
        {
            return Skip(displayName, size, hash);
        }
        _saved.Remove(hash);
        string? target = ResolveTarget(displayName, size, hash);
        if (target is null)
        {
            // Same name + same content already in the folder (previous run).
            _saved[hash] = new SavedRecord(
                Path.Combine(_dir, FileNameUtil.Sanitize(displayName)),
                IsBundle: false);
            return Skip(displayName, size, hash);
        }
        string? targetDir = Path.GetDirectoryName(target);
        if (targetDir is null) throw new IOException("目标路径没有目录");
        Directory.CreateDirectory(targetDir);
        string temp = Path.Combine(
            targetDir, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var src = new FileStream(
                       sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                src.CopyTo(dst);
                dst.Flush(flushToDisk: true);
            }
            if (HashFile(temp) != hash)
            {
                throw new IOException("复制后 SHA-256 校验失败");
            }
            File.Move(temp, target);
            // The folder now holds the verified copy; the source's owner (the
            // segment ledger) cleans it up with its task directory.
            try { File.Delete(sourcePath); }
            catch { /* ledger cleanup retries; the folder copy is complete */ }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        _saved[hash] = new SavedRecord(target, IsBundle: false);
        return new ContinuousSaveReport(
            ContinuousSaveStatus.Saved, target, Path.GetFileName(target), size, hash);
    }

    private ContinuousSaveReport SaveBytes(string displayName, byte[] bytes)
    {
        string hash = ContentStoreSha256(bytes);
        long size = bytes.LongLength;
        if (_saved.TryGetValue(hash, out SavedRecord? seen) &&
            VerifySavedRecord(seen, hash))
        {
            return Skip(displayName, size, hash);
        }
        _saved.Remove(hash);
        string? target = ResolveTarget(displayName, size, hash);
        if (target is null)
        {
            // Same name + same content already in the folder (previous run).
            _saved[hash] = new SavedRecord(
                Path.Combine(_dir, FileNameUtil.Sanitize(displayName)),
                IsBundle: false);
            return Skip(displayName, size, hash);
        }
        WriteAtomic(target, bytes);
        _saved[hash] = new SavedRecord(target, IsBundle: false);
        return new ContinuousSaveReport(
            ContinuousSaveStatus.Saved, target, Path.GetFileName(target), size, hash);
    }

    /// <summary>
    /// Resolve the final target path for a name, or null when an existing
    /// same-name file holds identical content (cross-run duplicate).
    /// </summary>
    private string? ResolveTarget(string displayName, long expectedSize, string expectedHash)
    {
        string safe = FileNameUtil.Sanitize(displayName);
        string first = Path.Combine(_dir, safe);
        if (File.Exists(first))
        {
            if (new FileInfo(first).Length == expectedSize &&
                HashFile(first) == expectedHash)
            {
                return null;
            }
            // Different content under the same name → name(N) via the shared
            // helper (it re-checks existence, starting at (1)).
            return FileNameUtil.UniqueTarget(_dir, safe);
        }
        return first;
    }

    /// <summary>
    /// Re-verify a dedup hit against the folder's actual bytes. A record is
    /// only trustworthy while what it points at is still there unchanged.
    /// </summary>
    private bool VerifySavedRecord(SavedRecord record, string expectedDigest)
    {
        return record.IsBundle
            ? VerifyBundleDir(record.Path, expectedDigest)
            : VerifySingleFile(record.Path, expectedDigest);
    }

    private static bool VerifySingleFile(string path, string expectedDigest)
    {
        return File.Exists(path) && HashFile(path) == expectedDigest;
    }

    /// <summary>
    /// A bundle folder counts as an intact duplicate only when its marker
    /// still parses, still matches the digest, and every manifest member is
    /// present with the recorded size and SHA-256.
    /// </summary>
    private bool VerifyBundleDir(string dir, string expectedDigest)
    {
        BundleMarker? marker = TryReadBundleMarker(dir);
        if (marker is null ||
            !string.Equals(marker.Digest, expectedDigest, StringComparison.OrdinalIgnoreCase) ||
            marker.Members.Count == 0)
        {
            return false;
        }
        foreach (BundleMemberManifestEntry m in marker.Members)
        {
            // Recorded names are our own sanitized final names; reject any
            // path-shaped entry defensively anyway.
            if (Path.GetFileName(m.Name) != m.Name)
            {
                return false;
            }
            string path = Path.Combine(dir, m.Name);
            if (!File.Exists(path) ||
                new FileInfo(path).Length != m.Size ||
                HashFile(path) != m.Sha256)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>First bundle folder whose marker matches AND verifies intact.</summary>
    private string? FindIntactBundleDir(string digest)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(_dir))
            {
                if (VerifyBundleDir(dir, digest))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // Target dir missing (first save) or unreadable — save normally.
        }
        return null;
    }

    private static void WriteBundleMarker(string stagingDir, BundleMarker marker)
    {
        File.WriteAllText(
            Path.Combine(stagingDir, BundleMarkerFileName),
            JsonSerializer.Serialize(marker),
            new UTF8Encoding(false));
    }

    private static BundleMarker? TryReadBundleMarker(string dir)
    {
        try
        {
            string marker = Path.Combine(dir, BundleMarkerFileName);
            if (!File.Exists(marker))
            {
                return null;
            }
            return JsonSerializer.Deserialize<BundleMarker>(
                File.ReadAllText(marker));
        }
        catch
        {
            return null;
        }
    }

    private string UniqueDir(string name)
    {
        string first = Path.Combine(_dir, name);
        if (!Directory.Exists(first) && !File.Exists(first))
        {
            return first;
        }
        int i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(_dir, $"{name}({i})");
            i++;
        }
        while (Directory.Exists(candidate) || File.Exists(candidate));
        return candidate;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort: a leftover .tmp staging dir is inert garbage.
        }
    }

    private static ContinuousSaveReport Skip(string name, long size, string hash) =>
        new(ContinuousSaveStatus.SkippedDuplicate, null, name, size, hash);

    private static string ContentStoreSha256(byte[] bytes)
    {
        // Same digest form as ContentStore.Sha256Hex so hashes stay comparable
        // across the store and the continuous folder.
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Deterministic digest over member names + contents (order kept).</summary>
    private static string BundleDigest(IReadOnlyList<BundleFile> files)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var len = new byte[8];
        foreach (BundleFile f in files)
        {
            BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)f.Name.Length);
            sha.AppendData(len);
            sha.AppendData(Encoding.UTF8.GetBytes(f.Name));
            BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)f.Data.LongLength);
            sha.AppendData(len);
            sha.AppendData(f.Data);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is null) throw new IOException("目标路径没有目录");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
