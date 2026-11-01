// Regenerates the app and tray .ico files from the SVG masters in assets/icons.
// Run from the repository root: dotnet run tools/icons/IconGen.cs
// Edit the masters, never the .ico files: a second run must reproduce the committed outputs.
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
var masters = Path.Combine(root, "assets", "icons");
var iconsOut = Path.Combine(root, "src", "GameShift.App", "Assets", "Icons");
var resourcesOut = Path.Combine(root, "src", "GameShift.App", "Resources");

int[] appSizes = [16, 20, 24, 32, 48, 64, 256];
int[] traySizes = [16, 20, 24, 32];
string[] trayStates = ["idle", "active", "paused", "warning", "error"];

var jobs = new List<(string Output, Func<int, string> MasterFor, int[] Sizes)>
{
    // The 16 px master is pixel-tuned; 20 px reuses it because the 256 px art loses the chevron gap there.
    (Path.Combine(iconsOut, "app-icon.ico"), size => size <= 20 ? "app-icon-16.svg" : "app-icon-256.svg", appSizes),
    (Path.Combine(resourcesOut, "gameshift.ico"), size => size <= 20 ? "app-icon-16.svg" : "app-icon-256.svg", appSizes),
};
foreach (var state in trayStates)
{
    foreach (var theme in new[] { "dark", "light" })
    {
        var master = $"tray-{state}-{theme}-taskbar.svg";
        jobs.Add((Path.Combine(iconsOut, $"tray-{state}-{theme}.ico"), _ => master, traySizes));
    }
}

// WPF rendering needs an STA thread.
Exception? failure = null;
var worker = new Thread(() =>
{
    try
    {
        foreach (var (output, masterFor, sizes) in jobs)
        {
            var frames = sizes.Select(size => (size, Pixels: Render(Path.Combine(masters, masterFor(size)), size))).ToList();
            File.WriteAllBytes(output, WriteIco(frames));
            Console.WriteLine($"{Path.GetRelativePath(root, output)}: {string.Join(", ", sizes)}");
        }
    }
    catch (Exception ex)
    {
        failure = ex;
    }
});
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
if (failure != null)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
return 0;

static string FindRepositoryRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GameShift.sln")))
            return dir.FullName;
    }
    throw new InvalidOperationException("Run this from inside the GameShift repository.");
}

