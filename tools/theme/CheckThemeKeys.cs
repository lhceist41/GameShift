// Fails when a theme file the app loads at start names a resource key no theme defines.
// Run from the repository root: dotnet run tools/theme/CheckThemeKeys.cs [themes folder]
// GameShiftTheme.xaml and WpfUiOverrides.xaml are loaded with XamlReader, so the markup compiler
// never sees them, and one bad key there stops every start of the app.
#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Text.RegularExpressions;
using System.Xml.Linq;

var themes = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(FindRepositoryRoot(Directory.GetCurrentDirectory()), "src", "GameShift.App", "Themes");

XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var markupReference = new Regex(@"\{(?:StaticResource|DynamicResource)\s+(?:ResourceKey=)?([^\s}]+)\s*\}");
string[] tokenFiles = ["GameShift.Dark.xaml", "GameShift.Light.xaml", "GameShift.HighContrast.xaml"];
string[] loadedFiles = ["GameShiftTheme.xaml", "WpfUiOverrides.xaml"];
var failures = new List<string>();

XElement? Load(string file)
{
    try
    {
        return XDocument.Load(Path.Combine(themes, file)).Root;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
    {
        failures.Add($"{file}: cannot be read: {ex.Message}");
        return null;
    }
}

IEnumerable<(XElement Element, string Key)> References(XElement root)
{
    foreach (var element in root.Descendants())
    {
        if (element.Name.LocalName == "StaticResource" && element.Attribute("ResourceKey") is { } resourceKey)
            yield return (element, resourceKey.Value);
        foreach (var attribute in element.Attributes())
            foreach (Match match in markupReference.Matches(attribute.Value))
                yield return (element, match.Groups[1].Value);
    }
}

HashSet<string> KeysOf(XElement root) =>
    root.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().ToHashSet(StringComparer.Ordinal);

var tokens = new Dictionary<string, HashSet<string>>();
foreach (var file in tokenFiles)
{
    if (Load(file) is not { } root)
        continue;
    tokens[file] = KeysOf(root);
    // A token file may point one token at another, but only inside itself: it is swapped whole.
    foreach (var (_, key) in References(root))
        if (!tokens[file].Contains(key))
            failures.Add($"{file}: refers to {key}, which it does not define");
}

foreach (var file in loadedFiles)
{
    if (Load(file) is not { } root)
        continue;
    var ownKeys = KeysOf(root);
    var checkedCount = 0;
    foreach (var (element, key) in References(root))
    {
        // Every alias must name a token. In style setters, keys the file defines itself and stock
        // WPF-UI keys are not tokens.
        var isAlias = element.Name.LocalName == "StaticResource";
        if (!isAlias && (ownKeys.Contains(key) || !key.StartsWith("GS.", StringComparison.Ordinal)))
            continue;
        checkedCount++;
        foreach (var (tokenFile, keys) in tokens)
            if (!keys.Contains(key))
                failures.Add($"{file}: {key} is missing from {tokenFile}");
    }
    Console.WriteLine($"{file}: {checkedCount} references checked");
}

foreach (var failure in failures)
    Console.WriteLine("FAIL " + failure);
Console.WriteLine(failures.Count == 0 ? "Theme keys: all defined" : $"Theme keys: {failures.Count} failures");
return failures.Count == 0 ? 0 : 1;

static string FindRepositoryRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GameShift.sln")))
            return dir.FullName;
    }
    throw new InvalidOperationException("Run this from inside the GameShift repository.");
}
