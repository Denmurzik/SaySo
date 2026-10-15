using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Tests.Input;

namespace SaySo.Core.Tests.Platform;

[Trait("Category", "Integration")]
public class OverlayWindowIntegrationTests
{
    // Повторное поднятие плашки идёт раз в 250 мс; запас на загруженную машину.
    private const int RaiseWaitMs = 700;

    // Скрытие длится 280 мс (растворение при «Уменьшении движения» — 150 мс), страховочный таймер — 1,5 с.
    private const int ReleaseWaitMs = 2500;

    [Fact]
    public void Window_IsLayeredClickThroughNoActivateTopmost_WithoutRedirectionBitmap() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);

        var ex = TestWindows.ExStyle(overlay.Handle);

        foreach (var (flag, name) in new[]
                 {
                     (Native.WS_EX_LAYERED, "WS_EX_LAYERED"), (Native.WS_EX_TRANSPARENT, "WS_EX_TRANSPARENT"),
                     (Native.WS_EX_NOACTIVATE, "WS_EX_NOACTIVATE"), (Native.WS_EX_TOOLWINDOW, "WS_EX_TOOLWINDOW"),
                     (Native.WS_EX_TOPMOST, "WS_EX_TOPMOST"), (Native.WS_EX_NOREDIRECTIONBITMAP, "WS_EX_NOREDIRECTIONBITMAP"),
                 })
        {
            Assert.True((ex & flag) != 0, "нет стиля " + name);
        }
    });

    [Fact]
    public void ShowAllModes_DoesNotStealFocus_AndHidesWithReleasedScene() => OnStaThread(() =>
    {
        var before = Native.GetForegroundWindow();
        using var overlay = new OverlayWindow(() => 0.7f);
        overlay.ShowRecording();
        TestWindows.Pump(300);
        overlay.ShowProcessing();
        TestWindows.Pump(300);
        overlay.ShowMessage("Проверка сообщения", OverlayTone.Error);
        TestWindows.Pump(300);
        Assert.Equal(before, Native.GetForegroundWindow());
        Assert.True(overlay.HasScene);

        overlay.Hide();
        WaitReleased(overlay);

        Assert.False(TestWindows.IsVisible(overlay.Handle));
        Assert.Equal(before, Native.GetForegroundWindow());
    });

    [Fact]
    public void ShownCapsule_LetsClicksThrough() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0.5f);
        overlay.ShowRecording();
        TestWindows.Pump(700); // пружина выезда успокоилась
        try
        {
            var layout = overlay.Layout!.Value;
            var center = new Native.POINT
            {
                X = layout.WindowLeft + layout.CapsuleLeft + layout.CapsuleMaxWidth / 2,
                Y = layout.WindowTop + layout.EndY + layout.CapsuleHeight / 2,
            };

            Assert.NotEqual(overlay.Handle, Native.WindowFromPoint(center));
        }
        finally
        {
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void ShownOverlay_IsInTopmostBand() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowRecording();
        try
        {
            Assert.True(TestWindows.IsTopmost(overlay.Handle), "у плашки нет WS_EX_TOPMOST");
            Assert.True(TestWindows.IsVisible(overlay.Handle));
        }
        finally
        {
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void Recording_TopmostWindowCreatedLater_DoesNotStayAboveOverlay() => OnStaThread(() =>
    {
        var before = Native.GetForegroundWindow();
        using var overlay = new OverlayWindow(() => 0.7f);
        overlay.ShowRecording();
        TestWindows.Pump(100);

        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300);
        try
        {
            Assert.False(TestWindows.IsAbove(overlay.Handle, cover), "стенд: чужое topmost-окно должно сначала оказаться над плашкой");
            TestWindows.Pump(RaiseWaitMs);

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка осталась под чужим topmost-окном");
            Assert.Equal(before, Native.GetForegroundWindow()); // поднятие не берёт фокус
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void Message_TopmostWindowCreatedLater_DoesNotStayAboveOverlay() => OnStaThread(() =>
    {
        // У сообщения нет таймера уровня: поднимать плашку должен отдельный механизм.
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowMessage("Речь не распознана", OverlayTone.Info);
        TestWindows.Pump(100);

        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300);
        try
        {
            Assert.False(TestWindows.IsAbove(overlay.Handle, cover), "стенд: чужое topmost-окно должно сначала оказаться над плашкой");
            TestWindows.Pump(RaiseWaitMs);

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка осталась под чужим topmost-окном");
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void Show_AfterTopmostWindowAppeared_PutsOverlayAboveItImmediately() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300); // плашка создана раньше, окно-конкурент появилось позже
        try
        {
            overlay.ShowRecording();

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка показана под чужим topmost-окном");
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    // KillTimer возвращает true, только если таймер существовал: так видно, какие таймеры живы у окна.
    private static bool TimerExists(OverlayWindow overlay, nuint id) => Native.KillTimer(overlay.Handle, id);

    [Fact]
    public void WhileVisible_TopmostTimerExists_AndLevelTimerOnlyWhileRecording() => OnStaThread(() =>
    {
        using var recording = new OverlayWindow(() => 0);
        recording.ShowRecording();
        Assert.True(TimerExists(recording, OverlayWindow.TopmostTimer));
        Assert.True(TimerExists(recording, OverlayWindow.LevelTimer));
        recording.Hide();
        WaitReleased(recording);

        using var processing = new OverlayWindow(() => 0);
        processing.ShowProcessing();
        Assert.True(TimerExists(processing, OverlayWindow.TopmostTimer));
        Assert.False(TimerExists(processing, OverlayWindow.LevelTimer)); // точки пульсируют анимацией компоновщика
        processing.Hide();
        WaitReleased(processing);

        using var message = new OverlayWindow(() => 0);
        message.ShowMessage("Речь не распознана", OverlayTone.Info);
        Assert.True(TimerExists(message, OverlayWindow.TopmostTimer));
        Assert.True(TimerExists(message, OverlayWindow.HideTimer));
        Assert.False(TimerExists(message, OverlayWindow.LevelTimer));
        message.Hide();
        WaitReleased(message);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hide_StopsTimersAtOnce_AndAfterAnimationLeavesNoTimersAndNoScene(bool message) => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        if (message)
        {
            overlay.ShowMessage("Речь не распознана", OverlayTone.Info);
        }
        else
        {
            overlay.ShowRecording();
        }

        TestWindows.Pump(100);
        overlay.Hide();

        Assert.False(TimerExists(overlay, OverlayWindow.TopmostTimer), "таймер подъёма пережил Hide()");
        Assert.False(TimerExists(overlay, OverlayWindow.LevelTimer), "таймер уровня пережил Hide()");
        Assert.False(TimerExists(overlay, OverlayWindow.HideTimer));

        WaitReleased(overlay);

        Assert.False(TimerExists(overlay, OverlayWindow.ReleaseTimer), "страховочный таймер пережил освобождение");
        Assert.False(TestWindows.IsVisible(overlay.Handle));
    });

    [Fact]
    public void Release_ClosesDispatcherQueue_AndRaisesReleasedOnce() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        var released = 0;
        overlay.Released += () => released++;
        overlay.ShowMessage("Нет сети, текст в буфере обмена", OverlayTone.Error);
        TestWindows.Pump(300);
        Assert.NotNull(Windows.System.DispatcherQueue.GetForCurrentThread());

        overlay.Hide();
        WaitReleased(overlay);
        TestWindows.Pump(100); // ShutdownQueueAsync завершается на цикле сообщений

        Assert.Equal(1, released);
        Assert.Null(Windows.System.DispatcherQueue.GetForCurrentThread());
    });

    [Fact]
    public void Text_GeometrySourcesAreFreed_AfterReleaseAndCollection() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowMessage("Резерв Groq (Gemini: лимит исчерпан)", OverlayTone.Info);
        TestWindows.Pump(200);
        overlay.ShowMessage("Речь не распознана", OverlayTone.Info);
        TestWindows.Pump(300);
        overlay.Hide();
        WaitReleased(overlay);

        for (var i = 0; i < 3 && GeometrySource.Live > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TestWindows.Pump(50);
        }

        Assert.Equal(0, GeometrySource.Live);
    });

    [Fact]
    public void ShowDuringHide_ReversesWithoutRelease() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0.4f);
        var released = 0;
        overlay.Released += () => released++;
        overlay.ShowRecording();
        TestWindows.Pump(600);
        overlay.Hide();
        TestWindows.Pump(100); // скрытие в разгаре

        overlay.ShowRecording();
        TestWindows.Pump(1800); // дольше и анимации скрытия, и страховочного таймера

        Assert.Equal(0, released);
        Assert.True(overlay.HasScene);
        Assert.True(TestWindows.IsVisible(overlay.Handle));
        Assert.True(TimerExists(overlay, OverlayWindow.TopmostTimer));
        Assert.False(TimerExists(overlay, OverlayWindow.ReleaseTimer));
        overlay.Hide();
        WaitReleased(overlay);
    });

    [Fact]
    public void RepeatedSessions_RecreateQueueAndCompositor_EvenRightAfterRelease() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0.6f);
        for (var i = 0; i < 3; i++)
        {
            overlay.ShowRecording();
            TestWindows.Pump(200);
            overlay.ShowMessage("Речь не распознана", OverlayTone.Info);
            TestWindows.Pump(200);
            overlay.Hide();
            WaitReleased(overlay, pumpAfter: false);

            // Сразу, без прокачки сообщений: закрытие прежней очереди может быть ещё не завершено.
            overlay.ShowProcessing();
            Assert.True(overlay.HasScene, $"сессия {i}: сцена не создалась сразу после освобождения прежней");
            TestWindows.Pump(200);
            Assert.True(TestWindows.IsVisible(overlay.Handle));
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void SettingChange_ImmersiveColorSet_ReappliesMaterialWhileVisible() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        var material = OverlayMaterial.Acrylic;
        overlay.MaterialOverride = () => material;
        overlay.ShowRecording();
        try
        {
            Assert.Equal(OverlayMaterial.Acrylic, overlay.Material);

            material = OverlayMaterial.Solid; // например, выключили «Эффекты прозрачности»
            TestWindows.SendSettingChange(overlay.Handle, "Policy");
            Assert.Equal(OverlayMaterial.Acrylic, overlay.Material); // чужой раздел настроек материал не трогает

            TestWindows.SendSettingChange(overlay.Handle, "ImmersiveColorSet");
            Assert.Equal(OverlayMaterial.Solid, overlay.Material);
        }
        finally
        {
            overlay.Hide();
            WaitReleased(overlay);
        }
    });

    [Fact]
    public void ReducedMotion_FadesInAndOut_AndReleasesWithoutTheSafetyTimer() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0.5f);
        overlay.ReducedMotionOverride = () => true;
        overlay.ShowRecording();
        TestWindows.Pump(300);
        Assert.True(overlay.SceneReducedMotion);
        Assert.True(TestWindows.IsVisible(overlay.Handle));
        overlay.ShowMessage("Речь не распознана", OverlayTone.Info); // смена содержимого без растворения
        TestWindows.Pump(200);

        var hiddenAt = Environment.TickCount64;
        overlay.Hide();
        WaitReleased(overlay);

        // Растворение 150 мс: сцена освобождена по концу анимации, а не страховочным таймером (1,5 с).
        Assert.InRange(Environment.TickCount64 - hiddenAt, 0, 1200);
        Assert.False(TestWindows.IsVisible(overlay.Handle));
    });

    [Fact]
    public void HiddenOverlay_IsNotRaisedOrShownAgain() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowRecording();
        overlay.Hide();
        WaitReleased(overlay);
        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300);
        try
        {
            TestWindows.Pump(RaiseWaitMs);

            Assert.False(TestWindows.IsVisible(overlay.Handle), "скрытая плашка снова показана");
            Assert.False(TestWindows.IsAbove(overlay.Handle, cover));
        }
        finally
        {
            TestWindows.Destroy(cover);
        }
    });

    private static void WaitReleased(OverlayWindow overlay, bool pumpAfter = true)
    {
        var until = Environment.TickCount64 + ReleaseWaitMs;
        while (overlay.HasScene && Environment.TickCount64 < until)
        {
            TestWindows.Pump(20);
        }

        Assert.False(overlay.HasScene, "сцена не освобождена после скрытия");
        if (pumpAfter)
        {
            TestWindows.Pump(20);
        }
    }

    private static void OnStaThread(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
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
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }
}
