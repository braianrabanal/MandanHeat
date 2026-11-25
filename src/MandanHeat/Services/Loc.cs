using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace MandanHeat.Services;

/// <summary>
/// Idioma de la interfaz. Las claves son los textos en español; si el idioma es inglés se busca su traducción.
/// Cambiar el idioma actualiza al instante todo lo enlazado con {l:T '...'}.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static bool English { get; private set; }

    /// <summary>Se dispara tras cambiar el idioma (para refrescar textos construidos desde código).</summary>
    public static event Action? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Propiedad ficticia a la que se enlazan los textos para refrescarse al cambiar de idioma.</summary>
    public int Revision { get; private set; }

    /// <summary>"es", "en" o "" (automático: el idioma de Windows).</summary>
    public static void Apply(string? language)
    {
        bool en = language switch
        {
            "en" => true,
            "es" => false,
            _ => !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase),
        };
        if (en == English && Instance.Revision > 0) return;
        English = en;
        Instance.Revision++;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Revision)));
        LanguageChanged?.Invoke();
    }

    public static string T(string es) => English && Strings.En.TryGetValue(es, out var en) ? en : es;

    public static string F(string es, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(es), args);
}

/// <summary>{l:T 'Texto en español'} → texto traducido que se actualiza al cambiar de idioma.</summary>
public sealed class TExtension : MarkupExtension
{
    private readonly string _key;
    public TExtension(string key) => _key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Revision))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = TrConverter.Instance,
            ConverterParameter = _key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

internal sealed class TrConverter : IValueConverter
{
    public static TrConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T((string)parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
