using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace MandanHeat.ViewModels;

internal static class SensorFormat
{
    /// <summary>Tipos que se muestran por defecto: temperaturas, uso, ventiladores (RPM y %) y potencia.</summary>
    public static readonly SensorType[] DefaultTypes =
        { SensorType.Temperature, SensorType.Load, SensorType.Fan, SensorType.Control, SensorType.Power };

    public static bool IsDefault(SensorType t) => DefaultTypes.Contains(t);

    public static int Order(SensorType t) => t switch
    {
        SensorType.Temperature => 0,
        SensorType.Load => 1,
        SensorType.Fan => 2,
        SensorType.Control => 3,
        SensorType.Power => 4,
        SensorType.Clock => 5,
        SensorType.Voltage => 6,
        _ => 10 + (int)t,
    };

    public static string GroupTitle(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperaturas",
        SensorType.Load => "Uso",
        SensorType.Fan => "Ventiladores",
        SensorType.Control => "Control de ventiladores",
        SensorType.Power => "Potencia",
        SensorType.Clock => "Frecuencias",
        SensorType.Voltage => "Voltajes",
        SensorType.Current => "Corriente",
        SensorType.Data or SensorType.SmallData => "Datos",
        SensorType.Throughput => "Transferencia",
        SensorType.Level => "Nivel",
        SensorType.Flow => "Caudal",
        SensorType.Factor => "Factores",
        SensorType.Frequency => "Frecuencia",
        SensorType.Energy => "Energía",
        SensorType.Noise => "Ruido",
        SensorType.TimeSpan => "Tiempo",
        SensorType.Humidity => "Humedad",
        _ => t.ToString(),
    };

    public static string Format(SensorType t, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return "—";
        double x = v;
        return t switch
        {
            SensorType.Temperature => $"{x:0.0} °C",
            SensorType.Load => $"{x:0.0} %",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Control => $"{x:0} %",
            SensorType.Power => $"{x:0.0} W",
            SensorType.Clock => $"{x:0} MHz",
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Current => $"{x:0.00} A",
            SensorType.Data => $"{x:0.0} GB",
            SensorType.SmallData => $"{x:0} MB",
            SensorType.Throughput => Throughput(x),
            SensorType.Level => $"{x:0.0} %",
            SensorType.Flow => $"{x:0} L/h",
            SensorType.Frequency => $"{x:0} Hz",
            SensorType.Energy => $"{x:0} mWh",
            SensorType.Noise => $"{x:0} dBA",
            SensorType.Humidity => $"{x:0} %",
            SensorType.Factor => $"{x:0.000}",
            SensorType.TimeSpan => TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss"),
            _ => $"{x:0.##}",
        };
    }

    /// <summary>Formato compacto para el overlay.</summary>
    public static string Short(SensorType t, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return "—";
        double x = v;
        return t switch
        {
            SensorType.Temperature => $"{x:0}°C",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{x:0}%",
            SensorType.Fan => $"{x:0} rpm",
            SensorType.Power => $"{x:0} W",
            SensorType.Clock => $"{x:0} MHz",
            _ => Format(t, value),
        };
    }

    private static string Throughput(double bytesPerSecond)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0;
        while (bytesPerSecond >= 1024 && i < units.Length - 1) { bytesPerSecond /= 1024; i++; }
        return $"{bytesPerSecond:0.0} {units[i]}";
    }

    public static string Badge(HardwareType t) => t switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "RAM",
        HardwareType.Motherboard => "PLACA",
        HardwareType.SuperIO => "PLACA",
        HardwareType.Storage => "DISCO",
        HardwareType.Cooler => "REFRIG.",
        HardwareType.EmbeddedController => "EC",
        HardwareType.Network => "RED",
        HardwareType.Psu => "FUENTE",
        _ => t.ToString().ToUpperInvariant(),
    };

    public static int HardwareOrder(HardwareType t) => t switch
    {
        HardwareType.Cpu => 0,
        HardwareType.GpuAmd or HardwareType.GpuNvidia => 1,
        HardwareType.GpuIntel => 2,
        HardwareType.Cooler => 3,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => 4,
        HardwareType.Memory => 5,
        HardwareType.Storage => 6,
        _ => 10,
    };
}
