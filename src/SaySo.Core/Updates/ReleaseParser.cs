using System.Text.Json;

namespace SaySo.Core.Updates;

/// <summary>Ответ <c>/releases/latest</c> → <see cref="ReleaseInfo"/>.</summary>
public static class ReleaseParser
{
    public const int MaxNotesLength = 500;

    /// <returns>
    /// Релиз или null, если подходящего релиза нет: тег не вида vX.Y.Z, черновик, пре-релиз, нет адреса страницы.
    /// Ассеты, которых нет в релизе, дают null в соответствующих полях (релиз остаётся «доступным», но не устанавливаемым).
    /// </returns>
    /// <exception cref="JsonException">Ответ — не JSON или не объект релиза.</exception>
    public static ReleaseInfo? Parse(ReadOnlySpan<byte> json)
    {
        var dto = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GitHubRelease)
                  ?? throw new JsonException("Пустой ответ релиза");
        if (dto.Draft || dto.Prerelease || !VersionTag.TryParse(dto.TagName, out var version))
        {
            return null;
        }

        // Страница релиза уйдёт в оболочку, ссылки ассетов — в загрузчик: только github.com/Denmurzik/SaySo/releases/… по https.
        if (!UpdateUrls.IsTrusted(dto.HtmlUrl))
        {
            return null;
        }

        var setupName = ReleaseInfo.SetupFileNameFor(version);
        var shaName = setupName + ".sha256";
        GitHubAsset? setup = null, sha = null;
        foreach (var asset in dto.Assets ?? [])
        {
            if (string.Equals(asset?.Name, setupName, StringComparison.Ordinal))
            {
                setup = asset;
            }
            else if (string.Equals(asset?.Name, shaName, StringComparison.Ordinal))
            {
                sha = asset;
            }
        }

        var setupUrl = UpdateUrls.IsTrusted(setup?.BrowserDownloadUrl) ? setup!.BrowserDownloadUrl : null;
        var shaUrl = UpdateUrls.IsTrusted(sha?.BrowserDownloadUrl) ? sha!.BrowserDownloadUrl : null;
        return new ReleaseInfo(version, dto.TagName!, dto.HtmlUrl!, Truncate(dto.Body), setupUrl, setupUrl is null ? 0 : setup!.Size, shaUrl);
    }

    private static string Truncate(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length <= MaxNotesLength)
        {
            return text;
        }

        // Не режем суррогатную пару пополам.
        var length = char.IsHighSurrogate(text[MaxNotesLength - 1]) ? MaxNotesLength - 1 : MaxNotesLength;
        return text[..length];
    }
}
