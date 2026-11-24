using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace MandanHeat.Services;

/// <summary>
/// "Iniciar con Windows" mediante el Programador de tareas con privilegios elevados,
/// así la app arranca como administrador sin mostrar el aviso de UAC en cada inicio de sesión.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "MandanHeat";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        var exe = Environment.ProcessPath!;
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        // Sin límite de tiempo de ejecución (por defecto Windows mata las tareas a las 72 h) y también con batería.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Inicia MandanHeat al iniciar sesión</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT15S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--minimized</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var path = Path.Combine(Path.GetTempPath(), "MandanHeat-task.xml");
        File.WriteAllText(path, xml, Encoding.Unicode);
        try { return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{path}\" /F") == 0; }
        finally { try { File.Delete(path); } catch { } }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
