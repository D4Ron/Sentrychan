using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sentrychan.Core.Services;

public static class TailscaleDetector
{
    // Tailscale assigns IPs in the 100.x.x.x range (CGNAT space)
    public static string? GetTailscaleIp()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                // Tailscale interface is named "Tailscale" on Windows
                if (!iface.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    // All Tailscale IPs start with 100.
                    if (ip.StartsWith("100."))
                        return ip;
                }
            }
        }
        catch (Exception ex)
        {
            // Not fatal -- Tailscale just not installed
            Console.WriteLine($"Tailscale detection failed: {ex.Message}");
        }
        return null;
    }

    public static bool IsTailscaleInstalled() => GetTailscaleIp() != null;
}
