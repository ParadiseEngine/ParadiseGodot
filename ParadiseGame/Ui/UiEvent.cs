using System.Numerics;

namespace ParadiseGame.Ui;

public enum UiEventKind
{
    PointerMove,
    PointerDown,
    PointerUp,
    Resize,
}

public enum UiPointerButton
{
    Left,
    Right,
    Middle,
}

/// <summary>One UI input event, produced on the platform/render thread (SDL, Godot, …) and
/// consumed on the SIMULATION thread by <see cref="IUiInput"/>. Pointer coordinates are in
/// UI pixels (already DPI-scaled by the producer). Pointer-down events may carry a world-space
/// pick ray so game logic can act on clicks the UI did not consume without needing camera
/// state on the sim thread.</summary>
public readonly record struct UiEvent(
    UiEventKind Kind,
    float X,
    float Y,
    UiPointerButton Button,
    Vector3 WorldRayOrigin,
    Vector3 WorldRayDirection,
    bool HasWorldRay)
{
    public static UiEvent PointerMove(float x, float y) =>
        new(UiEventKind.PointerMove, x, y, UiPointerButton.Left, default, default, false);

    public static UiEvent PointerDown(float x, float y, UiPointerButton button, Vector3 rayOrigin, Vector3 rayDirection) =>
        new(UiEventKind.PointerDown, x, y, button, rayOrigin, rayDirection, true);

    public static UiEvent PointerUp(float x, float y, UiPointerButton button) =>
        new(UiEventKind.PointerUp, x, y, button, default, default, false);

    public static UiEvent Resize(float width, float height) =>
        new(UiEventKind.Resize, width, height, UiPointerButton.Left, default, default, false);
}
