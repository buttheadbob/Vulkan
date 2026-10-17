using ClientPlugin.Render;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;


namespace ClientPlugin;

public enum RendererKind
{
    Vulkan,
    DirectX11
}

public class Config : INotifyPropertyChanged
{
    #region Options

    private RendererKind renderer = RendererKind.Vulkan;
    private bool validation = false;

    #endregion

    #region User interface

    public readonly string Title = "Vulkan Renderer";

    [Separator("Status")]

    [Info("Active renderer", "The renderer running now. Changing the renderer below takes effect after a restart.")]
    public string ActiveRenderer => RendererStatus.ActiveRenderer;

    [Info("GPU")]
    public string Gpu => RendererStatus.Gpu;

    [Info("Features", "Hardware features available for the modern effects (ray traced lighting needs ray tracing support)")]
    public string Features => RendererStatus.Features;

    [Separator("Renderer (restart required)")]

    [Dropdown(description: "Renderer used on the next start. Falls back to DirectX 11 automatically if Vulkan cannot start.")]
    public RendererKind Renderer
    {
        get => renderer;
        set => SetField(ref renderer, value);
    }

    [Checkbox(description: "Enables the Vulkan validation layers if installed. Slow, for diagnosing renderer problems only.")]
    public bool Validation
    {
        get => validation;
        set => SetField(ref validation, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
