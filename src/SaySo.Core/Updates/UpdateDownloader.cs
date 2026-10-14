using System.Security.Cryptography;
using System.Text;
using SaySo.Core.Diagnostics;

namespace SaySo.Core.Updates;

public enum DownloadFailure
{
    /// <summary>В релизе нет Setup.exe или его контрольной суммы.</summary>
    NotInstallable,
    Network,
    Timeout,

    /// <summary>Файл .sha256 не разобрать или он относится к другому файлу.</summary>
    ChecksumFileInvalid,
    SizeMismatch,
    HashMismatch,

    /// <summary>Не удалось записать файл на диск.</summary>
    Io,
}

/// <summary>
/// Проверенный Setup.exe. Файл открыт на чтение без разрешения записи и удаления до <see cref="Dispose"/>: между
/// проверкой SHA-256 и запуском (в том числе с повышением прав) его не подменить. Как файл-страж в <c>AdminTask.Install</c>.
/// </summary>
public sealed class StagedInstaller : IDisposable
{
    private readonly FileStream _guard;

    internal StagedInstaller(string path, FileStream guard)
    {
        Path = path;
        _guard = guard;
    }

    public string Path { get; }

    public void Dispose() => _guard.Dispose();

    /// <summary>Освободить файл и удалить его (отмена обновления).</summary>
    public void Delete()
    {
        _guard.Dispose();
        UpdateDownloader.TryDelete(Path);
    }
}

public sealed record DownloadResult(StagedInstaller? Installer, DownloadFailure? Failure)
{
    public static DownloadResult Fail(DownloadFailure failure) => new(null, failure);
}

/// <summary>
/// Скачивает Setup.exe релиза в <c>%TEMP%\SaySo-Update</c> и сверяет размер и SHA-256 с .sha256 того же релиза.
/// При любом несовпадении файл удаляется и ничего не запускается.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string directory)
{
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly string[] OwnFilePatterns = ["SaySo-Setup-*.exe", "SaySo-Setup-*.exe.part"];

    private const int MaxChecksumFileBytes = 4096;
    private const int BufferSize = 81920;

    public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (!release.IsInstallable)
        {
            return DownloadResult.Fail(DownloadFailure.NotInstallable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        var finalPath = Path.Combine(directory, release.SetupFileName);
        var partPath = finalPath + ".part";
        try
        {
            if (!PrepareDirectory())
            {
                return DownloadResult.Fail(DownloadFailure.Io);
            }

            CleanOwnFiles();

            var expected = await ReadChecksumAsync(release, timeout.Token).ConfigureAwait(false);
            if (expected is null)
            {
                return DownloadResult.Fail(DownloadFailure.ChecksumFileInvalid);
            }

            var failure = await DownloadFileAsync(release, partPath, timeout.Token).ConfigureAwait(false);
            if (failure is not null)
            {
                return DownloadResult.Fail(failure.Value);
            }

            // Перезаписи нет: старый файл (наш же) удалён, а чужую ссылку/файл на его месте Move не затрёт, а откажет.
            TryDelete(finalPath);
            File.Move(partPath, finalPath);
            return Verify(finalPath, release.SetupSize, expected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return DownloadResult.Fail(DownloadFailure.Network);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Скачивание обновления", e);
            return DownloadResult.Fail(DownloadFailure.Io);
        }
        finally
        {
            TryDelete(partPath);
        }
    }

    /// <summary>Строка вида «sha256-hex  SaySo-Setup-X.Y.Z.exe» (имя необязательно, перед ним допустима «*»).</summary>
    /// <returns>Хэш в нижнем регистре или null, если строка не разобрана или названа по другому файлу.</returns>
    public static string? ParseChecksum(string text, string expectedFileName)
    {
        var line = text.Trim('﻿', ' ', '\t', '\r', '\n');
        if (line.Contains('\n'))
        {
            return null; // ожидается одна строка
        }

        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
        {
            return null;
        }

        if (parts.Length == 2 && !string.Equals(parts[1].TrimStart('*'), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[0].ToLowerInvariant();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Файл остаётся в TEMP: следующее скачивание его зачистит.
        }
    }

    private async Task<string?> ReadChecksumAsync(ReleaseInfo release, CancellationToken ct)
    {
        using var response = await http.GetAsync(release.ShaUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Контрольная сумма недоступна", null, response.StatusCode);
        }

        var bytes = await HttpLimits.ReadAsync(response.Content, MaxChecksumFileBytes, ct).ConfigureAwait(false);
        return bytes is null ? null : ParseChecksum(DecodeText(bytes), release.SetupFileName);
    }

    /// <summary>
    /// UTF-8 (с BOM или без) и UTF-16/32 с BOM: Windows PowerShell 5.1 пишет файлы через <c>Out-File</c> в UTF-16, и
    /// сумма из такого релиза иначе никогда бы не прошла проверку. Нули — на случай UTF-16 без BOM.
    /// </summary>
    internal static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\0", "");
    }

    private async Task<DownloadFailure?> DownloadFileAsync(ReleaseInfo release, string partPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Установщик недоступен", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared != release.SetupSize)
        {
            return DownloadFailure.SizeMismatch; // тело не качаем
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // CreateNew после удаления: символическая ссылка на месте .part не перенаправит запись в другой файл.
        TryDelete(partPath);
        await using var target = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await HttpLimits.ReadNetworkAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > release.SetupSize)
            {
                return DownloadFailure.SizeMismatch; // больше заявленного: дальше не качаем
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return total == release.SetupSize ? null : DownloadFailure.SizeMismatch;
    }

    /// <summary>Открывает файл-страж и сверяет размер и хэш уже по нему: после этого содержимое изменить нельзя.</summary>
    private static DownloadResult Verify(string path, long expectedSize, string expectedHash)
    {
        var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (guard.Length != expectedSize)
            {
                guard.Dispose();
                TryDelete(path);
                return DownloadResult.Fail(DownloadFailure.SizeMismatch);
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(guard));
            if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            {
                guard.Dispose();
                TryDelete(path);
                return DownloadResult.Fail(DownloadFailure.HashMismatch);
            }

            guard.Position = 0;
            return new DownloadResult(new StagedInstaller(path, guard), null);
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Папка должна быть настоящим каталогом: если она (или созданные нами родители) — точка повторной обработки
    /// (символическая ссылка, junction), скачивание отменяется — иначе Setup оказался бы в чужом месте.
    /// </summary>
    private bool PrepareDirectory()
    {
        var created = new List<DirectoryInfo>();
        for (var d = new DirectoryInfo(directory); d is { Exists: false }; d = d.Parent)
        {
            created.Add(d);
        }

        Directory.CreateDirectory(directory);
        created.Add(new DirectoryInfo(directory));
        foreach (var d in created)
        {
            d.Refresh();
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Log.Info("Папка обновления — точка повторной обработки: скачивание отменено");
                return false;
            }
        }

        return true;
    }

    // Остатки прошлых обновлений (наши SaySo-Setup-*.exe и .part) в папке больше не нужны; чужое не трогаем и по папкам не ходим.
    private void CleanOwnFiles()
    {
        foreach (var pattern in OwnFilePatterns)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
        }
    }
}
