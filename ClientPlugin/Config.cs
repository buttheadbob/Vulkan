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
    private float exposureScale = 0.12f;
    private float sunScale = 1f;
    private float ambientScale = 1f;
    private float specularScale = 0f;
    private float reflectionScale = 0f;
    private float metalnessScale = 1f;
    private float emissiveScale = 1f;
    private float screenScale = 4f;
    private float lightScale = 1f;
    private float bloomScale = 0.2f;

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

    [Separator("World lighting tuning")]

    [Slider(0.05f, 2f, 0.01f, label: "Exposure", description: "Auto-exposure multiplier. Raise if the world is too dark; lower if it is overexposed.")]
    public float ExposureScale
    {
        get => exposureScale;
        set => SetField(ref exposureScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Sun", description: "Direct sunlight intensity (diffuse + specular).")]
    public float SunScale
    {
        get => sunScale;
        set => SetField(ref sunScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Ambient", description: "Diffuse ambient/IBL irradiance on blocks.")]
    public float AmbientScale
    {
        get => ambientScale;
        set => SetField(ref ambientScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Specular", description: "Direct sun highlights on blocks.")]
    public float SpecularScale
    {
        get => specularScale;
        set => SetField(ref specularScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Reflections", description: "Image-based specular reflections from the skybox.")]
    public float ReflectionScale
    {
        get => reflectionScale;
        set => SetField(ref reflectionScale, value);
    }

    [Slider(0f, 2f, 0.01f, label: "Metalness", description: "Scales the ColorMetal alpha metalness on block materials.")]
    public float MetalnessScale
    {
        get => metalnessScale;
        set => SetField(ref metalnessScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Emissive", description: "Scales emissive maps (indicators, lights) after BloomEmissiveness.")]
    public float EmissiveScale
    {
        get => emissiveScale;
        set => SetField(ref emissiveScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Screen", description: "Additional emissive multiplier for generated LCD/cockpit screens only.")]
    public float ScreenScale
    {
        get => screenScale;
        set => SetField(ref screenScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Dynamic lights", description: "Point/spot light intensity on blocks.")]
    public float LightScale
    {
        get => lightScale;
        set => SetField(ref lightScale, value);
    }

    [Slider(0f, 4f, 0.05f, label: "Bloom", description: "HDR bloom strength for emissive indicators, screens and stars.")]
    public float BloomScale
    {
        get => bloomScale;
        set => SetField(ref bloomScale, value);
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
