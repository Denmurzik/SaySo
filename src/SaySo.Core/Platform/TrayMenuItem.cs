namespace SaySo.Core.Platform;

public sealed record TrayMenuItem(uint Id, string Text, bool Enabled = true, bool Checked = false, bool IsSeparator = false)
{
    public static TrayMenuItem Separator { get; } = new(0, "", IsSeparator: true);
}
