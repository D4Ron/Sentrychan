namespace Sentrychan.Core.Models;

public class FrpServerPreset
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public static class FrpServerPresets
{
    public static readonly List<FrpServerPreset> All = new()
    {
        new FrpServerPreset { Name = "Custom...", Address = "" },
        new FrpServerPreset { Name = "sakura FRP (CN)", Address = "connect.sakurafrp.com:7000", Note = "Best for Asian users. Free with account." },
        new FrpServerPreset { Name = "OpenFRP (CN)", Address = "frp.openfrp.top:7000", Note = "Requires free account registration." },
        new FrpServerPreset { Name = "Mefrp (CN)", Address = "frp.mefrp.com:7000", Note = "Community server." },
        new FrpServerPreset { Name = "localhost (LAN only)", Address = "127.0.0.1:7000", Note = "Only works if you run frps locally." }
    };
}
