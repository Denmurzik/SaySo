using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

/// <summary>
/// Ставит глобальный хук клавиатуры и инжектирует неназначенную клавишу 0xE8 — на работу других
/// приложений это не влияет.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Hook")]
public class HookIntegrationTests
{
    private const ushort Unassigned = 0xE8;

    private static unsafe void Inject(ushort vk, bool up, nuint extra)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0, dwExtraInfo = extra } },
        };
        Native.SendInput(1, &input, sizeof(Native.INPUT));
    }

    [Fact]
    public void Hook_SeesInjectedKey_AndMarksOurOwn()
    {
        var events = new List<KeyEvent>();
        using (var hook = new LowLevelKeyboardHook(e =>
        {
            if (e.VirtualKey == Unassigned)
            {
                lock (events)
                {
                    events.Add(e);
                }
            }

            return false;
        }))
        {
            Inject(Unassigned, up: false, KeyboardInjector.InjectionMarker);
            Inject(Unassigned, up: true, KeyboardInjector.InjectionMarker);
            Inject(Unassigned, up: false, 0);
            Inject(Unassigned, up: true, 0);
            Thread.Sleep(300);
        }

        lock (events)
        {
            Assert.Equal(4, events.Count);
            Assert.True(events[0].IsInjected && events[0].IsDown);
            Assert.True(events[1].IsInjected && !events[1].IsDown);
            Assert.False(events[2].IsInjected);
        }
    }

    [Fact]
    public void Listener_PostsStartAndStop_ForHotkey()
    {
        const uint Action = Native.WM_APP + 9;
        var actions = new List<HotkeyAction>();
        var t = new Thread(() =>
        {
            using var window = new MessageWindow("SaySo.Test.Listener." + Guid.NewGuid().ToString("N"), (msg, w, _) =>
            {
                if (msg != Action)
                {
                    return null;
                }

                actions.Add((HotkeyAction)w);
                if (actions.Count == 2)
                {
                    Native.PostQuitMessage(0);
                }

                return 0;
            }, messageOnly: true);
            using var listener = new HotkeyListener(window.Handle, Action, [Unassigned], 300);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Inject(Unassigned, up: false, 0);   // старт
                Thread.Sleep(80);
                Inject(Unassigned, up: true, 0);    // короткое нажатие → «руки свободны»
                Thread.Sleep(80);
                Inject(Unassigned, up: false, 0);   // стоп
                Inject(Unassigned, up: true, 0);
            });
            MessageLoop.Run();
        });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal([HotkeyAction.StartRecording, HotkeyAction.StopRecording], actions);
    }
}
