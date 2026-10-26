using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
namespace Respiro.App;

public enum AppTheme { System, Light, Dark }

/// <summary>Troca o dicionário de cores em tempo real e mantém a barra de título coerente.</summary>
public static class ThemeManager
{
    public static AppTheme Mode { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;
    static bool listening;

    public static void Apply(AppTheme mode)
    {
        Mode = mode;
        IsDark = mode == AppTheme.Dark || (mode == AppTheme.System && SystemPrefersDark());
        var uri = new Uri($"pack://application:,,,/Respiro.App;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = new ResourceDictionary { Source = uri };
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        if (!listening) { listening = true; SystemEvents.UserPreferenceChanged += OnSystemPreference; }
        Changed?.Invoke();
    }

    static void OnSystemPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() => { if (SystemPrefersDark() != IsDark) Apply(AppTheme.System); });
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Barra de título na cor do tema (Windows 11 22H2+: cor, texto e borda; Windows 10 20H1+: ao menos o modo escuro).
    /// Em versões sem suporte as chamadas falham em silêncio e a barra do sistema é mantida.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { }
        try
        {
            int caption = ColorRef("Brush.Bg"), text = ColorRef("Brush.Text"), border = ColorRef("Brush.Border");
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch { }
    }

    static int ColorRef(string key)
    {
        var c = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources[key]).Color;
        return c.R | (c.G << 8) | (c.B << 16);
    }
}

/// <summary>Preferências locais da interface. Somente aparência; nada aqui altera regras de segurança.</summary>
public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    static string FilePath => Path.Combine(Respiro.Core.RespiroDataPaths.Root, "settings.json");

    public static UiSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* Preferência ilegível: usa o padrão. */ }
        return new();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* Falha ao salvar preferência nunca afeta a análise. */ }
    }
}
