using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Transform = AgentBell.Direct2D.Transform;
using Vector2 = AgentBell.Direct2D.Vector2;

namespace AgentBell;

internal sealed class Direct2DArtwork : IDisposable
{
    private static readonly Regex PathTokens = new(@"[A-Za-z]|-?(?:\d*\.)?\d+");
    private static readonly Regex TransformParts = new(@"(translate|scale)\(([^)]+)\)");

    private Vector2 canvas;
    private Direct2D.Rectangle card;
    private Direct2D.Color cardColor;
    private Direct2D.Color letteringColor;
    private float letteringStroke;
    private Direct2D.GradientStop[] backgroundStops = [];
    private Direct2D.GradientStop[] cornerStops = [];
    private readonly List<Shape> corners = [];
    private readonly List<Shape> letters = [];
    private nint factory;
    private nint roundStroke;
    private nint target;
    private nint backgroundBrush;
    private nint cornerBrush;
    private nint cardBrush;
    private nint letteringBrush;
    private int targetWidth;
    private int targetHeight;

    internal static Direct2DArtwork Load()
    {
        using var resource = typeof(Direct2DArtwork).Assembly.GetManifestResourceStream("AgentBell.OrangeSign")
            ?? throw new InvalidDataException("The C artwork is missing.");
        XElement svg = XDocument.Load(resource).Root ?? throw new InvalidDataException("The SVG is empty.");
        var space = svg.Name.Namespace;
        var viewBox = ReadNumbers(Attribute(svg, "viewBox"));
        XElement defs = svg.Element(space + "defs")!;
        XElement card = svg.Elements(space + "rect").Single(x => (string?)x.Attribute("id") == "status-card");
        XElement corners = svg.Elements(space + "g").Single(x => (string?)x.Attribute("id") == "corners");
        XElement letters = svg.Elements(space + "g").Single(x => (string?)x.Attribute("id") == "lettering");

        var artwork = new Direct2DArtwork
        {
            canvas = new Vector2(viewBox[2], viewBox[3]),
            card = new Direct2D.Rectangle(Number(card, "x"), Number(card, "y"),
                Number(card, "x") + Number(card, "width"), Number(card, "y") + Number(card, "height")),
            cardColor = Direct2D.Color.FromHex(Attribute(card, "fill")),
            letteringColor = Direct2D.Color.FromHex(Attribute(letters, "fill")),
            letteringStroke = Number(letters, "stroke-width"),
            backgroundStops = ReadStops(defs, "orange"),
            cornerStops = ReadStops(defs, "red")
        };

        try
        {
            artwork.factory = Direct2D.CreateFactory();
            artwork.roundStroke = Direct2D.CreateRoundStroke(artwork.factory);
            foreach (var path in corners.Elements(space + "path"))
                artwork.corners.Add(artwork.MakeShape(path));
            foreach (var path in letters.Elements(space + "path"))
                artwork.letters.Add(artwork.MakeShape(path));
            return artwork;
        }
        catch
        {
            artwork.Dispose();
            throw;
        }
    }

