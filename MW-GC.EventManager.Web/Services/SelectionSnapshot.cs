using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

/// <summary>
/// The one mapping from a library Game and Activity to the Selection an Event stores. The snapshot
/// carries no Comments: those are read live from the Activity (ADR 0001).
/// </summary>
public static class SelectionSnapshot
{
    /// <summary>
    /// The Selection: the Game, and the Activity's text as the Library has it today, without its tags. The Activity
    /// part is built here inline (#200.14): it had one caller and was not a Selection, so it is no longer a second
    /// <c>From</c>.
    /// </summary>
    public static Selection From(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name, ImageUrl = game.ImageUrl, Website = game.Website, IconUrl = game.IconUrl },
        Activity = new Activity
        {
            Id = act.Id, GameId = act.GameId, Name = act.Name, Description = act.Description,
            Rules = act.Rules, SetupRequirements = act.SetupRequirements
        }
    };
}
