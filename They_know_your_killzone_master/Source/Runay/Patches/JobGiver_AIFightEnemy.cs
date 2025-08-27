using HarmonyLib;
using RimWorld;
using Verse.AI;
using Verse;

namespace RunayAI.Patches
{
    public class JobGiver_AIFightEnemy
    {
        public static class JobGiver_AIFightEnemy_TryGiveJob
        {
            static void Prefix(Pawn pawn, RimWorld.JobGiver_AIFightEnemy __instance)
            {
                Traverse.Create(__instance).Field("needLOSToAcquireNonPawnTargets").SetValue(true);
            }

            static void Postfix(Pawn pawn, ref Job __result)
            {
                if (__result != null && __result.targetA.Thing != null && __result.def == JobDefOf.AttackMelee)
                {
                    int pawnCellIndex = pawn.Map.cellIndices.CellToIndex(pawn.Position);
                    int targetCellIndex = pawn.Map.cellIndices.CellToIndex(__result.targetA.Thing.Position);
                    if (pawn.Position.DistanceTo(__result.targetA.Cell) > 3 && pawn.Map.avoidGrid.Grid[pawnCellIndex] == 0 
                        && pawn.Map.avoidGrid.Grid[targetCellIndex] > 0 || !pawn.CanReach(__result.targetA.Thing, PathEndMode.Touch, Danger.Deadly))
                    {
                        __result = null;
                    }
                }
            }
        }
    }
}
