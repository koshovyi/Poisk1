namespace Poisk1.Core.Expansion;

/// <summary>
/// Registry of available expansion-card types. To add a new card (RAM, a game
/// or network adapter) — add an entry with a factory here; the slot menu picks it up.
/// </summary>
public static class CardCatalog
{
    public sealed record CardType(string Id, string DisplayName, Func<string, IExpansionCard> Create);

    public static readonly IReadOnlyList<CardType> Types = new[]
    {
        new CardType("rom", "B003 (ПЗУ-адаптер)", dataDir => RomCard.FromRomsFolder(dataDir)),
        new CardType("ram256", "B107 (ОЗУ 256 КБ)", _ => new RamCard("ram256", "B107 (ОЗУ 256 КБ)", 256)),
        new CardType("ram512", "B109 (ОЗУ 512 КБ)", _ => new RamCard("ram512", "B109 (ОЗУ 512 КБ)", 512)),
        new CardType("fdd", "B504 (НГМД)", dataDir => FddController.FromBios(dataDir)),
        new CardType("hdd", "B942 (НЖМД)", dataDir => HddController.FromBios(dataDir)),
        // new CardType("game", "Ігровий адаптер", ...),
    };

    /// <summary>
    /// Create a card by id (or null for "" / unknown).
    /// Special form "rom:&lt;name&gt;" — a ROM adapter with a single chosen module.
    /// </summary>
    public static IExpansionCard? Create(string id, string dataDir)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id.StartsWith("rom:", StringComparison.Ordinal))
            return RomCard.FromSingle(dataDir, id.Substring(4));
        if (id.StartsWith("fdd:", StringComparison.Ordinal))
            return FddController.FromBios(dataDir, id.Substring(4));
        if (id.StartsWith("hdd:", StringComparison.Ordinal))
            return HddController.FromBios(dataDir, id.Substring(4));
        foreach (var t in Types)
            if (t.Id == id) return t.Create(dataDir);
        return null;
    }

    public static string DisplayName(string id)
    {
        if (id.StartsWith("rom:", StringComparison.Ordinal))
            return "B003 (ПЗУ): " + id.Substring(4);
        if (id.StartsWith("fdd:", StringComparison.Ordinal))
            return "B504 (НГМД): " + id.Substring(4);
        if (id.StartsWith("hdd:", StringComparison.Ordinal))
            return "B942 (НЖМД): " + id.Substring(4);
        foreach (var t in Types)
            if (t.Id == id) return t.DisplayName;
        return "Порожньо";
    }
}
