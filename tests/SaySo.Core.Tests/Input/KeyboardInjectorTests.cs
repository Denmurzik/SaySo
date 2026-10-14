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

    [Theory]
    [InlineData(0, new ushort[0])]                 // система ничего не приняла — отпускать нечего
    [InlineData(1, new ushort[] { 0x11 })]         // принят только Ctrl↓
    [InlineData(2, new ushort[] { 0x56, 0x11 })]   // Ctrl↓ V↓ — отпустить V, затем Ctrl
    [InlineData(3, new ushort[] { 0x11 })]         // Ctrl↓ V↓ V↑ — остался Ctrl
    [InlineData(4, new ushort[0])]                 // принято всё
    public void BuildReleasesAfterPartial_ReleasesOnlyKeysLeftDown(int accepted, ushort[] expectedKeys)
    {
        var releases = KeyboardInjector.BuildReleasesAfterPartial(accepted);

        Assert.Equal(expectedKeys, releases.Select(i => i.u.ki.wVk).ToArray());
        Assert.All(releases, i =>
        {
            Assert.NotEqual(0u, i.u.ki.dwFlags & Native.KEYEVENTF_KEYUP);
            Assert.Equal(KeyboardInjector.InjectionMarker, i.u.ki.dwExtraInfo); // собственный хук SaySo их игнорирует
        });
    }
}
