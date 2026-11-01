namespace GameShift.Core.Config;

/// <summary>
/// The theme the user chose. Saved as its number, so the values never change; a number this
/// version does not know reads as <see cref="System"/>.
/// </summary>
public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}
