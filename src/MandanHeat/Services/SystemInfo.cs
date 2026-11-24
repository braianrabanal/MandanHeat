using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace MandanHeat.Services;

internal static class SystemInfo
{
    public static bool IsAdmin
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>PawnIO es el driver que usa LibreHardwareMonitor 0.9.5+ para leer CPU, placa base y SMBus.</summary>
    public static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key != null;
    }

    /// <summary>Relanza la app con UAC. Devuelve false si el usuario cancela.</summary>
    public static bool RelaunchElevated(string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(System.Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
