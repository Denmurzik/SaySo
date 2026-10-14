namespace SaySo.Core.Dictation;

/// <summary>Создаёт реплики диктовки.</summary>
public sealed class DictationPipeline(IDictationEngines engines)
{
    /// <summary>≈ −45 dBFS: всё тише считается тишиной.</summary>
    public const float SilenceLevel = 0.25f;

    public const int MinSpeechMs = 300;

    public static readonly TimeSpan PrimaryCompleteTimeout = TimeSpan.FromSeconds(15);

    public DictationRun Begin() => new(engines);
}
