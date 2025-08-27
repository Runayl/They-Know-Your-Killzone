using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Unity.Baselib.LowLevel;
using UnityEngine;
using UnityEngine.SocialPlatforms;
using Verse;
using Verse.AI;
using Verse.Noise;

namespace RunayAI.Patches
{
    [HarmonyPatch(typeof(AvoidGrid), "Regenerate")]
    public static class AvoidGrid_Regenerate
    {
        static Traverse instance;
        static int counter = 0;
        static ByteGrid tempGrid;
        public static int lastUpdateTicks = 0;
        const bool runMannableCheck = false;

        static bool Prefix(Verse.AI.AvoidGrid __instance)
        {
            instance = Traverse.Create(__instance);

            var gridDirty = instance.Field("gridDirty");
            if (lastUpdateTicks != 0 && (Find.TickManager.TicksGame - lastUpdateTicks) / 60 < 5)
            {
                gridDirty.SetValue(false);
                return false;
            }

            gridDirty.SetValue(false);

            counter = 0;

            try
            {
                var corpses = __instance.map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Where(x => ((Corpse)x).Age < 1800
                    && (((Corpse)x).InnerPawn?.Faction?.HostileTo(Faction.OfPlayer) ?? false));
                foreach (Corpse corpse in corpses)
                {
                    PrintAvoidGridAroundPos(__instance, __instance.map, corpse.Position, 1, 1000 * (1800 - corpse.Age) / 1800);
                }

                var downed = __instance.map.mapPawns.SpawnedDownedPawns.Where(x => x.Faction.HostileTo(Faction.OfPlayer));
                foreach (var raider in downed)
                {
                    PrintAvoidGridAroundPos(__instance, __instance.map, raider.Position, 1);
                }

                var draftedColonists = __instance.map.PlayerPawnsForStoryteller.Where(x =>
                    x.Drafted && x.equipment?.PrimaryEq != null && x.CurJobDef == JobDefOf.Wait_Combat && x.TargetCurrentlyAimingAt == null);
                foreach (var pawn in draftedColonists)
                {
                    var verb = pawn.equipment.PrimaryEq.PrimaryVerb;
                    if (verb.IsMeleeAttack)
                    {
                        PrintAvoidGridAroundPos(__instance, __instance.map, pawn.Position, 1);
                    }
                    else
                    {
                        PrintAvoidGridLOSThing(__instance, pawn.Map, pawn.Position, verb, true);
                    }
                }

                List<Building> allBuildingsColonist = __instance.map.listerBuildings.allBuildingsColonist;
                for (int i = 0; i < allBuildingsColonist.Count; i++)
                {
                    if (allBuildingsColonist[i].def.building.ai_combatDangerous)
                    {
                        CompEquippable equip;
                        var threatCondition = false;
                        var building = allBuildingsColonist[i];
                        if (Init.combatExtended && building.GetType().ToString() == "CombatExtended.Building_TurretGunCE")
                        {
                            equip = (CompEquippable)building.GetType().GetProperty("GunCompEq").GetValue(building, null);
                            var active = (bool)building.GetType().GetProperty("Active").GetValue(building, null);
                            var activePowerSource = (CompPowerTrader)building.GetType().GetProperty("PowerComp").GetValue(building, null);
                            var currentTarget = (LocalTargetInfo)building.GetType().GetProperty("CurrentTarget").GetValue(building, null);
                            var emptyMagazine = (bool)building.GetType().GetProperty("EmptyMagazine").GetValue(building, null);
                            var isMannable = (bool)building.GetType().GetProperty("IsMannable").GetValue(building, null);
                            var mannedByColonist = ((CompMannable)building.GetType().GetProperty("MannableComp").GetValue(building, null))?.MannedNow ?? false;
                            threatCondition = (active || (activePowerSource?.PowerNet?.CanPowerNow(activePowerSource) ?? false))
                                && currentTarget == null && !emptyMagazine && equip != null && (!runMannableCheck || !isMannable || mannedByColonist);
                        }
                        else
                        {
                            Building_TurretGun building_TurretGun = allBuildingsColonist[i] as Building_TurretGun;
                            equip = building_TurretGun.GunCompEq;
                            threatCondition = equip != null && (building_TurretGun.Active
                                || (building_TurretGun.PowerComp?.PowerNet?.CanPowerNow(Traverse.Create(building_TurretGun).Field("powerComp").GetValue<CompPowerTrader>()) ?? false))
                                && building_TurretGun.TargetCurrentlyAimingAt == null
                                && (!runMannableCheck || !building_TurretGun.IsMannable || Traverse.Create(building_TurretGun).Field("MannedByColonist").GetValue<bool>())
                                && (building_TurretGun.refuelableComp?.HasFuel ?? true);
                        }
                        if (threatCondition)
                        {
                            PrintAvoidGridLOSThing(__instance, building.Map, building.Position, equip.PrimaryVerb);
                        }
                    }
                }
                instance.Method("ExpandAvoidGridIntoEdifices").GetValue();
                //Log.Message($"Count: {counter}");
            }
            catch (Exception e)
            {
                Log.Error($"THEYAREKNOWYOURKILLZONE: {e.Message}\n{e.StackTrace}");
            }

            lastUpdateTicks = Find.TickManager.TicksGame;
            return false;
        }        

        private static void PrintAvoidGridLOSThing(Verse.AI.AvoidGrid __instance, Map map, IntVec3 pos, Verb verb, bool isPawn = false)
        {
            if (verb.Caster.def.defName == "Turret_RocketswarmLauncher")
            {
                return;
            }
            float range = verb.verbProps.range;            
            tempGrid = new ByteGrid(map);
            float num = verb.verbProps.EffectiveMinRange(true);
            int num2 = GenRadial.NumCellsInRadius(range);
            for (int i = num2; i > (num < 1f ? 0 : GenRadial.NumCellsInRadius(num)); i--)
            {
                IntVec3 intVec = pos + GenRadial.RadialPattern[i];
                if (intVec.InBounds(map) && intVec.WalkableByNormal(map)
                    && tempGrid[intVec] == 0
                    && GenSight.LineOfSight(pos, intVec, map, true, IncrementAvoidGrid, 0, 0))
                {
                    counter++;
                }
            }
        }

        private static bool IncrementAvoidGrid(IntVec3 cell)
        {
            if (tempGrid[cell] == 0)
            {
                instance.Method("IncrementAvoidGrid", cell, Init.settings.costLOS).GetValue();
                IncrementLocalAvoidGrid(tempGrid, cell, Init.settings.costLOS);
            }
            return true;
        }

        private static void IncrementLocalAvoidGrid(ByteGrid grid, IntVec3 c, int num)
        {
            byte b = grid[c];
            b = (byte)Mathf.Min(255, (int)b + num);
            grid[c] = b;
        }

        public static void PrintAvoidGridAroundPos(AvoidGrid __instance, Map map, IntVec3 pos, int radius, int incAmount = -1)
        {
            if (incAmount == -1)
            {
                incAmount = Init.settings.costLOS;
            }
            for (int i = 0; i < GenRadial.NumCellsInRadius(radius); i++)
            {
                IntVec3 intVec = pos + GenRadial.RadialPattern[i];
                if (intVec.InBounds(map) && intVec.WalkableByNormal(map))
                {
                    int cellIndex = map.cellIndices.CellToIndex(intVec);
                    byte val = __instance.Grid[cellIndex];
                    if (val == 0)
                    {
                        Traverse.Create(__instance).Method("IncrementAvoidGrid", intVec, incAmount).GetValue();
                    }
                }
            }
        }
    }
            
}
