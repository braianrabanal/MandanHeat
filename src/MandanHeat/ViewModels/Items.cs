using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using MandanHeat.Services;

namespace MandanHeat.ViewModels;

public sealed class SensorVm : ObservableObject
{
    private string _name;
    private string _value = "—", _min = "—", _max = "—";
    private Brush _valueBrush = Palette.Text;
    private double _percent;
    private bool _isPinned;
    private bool _isFavorite;

    public SensorVm(ISensor sensor, string name, bool pinned, Action<SensorVm> rename, Action<SensorVm> hide,
                    bool favorite = false, Action<SensorVm>? toggleFavorite = null)
    {
        Sensor = sensor;
        Id = sensor.Identifier.ToString();
        _name = name;
        Type = sensor.SensorType;
        IsLiquid = IsLiquidSensor(sensor);
        _isPinned = pinned;
        _isFavorite = favorite;
        Badge = SensorFormat.Badge(sensor.Hardware.HardwareType);
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite?.Invoke(this));
        RenameCommand = new RelayCommand(() => rename(this));
        HideCommand = new RelayCommand(() => hide(this));
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        Update();
    }

    public static bool IsLiquidSensor(ISensor s) =>
        s.SensorType == SensorType.Temperature &&
        (s.Hardware.HardwareType == HardwareType.Cooler ||
         s.Name.Contains("Liquid", StringComparison.OrdinalIgnoreCase) ||
         s.Name.Contains("Coolant", StringComparison.OrdinalIgnoreCase) ||
         s.Name.Contains("Water", StringComparison.OrdinalIgnoreCase));

    public ISensor Sensor { get; }
    public string Id { get; }
    public SensorType Type { get; }
    public bool IsLiquid { get; }
    public bool ShowBar => Type is SensorType.Load or SensorType.Control or SensorType.Level;
    public ICommand RenameCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public string Badge { get; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Min { get => _min; private set => Set(ref _min, value); }
    public string Max { get => _max; private set => Set(ref _max, value); }
    public Brush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }
    /// <summary>Aparece en la vista simple.</summary>
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

    public void Update()
    {
        var v = Sensor.Value;
        Value = SensorFormat.Format(Type, v);
        Min = SensorFormat.Format(Type, Sensor.Min);
        Max = SensorFormat.Format(Type, Sensor.Max);
        ValueBrush = Type == SensorType.Temperature && v.HasValue
            ? Palette.ForTemperature(v.Value, IsLiquid)
            : Palette.Text;
        if (ShowBar) Percent = Math.Clamp(v ?? 0, 0, 100);
    }
}

/// <summary>Ventilador seleccionable como "ventilador del radiador" en Ajustes.</summary>
public sealed class FanChoiceVm(string id, string name, string reading, bool selected, Action changed) : ObservableObject
{
    private bool _selected = selected;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Reading { get; } = reading;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) changed(); } }
}

public sealed class SensorGroupVm(string title, List<SensorVm> sensors)
{
    public string Title { get; } = title;
    public List<SensorVm> Sensors { get; } = sensors;
}

public sealed class HardwareVm(string name, string badge, List<SensorGroupVm> groups)
{
    public string Name { get; } = name;
    public string Badge { get; } = badge;
    public List<SensorGroupVm> Groups { get; } = groups;
}

public sealed class SummaryCard(string title) : ObservableObject
{
    private string _value = "—", _unit = "", _detail = "";
    private Brush _brush = Palette.Text;

    public string Title => Loc.T(title);
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}

public sealed class OverlaySegment(string text, Brush brush) : ObservableObject
{
    private string _text = text;
    private Brush _brush = brush;
    public string Text { get => _text; set => Set(ref _text, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}

public sealed class OverlayLine(string key, string label, Brush labelBrush, IEnumerable<OverlaySegment> segments)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public Brush LabelBrush { get; } = labelBrush;
    public ObservableCollection<OverlaySegment> Segments { get; } = new(segments);
}

public sealed class BannerVm(string key, string text, string? actionText, ICommand? action)
{
    public string Key { get; } = key;
    public string Text { get; } = text;
    public string? ActionText { get; } = actionText;
    public ICommand? Action { get; } = action;
    public bool HasAction => ActionText != null;
}
