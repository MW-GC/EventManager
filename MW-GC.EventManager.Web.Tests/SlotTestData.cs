using MW_GC.EventManager.Shared.Entities;
using MW_GC.EventManager.Web.Services;

namespace MW_GC.EventManager.Web.Tests;

// What the slot tests (SlotPlannerFixture and SlotPageFixture, and the classes on them) share without state:
// the Activity and slot builders, a Random that always draws first, and the planner's no-match messages.
internal static class SlotTestData
{
    internal const string NoMatchMessage = "No activities match the current filters. Adjust the filters or add activities to the library.";
    internal const string AddSlotNoMatchReason = "No unused activity matches the current filters. Adjust the filters or add activities to the library.";

    // Stable sort keys and first-choice draws force the greedy dead end.
    internal sealed class FirstChoiceRandom : Random
    {
        public override int Next() => 0;
        public override int Next(int maxValue) => 0;
    }

    internal static ActivityEntity Activity(Guid game) => new() { Id = Guid.NewGuid(), GameId = game };

    internal static SlotSelection Slot(ActivityEntity activity) => new() { GameId = activity.GameId, ActivityId = activity.Id };
}
