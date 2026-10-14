using System.Text;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Storage;

[Collection("Log")] // при сбоях Load пишет в статический Log
public class HistoryStoreTests
{
    private static HistoryEntry Entry(int i) =>
        new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero).AddMinutes(i), "Текст номер " + i, "gemini");

    [Fact]
    public void Load_WhenMissing_ReturnsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(new HistoryStore(new AppPaths(dir.Path)).Load());
    }

    [Fact]
    public void Add_KeepsNewestFirst_AndLimitsTo20()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(new AppPaths(dir.Path));
        for (var i = 0; i < 25; i++)
        {
            store.Add(Entry(i));
        }

        var list = new HistoryStore(new AppPaths(dir.Path)).Load();
        Assert.Equal(HistoryStore.MaxEntries, list.Count);
        Assert.Equal("Текст номер 24", list[0].Text);
        Assert.Equal("Текст номер 5", list[^1].Text);
    }

    [Fact]
    public void File_IsEncrypted()
    {
        // Маркер ASCII: System.Text.Json экранирует кириллицу в \uXXXX, поэтому по русскому тексту проверка была бы пустой.
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        new HistoryStore(paths).Add(new HistoryEntry(DateTimeOffset.UnixEpoch, "SECRETMARK-1", "gemini"));
        var raw = File.ReadAllBytes(paths.HistoryFile);

        Assert.DoesNotContain("SECRETMARK-1", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("SECRETMARK-1", Encoding.Unicode.GetString(raw));
        // Контроль: после расшифровки маркер на месте, значит проверка выше действительно что-то проверяет.
        Assert.Contains("SECRETMARK-1", Encoding.UTF8.GetString(Dpapi.Unprotect(raw)));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyAndKeepsBackup()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllBytes(paths.HistoryFile, [1, 2, 3, 4]);

        Assert.Empty(new HistoryStore(paths).Load());
        Assert.True(File.Exists(paths.HistoryFile + ".bad"));
    }

    [Fact]
    public void Load_FileBrieflyLocked_RetriesAndReturnsContent()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new HistoryStore(paths);
        store.Add(Entry(7));

        using (new HeldFile(paths.HistoryFile).ReleaseAfter(TimeSpan.FromMilliseconds(80)))
        {
            var list = store.Load();
            Assert.Single(list);
            Assert.Equal("Текст номер 7", list[0].Text);
        }
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(new AppPaths(dir.Path));
        store.Add(Entry(1));
        store.Clear();
        Assert.Empty(store.Load());
    }
}
