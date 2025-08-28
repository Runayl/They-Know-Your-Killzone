using HarmonyLib;
using RunayAI.Extensions;
using System.Linq;
using Verse;

namespace RunayAI.Patches
{
    internal class LordToil_AssaultColonyBreaching
    {
        [HarmonyPatch(typeof(RimWorld.LordToil_AssaultColonyBreaching), "UpdateAllDuties")]
        static class LordToil_AssaultColonyBreaching_UpdateAllDuties
        {
            static void Postfix(RimWorld.LordToil_AssaultColonyBreaching __instance)
            {
                __instance.Data.maxRange = 65f;
                BreachingUtility.currentLordForPatching = null;
            }

            static bool Prefix(RimWorld.LordToil_AssaultColonyBreaching __instance)
            {
                BreachingUtility.currentLordForPatching = __instance.lord;
                if (__instance.useAvoidGrid && __instance.lord.ownedPawns.Any(x => x.def.ToString().Matches("centipede")))
                {
                    __instance.useAvoidGrid = false;
                }

                return true;
            }
        }
    }
}
