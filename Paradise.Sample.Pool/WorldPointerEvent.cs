using System.Numerics;
using Paradise.Windowing;

namespace Paradise.Sample.Pool;

/// <summary>
/// A window input paired with the world ray its producer projected for it.
///
/// <see cref="WindowEvent"/> carries no ray, and deliberately: it is a WINDOW event, and a ray
/// needs a camera, which the window layer has no business knowing about. Only the host that owns
/// both — here the Godot bridge, which has the camera — can make the pairing, so the pairing lives
/// in the sample rather than in the engine's input contract.
///
/// <see cref="HasWorldRay"/> is separate from the vectors rather than implied by them: a producer
/// with no camera yet still has a real pointer event to deliver, and a zero ray is a legal ray.
/// </summary>
public readonly record struct WorldPointerEvent(
    WindowEvent Input,
    Vector3 WorldRayOrigin,
    Vector3 WorldRayDirection,
    bool HasWorldRay)
{
    /// <summary>Without a ray — a producer that has no camera, or an event that is not a pick.</summary>
    public WorldPointerEvent(WindowEvent input) : this(input, default, default, false) { }

    /// <summary>A pointer BUTTON going down, whichever device reported it. The old UiEvent had a
    /// PointerDown kind of its own; WindowEvent folds every transition into Button + Pressed and
    /// says which device in Source, so the question is asked here instead of matched on a kind.</summary>
    public bool IsPointerDown => Input.Kind == WindowEventKind.Button
        && Input.Source == EventSource.Mouse
        && Input.Pressed;
}
