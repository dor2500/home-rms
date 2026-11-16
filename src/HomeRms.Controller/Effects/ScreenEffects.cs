using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HomeRms.Controller.Effects;

/// <summary>GPU Catmull-Rom upscaler (Shaders/Upscale.fx). Needs the Image drawn with nearest-neighbour scaling.</summary>
public sealed class UpscaleEffect : ShaderEffect
{
    private static readonly PixelShader Shader = Load("Upscale.ps");

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(UpscaleEffect), 0, SamplingMode.NearestNeighbor);

    /// <summary>Remote frame size in pixels.</summary>
    public static readonly DependencyProperty SourceSizeProperty =
        DependencyProperty.Register(nameof(SourceSize), typeof(Point), typeof(UpscaleEffect),
            new UIPropertyMetadata(new Point(1920, 1080), PixelShaderConstantCallback(0)));

    public UpscaleEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SourceSizeProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point SourceSize
    {
        get => (Point)GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    internal static PixelShader Load(string file)
    {
        var shader = new PixelShader
        {
            UriSource = new Uri($"pack://application:,,,/HomeRmsController;component/Shaders/{file}", UriKind.Absolute)
        };
        shader.Freeze();
        return shader;
    }
}

/// <summary>Contrast-adaptive sharpening of the on-screen picture (Shaders/Sharpen.fx).</summary>
public sealed class SharpenEffect : ShaderEffect
{
    private static readonly PixelShader Shader = UpscaleEffect.Load("Sharpen.ps");

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(SharpenEffect), 0, SamplingMode.NearestNeighbor);

    /// <summary>0 = gentle, 1 = strong.</summary>
    public static readonly DependencyProperty SharpnessProperty =
        DependencyProperty.Register(nameof(Sharpness), typeof(double), typeof(SharpenEffect),
            new UIPropertyMetadata(0.6, PixelShaderConstantCallback(0)));

    public SharpenEffect()
    {
        PixelShader = Shader;
        DdxUvDdyUvRegisterIndex = 1; // shader reads the size of one screen pixel from c1
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SharpnessProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double Sharpness
    {
        get => (double)GetValue(SharpnessProperty);
        set => SetValue(SharpnessProperty, value);
    }
}
