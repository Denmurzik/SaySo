using System.Text.Json;
using SaySo.Core.Diagnostics;
using SaySo.Core.Json;
using SaySo.Core.Storage;

namespace SaySo.Core.Updates;

/// <summary>update-state.json: когда проверяли в последний раз и о какой версии пользователь уже отказался слушать.</summary>
public sealed class UpdateState
{
    /// <summary>Время последней проверки (UTC); null — проверок не было.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>Версия X.Y.Z, о которой всплывающее уведомление уже закрыли; null — нет такой.</summary>
    public string? DismissedVersion { get; set; }
}

/// <summary>Чтение и атомарная запись update-state.json. Состояние второстепенное: любые сбои — в журнал, а не наружу.</summary>
public sealed class UpdateStateStore(AppPaths paths)
{
    public UpdateState Load()
    {
        var status = StoreFile.Read(paths.UpdateStateFile, "UpdateStateStore", out var bytes);
        if (bytes is null || status != FileReadStatus.Ok)
        {
            return new UpdateState();
        }

        try
        {
            var state = JsonSerializer.Deserialize(bytes, SaySoJsonContext.Default.UpdateState) ?? new UpdateState();
            if (state.DismissedVersion is { } dismissed && !VersionTag.TryParse(dismissed, out _))
            {
                state.DismissedVersion = null; // испорченное значение хуже его отсутствия
            }

            return state;
        }
        catch (JsonException e)
        {
            StoreFile.Quarantine(paths.UpdateStateFile, "UpdateStateStore", e);
            return new UpdateState();
        }
    }

    /// <returns>false — записать не удалось (причина в журнале).</returns>
    public bool Save(UpdateState state)
    {
        try
        {
            AtomicFile.WriteAllBytes(paths.UpdateStateFile,
                JsonSerializer.SerializeToUtf8Bytes(state, SaySoJsonContext.Default.UpdateState));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("UpdateStateStore.Save", e); // окно настроек и демон пишут независимо: один из них мог застать файл занятым
            return false;
        }
    }
}

/// <summary>Правила: когда проверять и о чём напоминать всплывающим уведомлением.</summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Прошли сутки с последней проверки. Время из будущего (часы переводили назад) — тоже повод проверить.</summary>
    public static bool IsCheckDue(UpdateState state, DateTimeOffset nowUtc) =>
        state.LastCheckUtc is not { } last || last > nowUtc || nowUtc - last >= CheckInterval;

    /// <summary>О версии, которую уже закрывали, всплывающее уведомление больше не показывается (остаётся пункт меню).</summary>
    public static bool ShouldNotify(UpdateState state, Version available) =>
        !string.Equals(state.DismissedVersion, VersionTag.Normalize(available).ToString(3), StringComparison.Ordinal);
}
