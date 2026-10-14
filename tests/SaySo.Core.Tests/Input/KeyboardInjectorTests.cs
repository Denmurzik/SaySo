using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Tests.Input;

public class KeyboardInjectorTests
{
    [Fact]
    public unsafe void InputStruct_HasNativeSize() => Assert.Equal(40, sizeof(Native.INPUT));

    [Fact]
    public void BuildCtrlV_IsCtrlDown_VDown_VUp_CtrlUp_WithMarker()
    {
        var inputs = KeyboardInjector.BuildCtrlV();
        Assert.Equal(4, inputs.Length);
        Assert.Equal(new ushort[] { 0x11, 0x56, 0x56, 0x11 }, inputs.Select(i => i.u.ki.wVk).ToArray());
        Assert.Equal(new[] { false, false, true, true }, inputs.Select(i => (i.u.ki.dwFlags & Native.KEYEVENTF_KEYUP) != 0).ToArray());
        Assert.All(inputs, i =>
        {
            Assert.Equal(Native.INPUT_KEYBOARD, i.type);
            Assert.Equal(KeyboardInjector.InjectionMarker, i.u.ki.dwExtraInfo);
        });
    }
}
