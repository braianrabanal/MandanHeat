using System;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace MandanHeat.Services;

/// <summary>
/// Nombres legibles (alineados con los que muestra HWiNFO) y filtro de lecturas imposibles.
/// Los nombres personalizados del usuario tienen prioridad.
/// </summary>
internal static partial class SensorNames
{
    public static string Display(ISensor s, AppSettings settings, bool localize = true)
    {
        if (settings.CustomNames.TryGetValue(s.Identifier.ToString(), out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        var alias = Alias(s);
        return alias == null ? s.Name : localize ? Loc.T(alias) : alias;
    }

    private static string? Alias(ISensor s)
    {
        var hw = s.Hardware.HardwareType;
        var n = s.Name;
        switch (hw)
        {
            case HardwareType.Cpu:
                if (s.SensorType == SensorType.Temperature && n == "Core (Tctl/Tdie)") return "CPU (Tctl/Tdie)";
                if (s.SensorType == SensorType.Power && n == "Package") return "CPU PPT (paquete)";
                if (s.SensorType == SensorType.Load)
                {
                    if (n == "CPU Total") return "Uso total";
                    if (n == "CPU Core Max") return "Hilo más cargado";
                    var m = CoreRegex().Match(n);
                    if (m.Success) return $"{Loc.T("Hilo")} #{m.Groups[1].Value}"; // LHM numera hilos lógicos como "Core"
                }
                break;

            case HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel:
                switch (s.SensorType, n)
                {
                    case (SensorType.Temperature, "GPU Core"): return "GPU (borde)";
                    case (SensorType.Temperature, "GPU Hot Spot"): return "GPU punto caliente";
                    case (SensorType.Temperature, "GPU Memory"): return "Memoria GPU (unión)";
                    case (SensorType.Load, "GPU Core"): return "Uso GPU";
                    case (SensorType.Load, "GPU Memory"): return "Controlador de memoria";
                    case (SensorType.Power, "GPU Package"): return "GPU (TGP)";
                    case (SensorType.Fan, "GPU Fan"): return "Ventilador GPU";
                    case (SensorType.Control, "GPU Fan"): return "Ventilador GPU";
                }
                break;

            case HardwareType.Cooler:
                if (n == "Liquid") return "Líquido";
                if (n == "Pump") return "Bomba";
                if (n == "Pump Control") return "Bomba";
                break;

            case HardwareType.SuperIO:
                // Mismos nombres que usan BIOS/HWiNFO en placas MSI con NCT6687D.
                switch (n)
                {
                    case "CPU": return "CPU (placa)";
                    case "VRM MOS": return "MOS";
                    case "PCH": return "Chipset";
                    case "PCIe x1": return "PCIE_1";
                    case "CPU Fan": return "CPU_FAN";
                    case "Pump Fan": return "PUMPFAN";
                }
                var sys = SystemFanRegex().Match(n);
                if (sys.Success) return $"System {sys.Groups[1].Value}";
                break;

            case HardwareType.Memory:
                if (n == "Memory" && s.SensorType == SensorType.Load) return "Uso";
                if (n == "Memory Used") return "En uso";
                if (n == "Memory Available") return "Disponible";
                break;
        }
        return null;
    }

    public static string HardwareName(IHardware hw) => hw.Name switch
    {
        "Total Memory" => Loc.T("Memoria RAM"),
        "Virtual Memory" => Loc.T("Memoria virtual (RAM + archivo de paginación)"),
        _ => hw.Parent != null ? $"{hw.Parent.Name} · {hw.Name}" : hw.Name,
    };

    /// <summary>
    /// Descarta canales sin conectar o lecturas imposibles: sin driver algunas temperaturas/potencias quedan en 0,
    /// y los canales libres del Super I/O suelen dar -40, 127 o 255 °C.
    /// </summary>
    public static bool IsPlausible(ISensor s)
    {
        if (s.Value is not { } v || float.IsNaN(v)) return false;
        return s.SensorType switch
        {
            SensorType.Temperature => v > 0 && v < 125,
            SensorType.Power => !(v <= 0 && (s.Max ?? 0) <= 0),
            SensorType.Fan => v >= 0 && v < 20000,
            _ => true,
        };
    }

    [GeneratedRegex(@"^CPU Core #(\d+)$")]
    private static partial Regex CoreRegex();

    [GeneratedRegex(@"^System Fan #(\d+)$")]
    private static partial Regex SystemFanRegex();
}
