using System.Text;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Storage;

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
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        new HistoryStore(paths).Add(Entry(1));
        var raw = File.ReadAllBytes(paths.HistoryFile);
        Assert.DoesNotContain("Текст номер 1", Encoding.UTF8.GetString(raw));
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