// Renders the subset of SVG the masters use (rect, path, circle with flat fills and strokes) and
// returns straight-alpha BGRA rows, top row first. Anything outside that subset fails the run
// instead of rendering differently from a browser.
static byte[] Render(string svgPath, int size)
{
    var svg = XElement.Load(svgPath);
    foreach (var attribute in svg.Attributes())
    {
        // Unprefixed, so xmlns here is only the default namespace declaration.
        if (attribute.Name.Namespace != XNamespace.None || attribute.Name.LocalName is not ("xmlns" or "viewBox" or "width" or "height"))
            throw new InvalidDataException($"{svgPath}: unsupported attribute {attribute} on <svg>");
    }

    var viewBox = svg.Attribute("viewBox")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray()
        ?? throw new InvalidDataException($"{svgPath}: no viewBox");
    if (viewBox.Length != 4 || viewBox[0] != 0 || viewBox[1] != 0)
        throw new InvalidDataException($"{svgPath}: viewBox must be 0 0 width height");

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / viewBox[2], size / viewBox[3]));
        foreach (var element in svg.Elements())
        {
            var (geometry, allowed) = element.Name.LocalName switch
            {
                "rect" => ((Geometry)new RectangleGeometry(
                    new Rect(Attr(element, "x", 0), Attr(element, "y", 0), Attr(element, "width"), Attr(element, "height")),
                    Attr(element, "rx", 0), Attr(element, "rx", 0)), new[] { "x", "y", "width", "height", "rx" }),
                "circle" => (new EllipseGeometry(
                    new Point(Attr(element, "cx"), Attr(element, "cy")), Attr(element, "r"), Attr(element, "r")), new[] { "cx", "cy", "r" }),
                // F1: SVG's default fill rule is nonzero; WPF path markup defaults to even-odd.
                "path" => (Geometry.Parse("F1 " + (element.Attribute("d")?.Value ?? throw new InvalidDataException($"{svgPath}: path without d"))), new[] { "d" }),
                var name => throw new InvalidDataException($"{svgPath}: unsupported element <{name}>"),
            };

            string[] paint = ["fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin"];
            foreach (var attribute in element.Attributes())
            {
                if (!allowed.Contains(attribute.Name.LocalName) && !paint.Contains(attribute.Name.LocalName))
                    throw new InvalidDataException($"{svgPath}: unsupported attribute {attribute.Name.LocalName} on <{element.Name.LocalName}>");
            }

            // SVG paints a missing fill black and a missing stroke not at all.
            var fill = Paint(svgPath, element.Attribute("fill")?.Value ?? "#000000");
            var strokeBrush = Paint(svgPath, element.Attribute("stroke")?.Value ?? "none");
            Pen? pen = null;
            if (strokeBrush != null)
            {
                var cap = element.Attribute("stroke-linecap")?.Value switch
                {
                    null or "butt" => PenLineCap.Flat,
                    "round" => PenLineCap.Round,
                    "square" => PenLineCap.Square,
                    var other => throw new InvalidDataException($"{svgPath}: unsupported stroke-linecap {other}"),
                };
                var join = element.Attribute("stroke-linejoin")?.Value switch
                {
                    null or "miter" => PenLineJoin.Miter,
                    "round" => PenLineJoin.Round,
                    "bevel" => PenLineJoin.Bevel,
                    var other => throw new InvalidDataException($"{svgPath}: unsupported stroke-linejoin {other}"),
                };
                pen = new Pen(strokeBrush, Attr(element, "stroke-width", 1))
                {
                    StartLineCap = cap,
                    EndLineCap = cap,
                    LineJoin = join,
                    MiterLimit = 4, // SVG's default; WPF's is 10
                };
                pen.Freeze();
            }

            dc.DrawGeometry(fill, pen, geometry);
        }
    }

    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
    var pixels = new byte[size * size * 4];
    straight.CopyPixels(pixels, size * 4, 0);
    return pixels;
}

static SolidColorBrush? Paint(string svgPath, string value)
{
    if (value == "none")
        return null;
    if (value.Length != 7 || value[0] != '#'
        || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        throw new InvalidDataException($"{svgPath}: colours must be #RRGGBB, got {value}");
    var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    brush.Freeze();
    return brush;
}

static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

static double Attr(XElement element, string name, double? fallback = null)
{
    var text = element.Attribute(name)?.Value;
    if (text != null)
        return Number(text);
    return fallback ?? throw new InvalidDataException($"<{element.Name.LocalName}> needs {name}");
}

// Frames below 256 px are 32-bit DIBs, which every Windows icon loader reads (System.Drawing.Icon
// included, which the tray uses); the 256 px frame is PNG as Explorer expects.
static byte[] WriteIco(List<(int Size, byte[] Pixels)> frames)
{
    var images = frames.Select(f => f.Size == 256 ? Png(f.Size, f.Pixels) : Dib(f.Size, f.Pixels)).ToList();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].Size;
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        writer.Write(image);
    writer.Flush();
    return stream.ToArray();
}

static byte[] Dib(int size, byte[] pixels)
{
    var maskStride = (size + 31) / 32 * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(40);
    writer.Write(size);
    writer.Write(size * 2); // colour rows plus mask rows
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    for (var y = size - 1; y >= 0; y--)
        writer.Write(pixels, y * size * 4, size * 4);
    for (var y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < size; x++)
        {
            if (pixels[(y * size + x) * 4 + 3] == 0)
                row[x / 8] |= (byte)(0x80 >> (x % 8));
        }
        writer.Write(row);
    }
    writer.Flush();
    return stream.ToArray();
}

static byte[] Png(int size, byte[] pixels)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}
