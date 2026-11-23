using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace ProjectHub.Edge.Desktop;

internal static class BrandMarks
{
    public static DrawingImage Micro { get; } = Load("edge-micro.svg");
    public static DrawingImage Standard { get; } = Load("edge.svg");

    private static DrawingImage Load(string file)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://ProjectHub.Edge.Desktop/Assets/{file}"));
        var svg = XElement.Load(stream);
        var ns = svg.Name.Namespace;
        var body = svg.Elements(ns + "path").Single();
        var shape = Geometry.Parse((string)body.Attribute("d")!);

        // Both bundled marks use filled and round-stroked cuts on a white luminance mask.
        foreach (var path in svg.Descendants(ns + "mask").Single().Descendants(ns + "path"))
        {
            var cut = Geometry.Parse((string)path.Attribute("d")!);
            if ((double?)path.Attribute("stroke-width") is { } width)
                cut = cut.GetWidenedGeometry(new Pen(Brushes.Black, width, lineCap: PenLineCap.Round));
            shape = new CombinedGeometry(GeometryCombineMode.Exclude, shape, cut);
        }

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, Rect.Parse((string)svg.Attribute("viewBox")!));
            context.DrawGeometry(Brush.Parse((string)body.Attribute("fill")!), null, shape);
        }
        return new DrawingImage { Drawing = drawing };
    }
}
