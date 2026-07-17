using Paradise.Rendering.Pbr;

namespace ParadiseRuntime.Tests;

/// <summary>The PARADISE_BLOOM dev-override parser. Regression: the documented format is
/// "threshold,knee,intensity" (tokens 0,1,2) — an earlier off-by-one read tokens 1,2,3, which
/// silently dropped the threshold and always defaulted the intensity.</summary>
public class BloomOverrideTests
{
    private static readonly PbrBloom Fallback = new() { Threshold = 9f, Knee = 9f, Intensity = 9f };

    [Test]
    public async Task tokens_map_to_threshold_knee_intensity_in_order()
    {
        var bloom = RuntimeLoop.ParseBloomOverride("1.0,0.5,0.8", Fallback);
        await Assert.That(bloom).IsNotNull();
        await Assert.That(bloom!.Enabled).IsTrue();
        await Assert.That(bloom.Threshold).IsEqualTo(1.0f);
        await Assert.That(bloom.Knee).IsEqualTo(0.5f);
        await Assert.That(bloom.Intensity).IsEqualTo(0.8f);
    }

    [Test]
    public async Task disabled_or_empty_yields_no_override()
    {
        await Assert.That(RuntimeLoop.ParseBloomOverride("0", Fallback)).IsNull();
        await Assert.That(RuntimeLoop.ParseBloomOverride("", Fallback)).IsNull();
        await Assert.That(RuntimeLoop.ParseBloomOverride(null, Fallback)).IsNull();
    }

    [Test]
    public async Task missing_tokens_fall_back_per_field()
    {
        // Only threshold supplied → knee + intensity keep the fallback.
        var bloom = RuntimeLoop.ParseBloomOverride("2.0", Fallback);
        await Assert.That(bloom!.Threshold).IsEqualTo(2.0f);
        await Assert.That(bloom.Knee).IsEqualTo(9f);
        await Assert.That(bloom.Intensity).IsEqualTo(9f);
    }
}
