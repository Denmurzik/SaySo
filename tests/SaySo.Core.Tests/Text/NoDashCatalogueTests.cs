using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Engines;
using SaySo.Core.Hotkeys;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Text;

/// <summary>В строках интерфейса нет длинного (—) и среднего (–) тире: каталоги сообщений, подсказки трея, тексты обновлений.</summary>
public class NoDashCatalogueTests
{
    private static void AssertNoDashes(IEnumerable<string> texts, int atLeast)
    {
        var all = texts.Distinct().ToList();

        Assert.True(all.Count >= atLeast, $"каталог перечислил подозрительно мало строк: {all.Count}");
        Assert.DoesNotContain(all, t => t.AsSpan().IndexOfAny('—', '–') >= 0);
    }

    private static IEnumerable<string> VersionedUpdateTexts()
    {
        var version = new Version(0, 2, 0);
        yield return UpdateMessages.BalloonTitle;
        yield return UpdateMessages.UpToDate;
        foreach (var manual in new[] { false, true })
        {
            yield return UpdateMessages.Balloon(version, manual);
            yield return UpdateMessages.MenuItem(version, manual);
            yield return UpdateMessages.Available(version, manual);
        }

        foreach (var failure in Enum.GetValues<UpdateFailure>())
        {
            yield return UpdateMessages.CheckFailed(failure);
        }
    }

    [Fact]
    public void OverlayCatalogue_HasNoDashes() => AssertNoDashes(OverlayMessages.All(), atLeast: 50);

    [Fact]
    public void OverlayDetails_HaveNoDashes() => AssertNoDashes(OverlayMessages.AllDetails(), atLeast: 50);

    [Fact]
    public void UpdateTexts_HaveNoDashes() => AssertNoDashes(VersionedUpdateTexts(), atLeast: 10);

    [Fact]
    public void EngineErrorTexts_HaveNoDashes()
    {
        var texts = new List<string>();
        foreach (var engine in new[] { "Gemini", "Groq" })
        {
            foreach (var kind in Enum.GetValues<EngineErrorKind>())
            {
                texts.Add(ErrorMessages.Describe(kind, engine));
                texts.Add(ErrorMessages.DescribeShort(kind, engine));
            }
        }

        AssertNoDashes(texts, atLeast: 10);
    }

    [Fact]
    public void MicrophoneExceptionTexts_HaveNoDashes()
    {
        var texts = new List<string>();
        foreach (var kind in Enum.GetValues<MicrophoneErrorKind>())
        {
            var e = new MicrophoneException(kind, unchecked((int)0x80004005), "тест");
            texts.Add(e.UserMessage);
            texts.Add(e.Detail);
        }

        AssertNoDashes(texts, atLeast: 5);
    }

    [Fact]
    public void HotkeyValidatorTexts_HaveNoDashes()
    {
        var invalid = new int[][]
        {
            [],
            [VirtualKeys.Escape],
            [0xA2, 0xA0, 0xA4, 0x5B, 0x41], // слишком много клавиш
            [0x41],                         // одна буква
            [0xA0, 0x41],                   // Shift + буква
        };
        var texts = invalid.Select(keys => HotkeyValidator.Validate(keys)).ToList();

        Assert.All(texts, Assert.NotNull);
        AssertNoDashes(texts.Select(t => t!), atLeast: 4);
    }

    [Fact]
    public void TrayTexts_HaveNoDashes() => AssertNoDashes(
    [
        TrayTooltip.Paused,
        TrayTooltip.Recording,
        TrayTooltip.Idle("Правый Ctrl"),
        TrayTooltip.ForError(null),
        TrayTooltip.ForError("Gemini: нет связи с сервером; Groq: ключ API недействителен"),
    ], atLeast: 5);

#if WINDOWS
    [Fact]
    public void AdminTaskDescription_HasNoDashes()
    {
        var xml = AdminTask.BuildXml(@"C:\SaySo\SaySo.exe", @"PC\User");

        Assert.DoesNotContain('—', xml);
        Assert.DoesNotContain('–', xml);
    }
#endif
}
