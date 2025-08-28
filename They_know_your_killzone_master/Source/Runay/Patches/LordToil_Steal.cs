
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using System.Collections.Generic;
using System.Linq;

namespace RunayAI.Patches
{
    public class LordToil_Steal : LordToil
    {
        private List<Pawn> looters;
        private List<Pawn> escorts;

        public override void Init()
        {
            base.Init();
            AssignRoles();
        }

        public override void UpdateAllDuties()
        {
            if (looters == null || escorts == null)
            {
                AssignRoles();
            }

            foreach (var looter in looters)
            {
                if (looter.Spawned && !looter.Downed)
                {
                    looter.mindState.duty = new PawnDuty(DutyDefOf.Steal); 
                }
            }

            foreach (var escort in escorts)
            {
                if (escort.Spawned && !escort.Downed)
                {
                    Pawn targetLooter = FindClosestLooter(escort);
                    if (targetLooter != null)
                    {
                        escort.mindState.duty = new PawnDuty(DutyDefOf.Escort, targetLooter, 5f); 
                    }
                    else
                    {
                        escort.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);
                    }
                }
            }
        }

        private void AssignRoles()
        {
            looters = new List<Pawn>();
            escorts = new List<Pawn>();

            var freePawns = lord.ownedPawns.Where(p => p.Spawned && !p.Downed && p.health.summaryHealth.SummaryHealthPercent > 0.5f).ToList();
            int numPawns = freePawns.Count;
            int numLooters = numPawns / 2;

            var sortedPawns = freePawns.OrderByDescending(p => p.GetStatValue(StatDefOf.CarryingCapacity)).ToList();

            for (int i = 0; i < sortedPawns.Count; i++)
            {
                if (i < numLooters)
                {
                    looters.Add(sortedPawns[i]);
                }
                else
                {
                    escorts.Add(sortedPawns[i]);
                }
            }
        }

        private Pawn FindClosestLooter(Pawn escort)
        {
            return looters.Where(l => l.Spawned && !l.Downed)
                          .OrderBy(l => l.Position.DistanceTo(escort.Position))
                          .FirstOrDefault();
        }
    }
}
