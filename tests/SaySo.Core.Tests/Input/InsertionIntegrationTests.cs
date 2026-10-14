using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Tests.Input;

/// <summary>
/// Трогают буфер обмена пользователя (текст сохраняется и восстанавливается) и фокус.
/// Нажатия отправляются только если тестовое окно действительно на переднем плане.
/// </summary>
[Trait("Category", "Integration")]
public class InsertionIntegrationTests
{
    [Fact]
    public void Clipboard_SetAndGet_WithHistoryExclusion()
    {
        RunSta(() =>
        {
            var owner = ClipboardOwner.Create();
            ClipboardText.TryGetText(owner, out var saved);
            try
            {
                Assert.True(ClipboardText.SetText(owner, "Проверка буфера 123", excludeFromHistory: true));
                Assert.True(ClipboardText.TryGetText(owner, out var read));
                Assert.Equal("Проверка буфера 123", read);
                Assert.True(Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory")));
            }
            finally
            {
                if (saved is not null)
                {
                    ClipboardText.SetText(owner, saved, excludeFromHistory: true);
                }

                ClipboardOwner.Destroy(owner);
            }
        });
    }

    [Fact]
    public void InsertAsync_IntoForegroundEdit_TypesTextAndRestoresClipboard()
    {
        string? result = null;
        InsertOutcome? outcome = null;
        string? clipboardAfter = null;
        var skipped = false;
        RunSta(() =>
        {
            var owner = ClipboardOwner.Create();
            var edit = TestWindows.CreateEdit();
            ClipboardText.TryGetText(owner, out var saved);
            try
            {
                ClipboardText.SetText(owner, "исходный текст", excludeFromHistory: true);
                Native.SetForegroundWindow(edit);
                TestWindows.Pump(300);
                if (Native.GetForegroundWindow() != edit)
                {
                    skipped = true;
                    return;
                }

                // Окно теста живёт в этом же процессе, а вставка в «своё» окно запрещена: подменяем «свой» PID.
                var inserter = new TextInserter(owner, TimeProvider.System) { OwnProcessId = 0 };
                var task = Task.Run(() => inserter.InsertAsync("Привет, мир", [], expectedForeground: edit), TestContext.Current.CancellationToken);
                while (!task.IsCompleted)
                {
                    TestWindows.Pump(20);
                }

                TestWindows.Pump(300);
                outcome = task.Result;
                result = TestWindows.GetText(edit);
                ClipboardText.TryGetText(owner, out clipboardAfter);
            }
            finally
            {
                if (saved is not null)
                {
                    ClipboardText.SetText(owner, saved, excludeFromHistory: true);
                }

                Native.DestroyWindow(edit);
                ClipboardOwner.Destroy(owner);
            }
        });

        Assert.SkipWhen(skipped, "Тестовое окно не получило фокус (процесс тестов в фоне) — проверить вставку вручную в задаче 15.");
        Assert.Equal(InsertOutcome.Inserted, outcome);
        Assert.Equal("Привет, мир", result);
        Assert.Equal("исходный текст", clipboardAfter);
    }

    [Fact]
    public void Clipboard_HeldByAnotherThread_IsReportedAsUnavailable()
    {
        RunSta(() =>
        {
            var owner = ClipboardOwner.Create();
            ClipboardText.TryGetText(owner, out var saved);
            using var opened = new ManualResetEventSlim();
            var holderOpened = false;
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                var holderOwner = ClipboardOwner.Create();

                // Буфер могут кратко держать сторонние слушатели (менеджеры буфера обмена) — как и в продакшене, пробуем несколько раз.
                for (var i = 0; i < 20 && !holderOpened; i++)
                {
                    holderOpened = Native.OpenClipboard(holderOwner);
                    if (!holderOpened)
                    {
                        Thread.Sleep(15);
                    }
                }

                opened.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                Native.CloseClipboard();
                ClipboardOwner.Destroy(holderOwner);
            });
            holder.Start();
            try
            {
                Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(holderOpened, "поток-держатель должен реально открыть буфер, иначе тест ничего не проверяет");
                // Пока другой поток держит буфер, запись не удаётся (после ~150 мс попыток), а не зависает.
                Assert.False(ClipboardText.SetText(owner, "не должно записаться", excludeFromHistory: true));
            }
            finally
            {
                release.Set();
                holder.Join();
                if (saved is not null)
                {
                    ClipboardText.SetText(owner, saved, excludeFromHistory: true);
                }

                ClipboardOwner.Destroy(owner);
            }
        });
    }

    [Fact]
    public void InsertAsync_ForegroundIsOwnProcessWindow_ReturnsNoTarget_WithoutTyping()
    {
        InsertOutcome? outcome = null;
        string? text = null;
        var skipped = false;
        RunSta(() =>
        {
            var owner = ClipboardOwner.Create();
            var edit = TestWindows.CreateEdit();
            try
            {
                Native.SetForegroundWindow(edit);
                TestWindows.Pump(300);
                if (Native.GetForegroundWindow() != edit)
                {
                    skipped = true;
                    return;
                }

                // Окно теста — окно нашего процесса: как меню трея демона, пока сессия завершается.
                var inserter = new TextInserter(owner, TimeProvider.System);
                var task = Task.Run(() => inserter.InsertAsync("не должно вставиться", [], expectedForeground: edit), TestContext.Current.CancellationToken);
                while (!task.IsCompleted)
                {
                    TestWindows.Pump(20);
                }

                TestWindows.Pump(200);
                outcome = task.Result;
                text = TestWindows.GetText(edit);
            }
            finally
            {
                Native.DestroyWindow(edit);
                ClipboardOwner.Destroy(owner);
            }
        });

        Assert.SkipWhen(skipped, "Тестовое окно не получило фокус (процесс тестов в фоне).");
        Assert.Equal(InsertOutcome.NoTarget, outcome);
        Assert.Equal("", text);
    }

    [Fact]
    public void InsertAsync_WrongForeground_ReturnsNoTarget_WithoutTyping()
    {
        RunSta(() =>
        {
            var owner = ClipboardOwner.Create();
            try
            {
                var inserter = new TextInserter(owner, TimeProvider.System);
                // Окно owner никогда не бывает на переднем плане.
                Assert.Equal(InsertOutcome.NoTarget, inserter.InsertAsync("x", [], expectedForeground: owner).GetAwaiter().GetResult());
            }
            finally
            {
                ClipboardOwner.Destroy(owner);
            }
        });
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
