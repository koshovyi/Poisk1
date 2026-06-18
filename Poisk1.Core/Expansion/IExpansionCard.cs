namespace Poisk1.Core.Expansion;

/// <summary>
/// A virtual expansion card for one of the Poisk's 4 slots.
/// Implementations: ROM adapter, RAM, game/network adapters, etc.
/// On install, a card claims the machine's resources (memory/ports).
/// </summary>
public interface IExpansionCard
{
    /// <summary>Stable type identifier (for config.json), e.g. "rom".</summary>
    string Id { get; }

    /// <summary>Name for the menu, e.g. "ПЗУ-адаптер".</summary>
    string DisplayName { get; }

    /// <summary>Attach the card's resources to the machine.</summary>
    void Install(Machine machine);

    /// <summary>Detach the card's resources from the machine.</summary>
    void Remove(Machine machine);
}
