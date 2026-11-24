using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace MandanHeat.Services;

/// <summary>Envoltorio sobre LibreHardwareMonitorLib con actualización selectiva.</summary>
public sealed class HardwareService : IDisposable
{
    private static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(10);

    private Computer _computer = Create();
    private readonly object _lock = new();
    private long _lastStorageUpdate;
    private bool _opened;

    private static Computer Create() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true, // AIO (NZXT Kraken, Corsair...), controladoras de ventiladores
        IsStorageEnabled = true,
        IsNetworkEnabled = false,
    };

    public void Open()
    {
        lock (_lock)
        {
            _computer.Open();
            _opened = true;
            UpdateAllLocked(forceStorage: true);
        }
    }

    /// <summary>Cierra y vuelve a abrir todos los sensores (por si otro programa tenía el bus ocupado al arrancar).</summary>
    public void Reopen()
    {
        lock (_lock)
        {
            try { _computer.Close(); } catch { }
            _computer = Create();
            _computer.Open();
            _opened = true;
            UpdateAllLocked(forceStorage: true);
        }
    }

    /// <summary>
    /// Actualiza el hardware. <paramref name="only"/> = null actualiza todo; si no, solo esos componentes.
    /// Los discos (SMART) se leen como mucho cada 10 s: su temperatura cambia despacio y leerlos cuesta.
    /// </summary>
    public void Update(IReadOnlySet<IHardware>? only = null)
    {
        lock (_lock)
        {
            if (!_opened) return;
            if (only == null) { UpdateAllLocked(forceStorage: false); return; }
            foreach (var hw in _computer.Hardware)
                if (only.Contains(hw)) UpdateRecursive(hw);
        }
    }

    private void UpdateAllLocked(bool forceStorage)
    {
        bool storageDue = forceStorage || Stopwatch.GetElapsedTime(_lastStorageUpdate) >= StorageInterval;
        if (storageDue) _lastStorageUpdate = Stopwatch.GetTimestamp();
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Storage && !storageDue) continue;
            UpdateRecursive(hw);
        }
    }

    private static void UpdateRecursive(IHardware hw)
    {
        try { hw.Update(); } catch { /* un dispositivo que falla no debe tumbar el resto */ }
        foreach (var sub in hw.SubHardware)
            UpdateRecursive(sub);
    }

    /// <summary>Todo el hardware aplanado (incluye sub-hardware como el chip Super I/O de la placa).</summary>
    public List<IHardware> AllHardware()
    {
        lock (_lock)
        {
            var list = new List<IHardware>();
            foreach (var hw in _computer.Hardware) Flatten(hw, list);
            return list;
        }
    }

    private static void Flatten(IHardware hw, List<IHardware> list)
    {
        list.Add(hw);
        foreach (var sub in hw.SubHardware) Flatten(sub, list);
    }

    public static IHardware TopLevel(IHardware hw)
    {
        while (hw.Parent != null) hw = hw.Parent;
        return hw;
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MandanHeat - volcado de sensores {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Administrador: {SystemInfo.IsAdmin}   PawnIO instalado: {SystemInfo.IsPawnIoInstalled()}");
        sb.AppendLine();
        foreach (var hw in AllHardware())
        {
            sb.AppendLine($"[{hw.HardwareType}] {hw.Name}   ({hw.Identifier})");
            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                var v = s.Value.HasValue ? s.Value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "null";
                sb.AppendLine($"    {s.SensorType,-12} {s.Name,-36} {v,12}   {s.Identifier}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("================ Informe de LibreHardwareMonitor ================");
        try
        {
            lock (_lock) sb.AppendLine(_computer.GetReport());
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(no disponible: {ex.Message})");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_opened) return;
            try { _computer.Close(); } catch { }
            _opened = false;
        }
    }
}
