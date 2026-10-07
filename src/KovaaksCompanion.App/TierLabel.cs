namespace KovaaksCompanion.App;

/// <summary>Display form of a tier name: upper case, except names the data writes in lower case on purpose.</summary>
static class TierText
{
    public static string Label(string name) => name.Length > 0 && char.IsLower(name[0]) ? name : name.ToUpperInvariant();
}
