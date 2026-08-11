using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AirFerry.Windows.Bundle;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Views;

/// <summary>
/// Received-file history browser — mirrors Android's <c>FileListActivity</c>.
/// Lists logical entries from <see cref="ContentStore"/>. Opening is always
/// handled inside AirFerry; untrusted received files are never shell-executed.
/// </summary>
public partial class FileListView : Page
{
    private readonly ObservableCollection<FileEntry> _entries = [];
    private readonly ObservableCollection<TaskEntry> _tasks = [];

    public FileListView()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _entries;
        TasksListView.ItemsSource = _tasks;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _entries.Clear();
        _tasks.Clear();
        PathHint.Text = $"位置: {ContentStore.RootDir}";
        foreach (SegmentAssembler.TaskInfo task in SegmentAssembler.ListTasks())
        {
            _tasks.Add(new TaskEntry(
                task.RootSessionIdHex,
                task.FileName,
                $"{task.ReceivedCount}/{task.SegmentCount} · 缺 {FormatMissingSegments(task)}",
                FormatSize((ulong)task.RootOriginalSize),
                task.RootLo,
                task.RootHi));
        }
        TasksPanel.Visibility = _tasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IReadOnlyList<ContentStore.Entry> entries;
        try
        {
            ContentStore.MigrateLegacyReceivedIfNeeded();
            entries = ContentStore.ListEntries();
        }
        catch (InvalidDataException ex)
        {
            ClearButton.IsEnabled = _tasks.Count > 0;
            PathHint.Text = ex.Message;
            MessageBox.Show(ex.Message, "AirFerry", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        ClearButton.IsEnabled = entries.Count > 0 || _tasks.Count > 0;
        foreach (ContentStore.Entry item in entries.OrderByDescending(e => e.CreatedAt))
        {
            string path = ContentStore.BlobPath(item.Hash);
            _entries.Add(new FileEntry(
                item.Id,
                item.BundleTitle is null ? item.Name : $"{item.BundleTitle} / {item.Name}",
                item.Name,
                FormatSize((ulong)item.Size),
                DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                path,
                item.Kind,
                item.CrcHex,
                item.CrcUnknown));
        }
    }

    private void FileList_DoubleClick(object sender, RoutedEventArgs e)
    {
        if (FilesListView.SelectedItem is not FileEntry entry)
        {
            return;
        }
        if (!File.Exists(entry.FullPath))
        {
            MessageBox.Show("内容文件已丢失或损坏。", "AirFerry",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        try
        {
            long len = new FileInfo(entry.FullPath).Length;
            byte[]? bytes = null;
            bool textCandidate = entry.Kind == "text" || FileNameUtil.IsTextLikeName(entry.Name);
            if (textCandidate && FileNameUtil.FitsTextUi(len))
            {
                bytes = File.ReadAllBytes(entry.FullPath);
                string? text = FileNameUtil.DecodeUtf8Strict(bytes);
                if (text is not null)
                {
                    var textResult = BuildResult(entry, (ulong)bytes.Length,
                        Crc32.Compute(bytes), text);
                    NavigationService?.Navigate(new ReceiveTextView(textResult, entry.Name));
                    return;
                }
            }
            ulong receivedCrc;
            if (bytes is not null)
            {
                receivedCrc = Crc32.Compute(bytes);
            }
            else
            {
                using FileStream stream = File.OpenRead(entry.FullPath);
                receivedCrc = Crc32.Compute(stream);
            }
            NavigationService?.Navigate(new ReceiveDetailView(
                BuildResult(entry, (ulong)len, receivedCrc, null)));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开: {ex.Message}", "AirFerry",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空所有已接收文件和待恢复任务？此操作不可撤销。", "AirFerry",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            ContentStore.ClearAll();
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清空失败: {ex.Message}", "AirFerry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContinueTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskEntry task }) return;
        // Device selection remains explicit; after choosing the camera the
        // scanner accepts only this root and reopens its durable bitmap when a
        // matching segment is shown. No already-verified segment is rewritten.
        NavigationService?.Navigate(new DeviceSelectView(task.RootSessionIdHex));
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskEntry task }) return;
        if (MessageBox.Show($"删除「{task.DisplayName}」的待恢复分段？", "AirFerry",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            SegmentAssembler.Discard(task.RootLo, task.RootHi);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除任务失败: {ex.Message}", "AirFerry",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();

    private static string FormatSize(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string FormatMissingSegments(SegmentAssembler.TaskInfo task)
    {
        var have = task.ReceivedIndices.ToHashSet();
        var ranges = new List<string>();
        bool omitted = false;
        for (int i = 0; i < task.SegmentCount; i++)
        {
            if (have.Contains(i)) continue;
            int start = i;
            while (i + 1 < task.SegmentCount && !have.Contains(i + 1)) i++;
            if (ranges.Count < 4)
                ranges.Add(start == i ? $"{start + 1}" : $"{start + 1}–{i + 1}");
            else
                omitted = true;
        }
        return ranges.Count == 0 ? "无" : string.Join("、", ranges) + (omitted ? " 等" : "");
    }

    private static RecoveryResult BuildResult(
        FileEntry entry, ulong size, ulong receivedCrc, string? text)
    {
        ulong expected = 0;
        bool parsed = !entry.CrcUnknown && ulong.TryParse(entry.CrcHex,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out expected);
        return new RecoveryResult(
            SingleFilePath: entry.FullPath,
            SingleFileSize: size,
            ExpectedCrc32: parsed ? expected : null,
            Crc32Known: parsed,
            ReceivedCrc32: receivedCrc,
            Bundle: null,
            BundleDir: null,
            Text: text,
            DisplayName: entry.Name);
    }

    public sealed record FileEntry(
        string Id,
        string DisplayName,
        string Name,
        string SizeText,
        string ModifiedText,
        string FullPath,
        string Kind,
        string CrcHex,
        bool CrcUnknown);

    public sealed record TaskEntry(
        string RootSessionIdHex,
        string DisplayName,
        string ProgressText,
        string SizeText,
        ulong RootLo,
        ulong RootHi);
}
