using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MandanHeat.Services;

/// <summary>Reduce el impacto de la app mientras no se está mirando la ventana.</summary>
internal static class PowerSaving
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    /// <summary>"Modo eficiencia" de Windows 11 (EcoQoS): el planificador da a la app núcleos y frecuencias bajos.</summary>
    public static void SetEfficiencyMode(bool enabled)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            using var me = Process.GetCurrentProcess();
            SetProcessInformation(me.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }

    /// <summary>Devuelve al sistema la memoria que la interfaz ya no usa (al minimizar a la bandeja).</summary>
    public static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            using var me = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(me.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
}
