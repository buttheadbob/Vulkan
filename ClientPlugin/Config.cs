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

public enum FrameLimitKind
{
    Unlimited,
    Fps30,
    Fps60,
    Fps120,
    Fps240
}

public class Config : INotifyPropertyChanged
{
    #region Options

    private RendererKind renderer = RendererKind.Vulkan;
    private bool validation = false;
    private bool diagnostics = true;
    private FrameLimitKind frameLimit = FrameLimitKind.Unlimited;
    private bool vsync = true;
    private float exposureScale = 0.5f;
    private float sunScale = 1f;
    private float ambientScale = 1f;
    private float specularScale = 1f;
    private float reflectionScale = 1f;
    private float metalnessScale = 1f;
    private float emissiveScale = 1f;
    private float screenScale = 4f;
    private float lightScale = 1f;
    private float bloomScale = 0.6f;
    private float effectsDistance = 150f;
    private float shadowDistance = 250f;
    private float renderScale = 1f;
    private bool fxaa = true;
    private int fxaaQuality = 2;
    private float modelDetail = 1f;
    private int reflectionQuality = 2;
    private int reflectionProbeQuality = 1;
    private bool screenSpaceReflections = false;
    private float reflectionBlendStart = 40f;
    private float reflectionBlendEnd = 250f;
    private float reflectionFarMipBias = 2f;
    private float reflectionMinMip = 2f;
    private float reflectionProbeBlendSeconds = 0.25f;
    private bool shadows = false;
    private int msaaLevel = 2;

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

    [Dropdown(description: "Caps the frame rate. Unlimited does not add a cap; the plugin's own VSync option can still limit it to the monitor refresh rate.")]
    public FrameLimitKind FrameLimit
    {
        get => frameLimit;
        set => SetField(ref frameLimit, value);
    }

    [Checkbox(label: "VSync (restart required)", description: "Plugin-owned vertical sync, independent of the game's video settings. Restart required.")]
    public bool VSync
    {
        get => vsync;
        set => SetField(ref vsync, value);
    }

    [Checkbox(description: "Enables the Vulkan validation layers if installed. Slow, for diagnosing renderer problems only.")]
    public bool Validation
    {
        get => validation;
        set => SetField(ref validation, value);
    }

    [Separator("Diagnostics")]

    [Checkbox(label: "Diagnostics overlay", description: "Shows the Vulkan renderer's own frame statistics: GPU triangle/object/cull counts, frame time, FPS and graphs over time.")]
    public bool Diagnostics
    {
        get => diagnostics;
        set => SetField(ref diagnostics, value);
    }

    [Separator("Shadows")]

    [Checkbox(label: "Sun shadows (restart required)", description: "Experimental cascaded sun shadows with soft PCF and alpha-tested casters. Restart required. Contact shadows, LOD1+ verification and quality options are planned.")]
    public bool Shadows
    {
        get => shadows;
        set => SetField(ref shadows, value);
    }

    [Slider(50f, 1000f, 10f, label: "Shadow distance", description: "Sun shadows keep their full cascade quality out to this distance and stop beyond it. Higher values cover more of the scene at a higher shadow-map cost. 250 m is the default.")]
    public float ShadowDistance
    {
        get => shadowDistance;
        set => SetField(ref shadowDistance, value);
    }

    [Separator("Rendering quality")]

    [Slider(150f, 1000f, 10f, label: "Light distance", description: "Maximum distance for dynamic point and spot lights. Sun shadow distance is set separately above.")]
    public float EffectsDistance
    {
        get => effectsDistance;
        set => SetField(ref effectsDistance, value);
    }

    [Checkbox(label: "FXAA", description: "Plugin-owned anti-aliasing. Independent of the game's Graphics > Anti-aliasing setting. FXAA runs after tonemapping, like the stock renderer.")]
    public bool Fxaa
    {
        get => fxaa;
        set => SetField(ref fxaa, value);
    }

    [Slider(1f, 4f, 1f, SliderAttribute.SliderType.Integer, label: "FXAA quality",
        description: "FXAA edge-search quality, Low for weak GPUs, Medium for stock quality, High and Very high for stronger GPUs. Very high exceeds stock. Used when FXAA is enabled.",
        valueLabels: ["Low", "Medium", "High", "Very high"])]
    public int FxaaQuality
    {
        get => fxaaQuality;
        set => SetField(ref fxaaQuality, value < 1 ? 1 : value > 4 ? 4 : value);
    }

    [Slider(0.5f, 4f, 0.05f, label: "Model detail distance", description: "Scales the distance at which block models switch to lower LODs. Above 1.0 keeps full-detail geometry farther away; 1.0 matches stock.")]
    public float ModelDetail
    {
        get => modelDetail;
        set => SetField(ref modelDetail, value);
    }

