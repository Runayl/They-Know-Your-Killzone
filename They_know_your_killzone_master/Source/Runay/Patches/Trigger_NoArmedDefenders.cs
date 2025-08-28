
using RimWorld;
using Verse;
using Verse.AI.Group;
using System.Linq;

namespace RunayAI.Patches
{
    public class Trigger_NoArmedDefenders : Trigger
    {
        public override bool ActivateOn(Lord lord, TriggerSignal signal)
        {
            if (signal.type == TriggerSignalType.Tick)
            {
                var armedDefenders = lord.Map.mapPawns.PawnsInFaction(Faction.OfPlayer)
                    .Where(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && (p.equipment.Primary != null || p.apparel.WornApparel.Any(a => a.def.IsWeapon)))
                    .ToList();

                return armedDefenders.Count == 0;
            }
            return false;
        }
    }
}
