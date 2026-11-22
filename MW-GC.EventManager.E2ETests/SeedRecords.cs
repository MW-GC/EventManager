namespace MW_GC.EventManager.E2ETests;

// What the API answers for an item a flow seeded: only the fields the flows read.

/// <summary>A Game, Theme or Holiday.</summary>
internal sealed record SeededItem(Guid Id, string Name);

internal sealed record SeededActivity(Guid Id, Guid GameId, string Name, string Description, string Rules);

internal sealed record SeededEvent(Guid Id, string Name, Guid? WinnerActivityId);

// What a flow sends to seed an item.

internal sealed record NameBody(string Name);

internal sealed record ActivityBody(Guid GameId, string Name, string Description, string Rules);

/// <summary>A Selection's snapshot of its Game and Activity, as the API stores it on the Event.</summary>
internal sealed record SelectionBody(SeededItem Game, SeededActivity Activity);

internal sealed record EventBody(string Name, DateTimeOffset Date, bool UniqueGamesOnly, IReadOnlyList<SelectionBody> Selections)
{
    private static readonly DateTimeOffset Date20261001 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An Event on a fixed date with these Selections.</summary>
    public static EventBody Of(string name, bool uniqueGamesOnly, params SelectionBody[] selections) =>
        new(name, Date20261001, uniqueGamesOnly, selections);
}
