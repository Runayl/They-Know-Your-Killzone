
using RimWorld;
using Verse;
using Verse.AI;
using System.Collections.Generic;
using System.Linq;

namespace RunayAI.Patches
{
    public class JobGiver_Steal : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.mindState.duty == null || pawn.mindState.duty.def != DutyDefOf.Steal)
            {
                return null;
            }

            float capacity = pawn.GetStatValue(StatDefOf.CarryingCapacity);
            float currentMass = MassUtility.GearAndInventoryMass(pawn);
            float remainingCapacity = capacity - currentMass;

            if (remainingCapacity <= 0)
            {
                return ExitJob(pawn);
            }

            List<Thing> stealableThings = FindStealableThings(pawn);
            List<Thing> itemsToSteal = SelectItemsToSteal(pawn, stealableThings, remainingCapacity);

            if (itemsToSteal.Count > 0)
            {
                Job job = JobMaker.MakeJob(JobDefOf.Steal);
                job.targetQueueA = new List<LocalTargetInfo>(itemsToSteal.Select(t => new LocalTargetInfo(t)));
                if (RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
                {
                    job.targetB = new LocalTargetInfo(exitSpot);
                }
                else
                {
                    return null;
                }
                return job;
            }

            return ExitJob(pawn);
        }

        private Job ExitJob(Pawn pawn)
        {
            if (RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
            {
                return JobMaker.MakeJob(JobDefOf.Goto, exitSpot);
            }
            return null;
        }

        private List<Thing> FindStealableThings(Pawn pawn)
        {
            var allItems = pawn.Map.listerThings.AllThings.Where(t => t.def.EverHaulable && t.def.PlayerAcquirable && !t.IsForbidden(pawn.Faction)).ToList();

            var packableBuildings = pawn.Map.listerBuildings.allBuildingsColonist.Where(b =>
                b.def.Minifiable &&
                b.Faction == Faction.OfPlayer &&
                pawn.CanReserve(b)
            ).Cast<Thing>();

            return allItems.Concat(packableBuildings).ToList();
        }

        private List<Thing> SelectItemsToSteal(Pawn pawn, List<Thing> things, float capacity)
        {
            var selected = new List<Thing>();
            float totalMass = 0f;

            var sortedThings = things.OrderByDescending(t => t.MarketValue / t.GetStatValue(StatDefOf.Mass)).ToList();

            foreach (var thing in sortedThings)
            {
                float thingMass = thing.GetStatValue(StatDefOf.Mass);
                if (totalMass + thingMass <= capacity)
                {
                    if (pawn.CanReserve(thing))
                    {
                        selected.Add(thing);
                        totalMass += thingMass;
                    }
                }
            }

            return selected;
        }
    }
}
