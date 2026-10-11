using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Shared.Models;

namespace MW_GC.EventManager.Web.Services;

/// <summary>The one mapping from a library Game and Activity to the Selection an Event stores.</summary>
public static class SelectionSnapshot
{
    public static Selection From(GameEntity game, ActivityEntity act) => new()
    {
        Game = new Game { Id = game.Id, Name = game.Name, ImageUrl = game.ImageUrl, Website = game.Website, IconUrl = game.IconUrl },
        Activity = new Activity
        {
            Id = act.Id, GameId = act.GameId, Name = act.Name, Description = act.Description,
            Rules = act.Rules, SetupRequirements = act.SetupRequirements, Comments = act.Comments
        }
    };
}
