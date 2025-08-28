using HarmonyLib;
using RimWorld;
using System;
using System.Linq;
using Verse.AI.Group;

namespace RunayAI.Patches
{
    internal class LordJob_AssaultColony
    {
        [HarmonyPatch(typeof(RimWorld.LordJob_AssaultColony), MethodType.Constructor, new Type[] { typeof(Faction), 
            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        static class LordJob_AssaultColony_Ctor
        {
            static void Postfix(ref RimWorld.LordJob_AssaultColony __instance)
            {
                var instance = Traverse.Create(__instance);
                if (instance.Field("assaulterFaction").GetValue<Faction>().def.techLevel >= Init.settings.minSmartTechLevel)
                {
                    instance.Field("breachers").SetValue(true);
                    instance.Field("useAvoidGridSmart").SetValue(!Init.combatAi);
                }
                JobGiver_AISapper.pathCostCache.Clear();
                JobGiver_AISapper.findNewPaths = true;
            }
        }

        [HarmonyPatch(typeof(RimWorld.LordJob_AssaultColony), "CreateGraph")]
        static class LordJob_AssaultColony_CreateGraph_Patch
        {
            static void Postfix(ref StateGraph __result, RimWorld.LordJob_AssaultColony __instance)
            {
                var assaulterFaction = Traverse.Create(__instance).Field("assaulterFaction").GetValue<Faction>();
                if (assaulterFaction == null || assaulterFaction.def.techLevel < Init.settings.minSmartTechLevel)
                {
                    return;
                }

                LordToil_Steal stealToil = new LordToil_Steal();
                __result.AddToil(stealToil);

                LordToil assaultToil = __result.lordToils.FirstOrDefault(t => t is LordToil_AssaultColony);

                if (assaultToil != null)
                {
                    Transition stealTransition = new Transition(assaultToil, stealToil);
                    stealTransition.AddTrigger(new Trigger_TicksPassed(5000)); 
                    stealTransition.AddTrigger(new Trigger_NoArmedDefenders());
                    __result.AddTransition(stealTransition);
                }
            }
        }
    }
}
