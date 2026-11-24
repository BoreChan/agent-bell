using System.Runtime.InteropServices;

namespace AgentBell;

internal static unsafe class Direct2D
{
    internal const int RecreateTarget = unchecked((int)0x8899000c);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vector2(float x, float y)
    {
        internal float X = x;
        internal float Y = y;
    }

    internal readonly record struct Transform(float ScaleX, float ScaleY, float X, float Y)
    {
        internal static Transform Identity => Scale(1, 1);
        internal static Transform Scale(float x, float y) => new(x, y, 0, 0);
        internal static Transform Translate(float x, float y) => new(1, 1, x, y);

        public static Transform operator *(Transform first, Transform second) =>
            new(first.ScaleX * second.ScaleX, first.ScaleY * second.ScaleY,
                first.X * second.ScaleX + second.X, first.Y * second.ScaleY + second.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Matrix(float M11, float M12, float M21, float M22, float Dx, float Dy);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Rectangle(float Left, float Top, float Right, float Bottom);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Color(float Red, float Green, float Blue, float Alpha)
    {
        internal static Color FromHex(string value)
        {
            int rgb = Convert.ToInt32(value.Substring(1), 16);
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct GradientStop(float Position, Color Color);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Bezier(Vector2 FirstControl, Vector2 SecondControl, Vector2 End);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RenderProperties(int Type, int Format, int Alpha, float DpiX, float DpiY,
        int Usage, int MinimumFeatureLevel);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowProperties(nint Window, uint Width, uint Height, int PresentOptions);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GradientProperties(Vector2 Start, Vector2 End);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StrokeProperties(int StartCap, int EndCap, int DashCap, int Join,
        float MiterLimit, int DashStyle, float DashOffset);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int type, Guid* iid, nint options, nint* result);

    // Native interface slots follow the Windows SDK d2d1.h definitions, including inherited methods.
    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    internal static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result, (nint)(-1));
    }

    internal static void Release(ref nint instance)
    {
        if (instance == 0)
            return;
        Marshal.Release(instance);
        instance = 0;
    }

    internal static nint CreateFactory()
    {
        var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        nint factory = 0;
        Check(D2D1CreateFactory(0, &iid, 0, &factory));
        return factory;
    }

    internal static nint CreateTarget(nint factory, nint window, int width, int height)
    {
        // 96 DPI makes the window's Direct2D drawing coordinates equal its physical pixels.
        var properties = new RenderProperties(0, 87, 3, 96, 96, 0, 0);
        var hwnd = new WindowProperties(window, (uint)width, (uint)height, 0);
        nint target = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, RenderProperties*, WindowProperties*, nint*, int>)Slot(factory, 14))
            (factory, &properties, &hwnd, &target));
        return target;
    }

    internal static nint CreatePath(nint factory)
    {
        nint geometry = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 10))(factory, &geometry));
        return geometry;
    }

    internal static nint OpenPath(nint geometry)
    {
        nint sink = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(geometry, 17))(geometry, &sink));
        return sink;
    }

    internal static void SetWinding(nint sink) =>
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(sink, 3))(sink, 1);

    internal static void BeginFigure(nint sink, Vector2 start) =>
        ((delegate* unmanaged[Stdcall]<nint, Vector2, int, void>)Slot(sink, 5))(sink, start, 0);

    internal static void LineTo(nint sink, Vector2 end) =>
        ((delegate* unmanaged[Stdcall]<nint, Vector2, void>)Slot(sink, 10))(sink, end);

    internal static void CurveTo(nint sink, Bezier curve) =>
        ((delegate* unmanaged[Stdcall]<nint, Bezier*, void>)Slot(sink, 11))(sink, &curve);

    internal static void EndFigure(nint sink) =>
        ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(sink, 8))(sink, 1);

    internal static void ClosePath(nint sink) =>
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, 9))(sink));

    internal static Rectangle Bounds(nint geometry)
    {
        Rectangle bounds;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, Rectangle*, int>)Slot(geometry, 4))(geometry, 0, &bounds));
        return bounds;
    }

    internal static nint CreateRoundStroke(nint factory)
    {
        var properties = new StrokeProperties(0, 0, 0, 2, 10, 0, 0);
        nint stroke = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, StrokeProperties*, nint, uint, nint*, int>)Slot(factory, 11))
            (factory, &properties, 0, 0, &stroke));
        return stroke;
    }

    internal static nint SolidBrush(nint target, Color color)
    {
        nint brush = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Color*, nint, nint*, int>)Slot(target, 8))
            (target, &color, 0, &brush));
        return brush;
    }

    internal static nint GradientBrush(nint target, GradientStop[] stops)
    {
        nint collection = 0;
        try
        {
            fixed (GradientStop* values = stops)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, GradientStop*, uint, int, int, nint*, int>)Slot(target, 9))
                    (target, values, (uint)stops.Length, 0, 0, &collection));
            }
            nint brush = 0;
            var properties = new GradientProperties(default, new Vector2(1, 1));
            Check(((delegate* unmanaged[Stdcall]<nint, GradientProperties*, nint, nint, nint*, int>)Slot(target, 10))
                (target, &properties, 0, collection, &brush));
            return brush;
        }
        finally { Release(ref collection); }
    }

    internal static void SetGradientPoints(nint brush, Vector2 start, Vector2 end)
    {
        ((delegate* unmanaged[Stdcall]<nint, Vector2, void>)Slot(brush, 8))(brush, start);
        ((delegate* unmanaged[Stdcall]<nint, Vector2, void>)Slot(brush, 9))(brush, end);
    }

    internal static void BeginDraw(nint target) =>
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(target, 48))(target);

    internal static int EndDraw(nint target) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(target, 49))(target, 0, 0);

    internal static void SetTransform(nint target, Transform transform)
    {
        var matrix = new Matrix(transform.ScaleX, 0, 0, transform.ScaleY, transform.X, transform.Y);
        ((delegate* unmanaged[Stdcall]<nint, Matrix*, void>)Slot(target, 30))(target, &matrix);
    }

    internal static void Fill(nint target, Rectangle rectangle, nint brush) =>
        ((delegate* unmanaged[Stdcall]<nint, Rectangle*, nint, void>)Slot(target, 17))(target, &rectangle, brush);

    internal static void Fill(nint target, nint geometry, nint brush) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, void>)Slot(target, 23))(target, geometry, brush, 0);

    internal static void Stroke(nint target, nint geometry, nint brush, float width, nint style) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, float, nint, void>)Slot(target, 22))
            (target, geometry, brush, width, style);
}
