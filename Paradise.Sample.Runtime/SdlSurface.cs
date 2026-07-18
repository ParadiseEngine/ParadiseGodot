using Paradise.Rendering;
using static SDL.SDL3;
using SDL;

namespace Paradise.Sample.Runtime;

/// <summary>SDL window → WebGPU surface descriptor mapping (the engine sample's snippet,
/// copied by design — 40 lines is cheaper than a shared windowing package).</summary>
internal static class SdlSurface
{
    public static unsafe SurfaceDescriptor BuildDescriptor(SDL_Window* window, out IntPtr metalView)
    {
        metalView = IntPtr.Zero;
        var props = SDL_GetWindowProperties(window);

        int w = 0, h = 0;
        SDL_GetWindowSizeInPixels(window, &w, &h);
        var width = (uint)Math.Max(1, w);
        var height = (uint)Math.Max(1, h);

        if (OperatingSystem.IsWindows())
        {
            var hwnd = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
            return new SurfaceDescriptor(SurfacePlatform.Win32, IntPtr.Zero, hwnd, width, height);
        }

        if (OperatingSystem.IsMacOS())
        {
            metalView = SDL_Metal_CreateView(window);
            if (metalView == IntPtr.Zero)
                throw new InvalidOperationException($"SDL_Metal_CreateView failed: {SDL_GetError()}");
            var layer = SDL_Metal_GetLayer(metalView);
            if (layer == IntPtr.Zero)
                throw new InvalidOperationException("SDL_Metal_GetLayer returned null.");
            return new SurfaceDescriptor(SurfacePlatform.Cocoa, IntPtr.Zero, layer, width, height);
        }

        if (OperatingSystem.IsLinux())
        {
            var wlDisplay = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, IntPtr.Zero);
            if (wlDisplay != IntPtr.Zero)
            {
                var wlSurface = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, IntPtr.Zero);
                return new SurfaceDescriptor(SurfacePlatform.Wayland, wlDisplay, wlSurface, width, height);
            }

            var x11Display = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, IntPtr.Zero);
            var x11Window = SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
            return new SurfaceDescriptor(SurfacePlatform.Xlib, x11Display, (IntPtr)x11Window, width, height);
        }

        throw new PlatformNotSupportedException("No surface mapping for this OS; use --headless.");
    }
}
