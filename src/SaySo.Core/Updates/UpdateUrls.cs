namespace SaySo.Core.Updates;

/// <summary>
/// Белый список адресов из ответа GitHub: страница релиза и оба ассета обязаны вести на <c>github.com/Denmurzik/SaySo/releases/…</c>
/// по https (порт по умолчанию, без логина). Хост CDN, на который GitHub перенаправляет скачивание, не фиксируется: проверяется
/// только исходная ссылка.
/// </summary>
public static class UpdateUrls
{
    public const string Host = "github.com";
    public const string PathPrefix = "/Denmurzik/SaySo/releases/";

    public static bool IsTrusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase);
}
