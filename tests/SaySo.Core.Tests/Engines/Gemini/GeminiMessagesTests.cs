using System.Text;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Settings;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiMessagesTests
{
    private static string Str(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void BuildSetup_Smart_AutoLanguage_WithVocabulary()
    {
        var json = Str(GeminiMessages.BuildSetup(new GeminiSessionOptions(
            "gemini-3.5-transcribe-live", TranscriptionMode.Smart, [], ["SaySo", "Кубернетис"])));
        Assert.Equal(
            """{"setup":{"model":"models/gemini-3.5-transcribe-live","generationConfig":{"responseModalities":["TEXT"]},"inputAudioTranscription":{"mode":"SMART","languageCodes":[],"customVocabulary":["SaySo","Кубернетис"]}}}""",
            json);
    }

    [Fact]
    public void BuildSetup_Verbatim_FixedLanguage_NoVocabulary_KeepsModelsPrefix()
    {
        var json = Str(GeminiMessages.BuildSetup(new GeminiSessionOptions(
            "models/custom-model", TranscriptionMode.Verbatim, ["ru-RU"], [])));
        Assert.Equal(
            """{"setup":{"model":"models/custom-model","generationConfig":{"responseModalities":["TEXT"]},"inputAudioTranscription":{"mode":"VERBATIM","languageCodes":["ru-RU"]}}}""",
            json);
    }

    [Fact]
    public void BuildAudio_EncodesBase64Pcm() =>
        Assert.Equal(
            """{"realtimeInput":{"audio":{"data":"AQID","mimeType":"audio/pcm;rate=16000"}}}""",
            Str(GeminiMessages.BuildAudio([1, 2, 3])));

    [Fact]
    public void BuildAudioStreamEnd() =>
        Assert.Equal("""{"realtimeInput":{"audioStreamEnd":true}}""", Str(GeminiMessages.BuildAudioStreamEnd()));
}
