using System.Collections.Generic;
using Paradise.Sample.Pool.Physics;

namespace Paradise.Sample.Pool.Tests;

/// <summary>The shared static-surface bounce reduction both hosts feed (SceneAssembler from the
/// contract, EcsSceneBridge from live nodes). Regression: the Obstacle filter must be BITWISE so a
/// body authored on Obstacle plus another layer still counts.</summary>
public class StaticSurfacesTests
{
    private static StaticSurfaces.Surface S(float restitution, uint layerMask) => new(restitution, layerMask);

    [Test]
    public async Task takes_max_restitution_among_obstacle_surfaces()
    {
        var surfaces = new List<StaticSurfaces.Surface>
        {
            S(0.5f, PhysicsLayers.Obstacle),
            S(0.75f, PhysicsLayers.Obstacle),
            S(0.9f, PhysicsLayers.Floor), // not an obstacle surface → ignored
        };
        await Assert.That(StaticSurfaces.BounceRestitution(surfaces, 0.4f)).IsEqualTo(0.75f);
    }

    [Test]
    public async Task multi_layer_obstacle_surface_still_counts()
    {
        var surfaces = new List<StaticSurfaces.Surface>
        {
            S(0.75f, PhysicsLayers.Obstacle | PhysicsLayers.Floor),
        };
        await Assert.That(StaticSurfaces.BounceRestitution(surfaces, 0.4f)).IsEqualTo(0.75f);
    }

    [Test]
    public async Task falls_back_when_no_obstacle_surface()
    {
        var floorOnly = new List<StaticSurfaces.Surface> { S(0.9f, PhysicsLayers.Floor) };
        await Assert.That(StaticSurfaces.BounceRestitution(floorOnly, 0.4f)).IsEqualTo(0.4f);
        await Assert.That(StaticSurfaces.BounceRestitution(new List<StaticSurfaces.Surface>(), 0.4f)).IsEqualTo(0.4f);
    }
}