    internal void Draw(nint window, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        int result = Direct2D.RecreateTarget;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            EnsureTarget(window, width, height);
            Direct2D.BeginDraw(target);
            DrawFrame(width, height);
            result = Direct2D.EndDraw(target);
            if (result != Direct2D.RecreateTarget)
                break;
            ReleaseTarget();
        }
        Direct2D.Check(result);
    }

    private void DrawFrame(int width, int height)
    {
        float scale = Math.Min(width / canvas.X, height / canvas.Y);
        float freeX = width - canvas.X * scale;
        float freeY = height - canvas.Y * scale;
        Transform sizing = Transform.Scale(scale, scale);

        Direct2D.SetTransform(target, Transform.Identity);
        Direct2D.SetGradientPoints(backgroundBrush, default, new Vector2(0, height));
        Direct2D.Fill(target, new Direct2D.Rectangle(0, 0, width, height), backgroundBrush);

        // In SVG order: top-left, bottom-left, bottom-right.
        for (int index = 0; index < corners.Count; index++)
        {
            Shape shape = corners[index];
            var bounds = shape.Bounds;
            Direct2D.SetTransform(target, sizing * Transform.Translate(
                index == 2 ? freeX : 0, index == 0 ? 0 : freeY));
            Direct2D.SetGradientPoints(cornerBrush, new Vector2(bounds.Left, bounds.Top),
                new Vector2(bounds.Right, bounds.Bottom));
            Direct2D.Fill(target, shape.Geometry, cornerBrush);
        }

        Transform center = sizing * Transform.Translate(freeX / 2, freeY / 2);
        Direct2D.SetTransform(target, center);
        Direct2D.Fill(target, card, cardBrush);

        foreach (Shape shape in letters)
        {
            Direct2D.SetTransform(target, shape.Transform * center);
            Direct2D.Fill(target, shape.Geometry, letteringBrush);
            Direct2D.Stroke(target, shape.Geometry, letteringBrush, letteringStroke, roundStroke);
        }
    }

    private void EnsureTarget(nint window, int width, int height)
    {
        if (target != 0 && width == targetWidth && height == targetHeight)
            return;

        ReleaseTarget();
        try
        {
            target = Direct2D.CreateTarget(factory, window, width, height);
            backgroundBrush = Direct2D.GradientBrush(target, backgroundStops);
            cornerBrush = Direct2D.GradientBrush(target, cornerStops);
            cardBrush = Direct2D.SolidBrush(target, cardColor);
            letteringBrush = Direct2D.SolidBrush(target, letteringColor);
            targetWidth = width;
            targetHeight = height;
        }
        catch
        {
            ReleaseTarget();
            throw;
        }
    }

    private void ReleaseTarget()
    {
        Direct2D.Release(ref letteringBrush);
        Direct2D.Release(ref cardBrush);
        Direct2D.Release(ref cornerBrush);
        Direct2D.Release(ref backgroundBrush);
        Direct2D.Release(ref target);
    }

    public void Dispose()
    {
        ReleaseTarget();
        foreach (Shape shape in letters.Concat(corners))
            shape.Dispose();
        letters.Clear();
        corners.Clear();
        Direct2D.Release(ref roundStroke);
        Direct2D.Release(ref factory);
    }

    private Shape MakeShape(XElement element)
    {
        nint geometry = Direct2D.CreatePath(factory);
        nint sink = 0;
        try
        {
            sink = Direct2D.OpenPath(geometry);
            Direct2D.SetWinding(sink);
            WritePath(sink, Attribute(element, "d"));
            Direct2D.ClosePath(sink);
            return new Shape(geometry, Direct2D.Bounds(geometry), ReadTransform((string?)element.Attribute("transform")));
        }
        catch
        {
            Direct2D.Release(ref sink);
            Direct2D.Release(ref geometry);
            throw;
        }
        finally { Direct2D.Release(ref sink); }
    }

    private static void WritePath(nint sink, string path)
    {
        var tokens = PathTokens.Matches(path);
        int index = 0;
        Vector2 point = default;
        while (index < tokens.Count)
        {
            switch (tokens[index++].Value[0])
            {
                case 'M':
                    point = ReadPoint();
                    Direct2D.BeginFigure(sink, point);
                    break;
                case 'L':
                    point = ReadPoint();
                    Direct2D.LineTo(sink, point);
                    break;
                case 'H':
                    point.X = ReadNumber();
                    Direct2D.LineTo(sink, point);
                    break;
                case 'V':
                    point.Y = ReadNumber();
                    Direct2D.LineTo(sink, point);
                    break;
                case 'C':
                    var first = ReadPoint();
                    var second = ReadPoint();
                    point = ReadPoint();
                    Direct2D.CurveTo(sink, new Direct2D.Bezier(first, second, point));
                    break;
                case 'Z':
                    Direct2D.EndFigure(sink);
                    break;
                default:
                    throw new InvalidDataException("Unsupported path in the C artwork.");
            }
        }

        Vector2 ReadPoint() => new(ReadNumber(), ReadNumber());
        float ReadNumber() => float.Parse(tokens[index++].Value, CultureInfo.InvariantCulture);
    }

    private static Transform ReadTransform(string? value)
    {
        var transform = Transform.Identity;
        if (value is null)
            return transform;

        foreach (Match match in TransformParts.Matches(value))
        {
            float[] coordinates = ReadNumbers(match.Groups[2].Value);
            Transform part = match.Groups[1].Value == "translate"
                ? Transform.Translate(coordinates[0], coordinates[1])
                : Transform.Scale(coordinates[0], coordinates[1]);
            transform = part * transform;
        }
        return transform;
    }

    private static Direct2D.GradientStop[] ReadStops(XElement defs, string id)
    {
        XElement gradient = defs.Elements().Single(e => (string?)e.Attribute("id") == id);
        return gradient.Elements().Select(stop => new Direct2D.GradientStop(
            (string?)stop.Attribute("offset") is string offset ? float.Parse(offset, CultureInfo.InvariantCulture) : 0,
            Direct2D.Color.FromHex(Attribute(stop, "stop-color")))).ToArray();
    }

    private static float[] ReadNumbers(string values) => values.Split([' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    private static float Number(XElement element, string name) => float.Parse(Attribute(element, name), CultureInfo.InvariantCulture);

    private static string Attribute(XElement element, string name) =>
        (string?)element.Attribute(name) ?? throw new InvalidDataException($"The C artwork is missing the {name} attribute.");

    private sealed class Shape(nint geometry, Direct2D.Rectangle bounds, Transform transform) : IDisposable
    {
        internal nint Geometry => geometry;
        internal Direct2D.Rectangle Bounds { get; } = bounds;
        internal Transform Transform { get; } = transform;

        public void Dispose() => Direct2D.Release(ref geometry);
    }
}