    [Checkbox(label: "Screen-space reflections",
        description: "Optional sharp screen-space ship reflections on metal and high-gloss surfaces. Off by default: stock has no SSR and the close/far environment probe handles most reflective surfaces at a much lower cost. Enabling it can add detail but costs GPU time.")]
    public bool ScreenSpaceReflections
    {
        get => screenSpaceReflections;
        set => SetField(ref screenSpaceReflections, value);
    }

    [Slider(0f, 2f, 1f, SliderAttribute.SliderType.Integer, label: "SSR quality",
        description: "Screen-space reflection trace quality, used only when Screen-space reflections is enabled. Low is cheapest, High traces farther.",
        valueLabels: ["Low", "Medium", "High"])]
    public int ReflectionQuality
    {
        get => reflectionQuality;
        set => SetField(ref reflectionQuality, value < 0 ? 0 : value > 2 ? 2 : value);
    }

    [Slider(1f, 300f, 1f, label: "Reflection close distance",
        description: "Surfaces closer than this use the sharpest reflection mip. Increase for crisper reflections on nearby blocks; the live probe quality blend is evaluated every frame without a restart.")]
    public float ReflectionBlendStart
    {
        get => reflectionBlendStart;
        set => SetField(ref reflectionBlendStart, value < 1f ? 1f : value);
    }

    [Slider(2f, 2000f, 1f, label: "Reflection far distance",
        description: "At this distance reflections use the coarsest (far) quality mip. The interval between the close and far distance is blended smoothly, so distant objects contribute only colour/blobs without sharp detail. Evaluated live.")]
    public float ReflectionBlendEnd
    {
        get => reflectionBlendEnd;
        set => SetField(ref reflectionBlendEnd, value < ReflectionBlendStart ? ReflectionBlendStart : value);
    }

    [Slider(0f, 4f, 0.1f, label: "Reflection far blur",
        description: "Extra mip levels added to distant reflections. Higher values make far reflections softer and cheaper-looking. Evaluated live.")]
    public float ReflectionFarMipBias
    {
        get => reflectionFarMipBias;
        set => SetField(ref reflectionFarMipBias, value < 0f ? 0f : value > 4f ? 4f : value);
    }

    [Slider(0f, 4f, 1f, label: "Reflection minimum blur",
        description: "Minimum probe mip used even on close surfaces. 0 is sharpest, 2 removes star/edge glitter, 3 or 4 is intentionally soft. Evaluated live.")]
    public float ReflectionMinMip
    {
        get => reflectionMinMip;
        set => SetField(ref reflectionMinMip, value < 0f ? 0f : value > 4f ? 4f : value);
    }

    [Slider(0.05f, 1f, 0.05f, label: "Reflection update blend",
        description: "Time over which one completed probe face cycle fades in. 0.25 s is responsive; larger values are smoother but lag more. The probe still captures one face per frame; this only controls the live blend. Evaluated live.")]
    public float ReflectionProbeBlendSeconds
    {
        get => reflectionProbeBlendSeconds;
        set => SetField(ref reflectionProbeBlendSeconds, value < 0.05f ? 0.05f : value > 1f ? 1f : value);
    }

    [Slider(0f, 2f, 1f, SliderAttribute.SliderType.Integer, label: "Reflection probe (restart required)",
        description: "Dynamic reflection cubemap resolution. Capture is one face per frame. 256 is the default; 512 is sharper but much more expensive, 128 is the cheapest. Applies after a restart.",
        valueLabels: ["Low (128)", "Medium (256)", "High (512)"])]
    public int ReflectionProbeQuality
    {
        get => reflectionProbeQuality;
        set => SetField(ref reflectionProbeQuality, value < 0 ? 0 : value > 2 ? 2 : value);
    }

    [Separator("Advanced")]

    [Slider(0f, 3f, 1f, SliderAttribute.SliderType.Integer, label: "MSAA (restart required)",
        description: "Multi-sample anti-aliasing for the 3D scene, which smooths block silhouettes and round objects. 4x is a good quality/cost balance. Applies after a restart.",
        valueLabels: ["Off", "2x", "4x", "8x"])]
    public int MsaaLevel
    {
        get => msaaLevel;
        set => SetField(ref msaaLevel, value < 0 ? 0 : value > 3 ? 3 : value);
    }

    [Slider(1f, 2f, 0.05f, label: "Supersampling", description: "Optional SSAA factor for the 3D scene. Values above 1.0 render at a higher internal resolution and downsample, at a large GPU cost. This is not a substitute for native resolution or FXAA.")]
    public float RenderScale
    {
        get => renderScale;
        set => SetField(ref renderScale, value);
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

    [Slider(0f, 4f, 0.05f, label: "Reflections", description: "Specular reflection strength for the skybox IBL and screen-space ship reflections.")]
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

    [Slider(0f, 8f, 0.05f, label: "Emissive strength", description: "Scales emissive maps (indicators, light fixtures, screens) after BloomEmissiveness.")]
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
