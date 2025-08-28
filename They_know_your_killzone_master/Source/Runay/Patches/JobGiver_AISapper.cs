using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Unity.Jobs;
using static UnityEngine.GraphicsBuffer;
using Steamworks;
using static RunayAI.Patches.JobGiver_AISapper;
using System;

namespace RunayAI.Patches
{
    public static class JobGiver_AISapper
    {
        public class CachedPath
        {
            public Pawn pawn;
            public IAttackTarget attackTarget;
            public Thing blockingThing;
            public IntVec3 cellBefore;
            public IntVec3 cellAfter;
            public List<int> excludeList = new List<int>();

            public CachedPath(Pawn pawn, IAttackTarget targetThing, Thing blockingThing, IntVec3 cellBefore, IntVec3 cellAfter)
            {
                this.pawn = pawn;
                this.attackTarget = targetThing;
                this.blockingThing = blockingThing;
                this.cellBefore = cellBefore;
                this.cellAfter = cellAfter;
            }
        }

        public static List<CachedPath> pathCostCache = new List<CachedPath>();

        public static bool findNewPaths = true;

        [HarmonyPatch(typeof(RimWorld.JobGiver_AISapper), "TryGiveJob")]
        public static class JobGiver_AISapper_TryGiveJob_Patch
        {
            public static bool Prefix(Pawn pawn, ref Job __result)
            {
                if (pawn.Faction == Faction.OfInsects || !pawn.Map.IsPlayerHome
                    || pawn.mindState?.duty?.def == DutyDefOf.AssaultThing
                    || pawn.mindState?.duty?.def == DutyDefOf.PrisonerEscapeSapper)
                {
                    return true;
                }

                var findPathMethod = AccessTools.Method(typeof(PathFinder), "FindPath", new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) });
                if (findPathMethod == null)
                {
                    Log.ErrorOnce("RunayAI Sapper: Could not find method PathFinder.FindPath via reflection. AI will not function correctly.", 8675309);
                    return true;
                }

                IntVec3 intVec = pawn.mindState.duty.focus.Cell;
                if (intVec.IsValid && (float)intVec.DistanceToSquared(pawn.Position) < 100f && intVec.GetRoom(pawn.Map) == pawn.GetRoom(RegionType.Set_All)
                    && intVec.WithinRegions(pawn.Position, pawn.Map, 9, TraverseMode.NoPassClosedDoors, RegionType.Set_Passable))
                {
                    pawn.GetLord().Notify_ReachedDutyLocation(pawn);
                    return false;
                }

                if (pathCostCache.RemoveAll(x => x.attackTarget?.Thing == null || x.attackTarget.Thing.Destroyed || x.attackTarget.ThreatDisabled(pawn)
                    || (x.blockingThing == null && (x.pawn?.Map == null || !x.pawn.Position.WithinRegions(x.cellBefore, x.pawn.Map, 9, TraverseMode.NoPassClosedDoors, RegionType.Set_Passable)))
                    || (x.blockingThing != null && (x.pawn?.Map == null || !Utilities.CellBlockedFor(pawn, x.blockingThing.Position)))) > 0)
                {
#if DEBUG
                    Log.Message($"{pawn} Cache trimmed: {string.Join(",", pathCostCache.Select(x => x.attackTarget.Thing))}");
#endif
                    findNewPaths = true;
                }

                List<(Job job, float score, string desc)> candidates = new List<(Job, float, string)>();

                IntVec3 dutyFocus = pawn.mindState.duty.focus.Cell;
                if (dutyFocus.IsValid)
                {
                    Job gotoJob = JobMaker.MakeJob(JobDefOf.Goto, dutyFocus);
                    gotoJob.expiryInterval = Rand.RangeInclusive(200, 400);
                    candidates.Add((gotoJob, 0.5f, "Fallback: move to duty focus"));
                }

                var highValueTargets = pawn.Map.mapPawns.AllPawnsSpawned
                    .Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike) 
                    .Cast<Thing>()
                    .Concat(pawn.Map.listerThings.AllThings.Where(t => t is Building b && 
                        b.Faction == Faction.OfPlayer && 
                        (b.def.defName.ToLower().Contains("storage") ||
                        b.def.defName.ToLower().Contains("shelf") ||
                        b.def.defName.ToLower().Contains("battery") ||
                        b.def.defName.ToLower().Contains("generator"))))
                    .ToList();

                int allyCount = pawn.Map.mapPawns.AllPawnsSpawned.Count(p => p.Faction == pawn.Faction && !p.Downed);

                var allies = pawn.Map.mapPawns.AllPawnsSpawned
                    .Where(p => p.Faction == pawn.Faction && p != pawn && p.Position.DistanceTo(pawn.Position) < 20)
                    .ToList();

                int[] lateralOffsets = new int[] { 0, 2, -2, 4, -4 };

                foreach (var target in highValueTargets)
                {
                    var dest = new LocalTargetInfo(target);
                    PawnPath path = null;
                    if (findPathMethod != null)
                    {
                        path = (PawnPath)findPathMethod.Invoke(pawn.Map.pathFinder, new object[] {
                            pawn.Position,
                            dest,
                            TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors, false),
                            PathEndMode.Touch
                        });
                    }

                    if (path != null && path.Found)
                    {
                        float pathTrapRisk = 0f;
                        foreach (var cell in path.NodesReversed)
                        {
                            if (HasTrapIn3x3(cell, pawn.Map))
                                pathTrapRisk += 100f;
                        }

                        if (pathTrapRisk < 50f)
                        {
                            Job gotoJob = JobMaker.MakeJob(JobDefOf.Goto, target.Position);
                            gotoJob.expiryInterval = Rand.RangeInclusive(200, 400);
                            candidates.Add((gotoJob, 200f - pathTrapRisk, $"Safe path to {target.LabelShort}"));
                            continue; 
                        }
                    }

                    foreach (int offset in lateralOffsets)
                    {
                        IntVec3 start = pawn.Position;
                        IntVec3 mid = new IntVec3(start.x + offset, 0, start.z);
                        List<IntVec3> pathCells = new List<IntVec3>();

                        if (offset == 0)
                        {
                            pathCells.AddRange(GetPathCells(pawn, start, target.Position, offset));
                        }
                        else
                        {
                            pathCells.AddRange(GetPathCells(pawn, start, mid, offset));
                            pathCells.AddRange(GetPathCells(pawn, mid, target.Position, offset));
                        }

                        bool nimble = pawn.story?.traits?.HasTrait(DefDatabase<TraitDef>.GetNamedSilentFail("Nimble")) ?? false;
                        
                        float trapRisk = 0f;
                        float defenseRisk = 0f;
                        float wallBlock = 0f;


                        foreach (var cell in pathCells)
                        {
                            if (!cell.InBounds(pawn.Map) || nimble) continue;

                            if (HasTrapIn3x3(cell, pawn.Map))
                            {
                                trapRisk += 40.0f;
                            }

                            foreach (var nearCell in GenRadial.RadialCellsAround(cell, 3f, true))
                            {
                                if (nearCell.InBounds(pawn.Map) && IsDefensiveStructureAt(nearCell, pawn.Map))
                                    defenseRisk += 1.0f;
                            }

                            foreach (var thing in cell.GetThingList(pawn.Map))
                            {
                                if (thing is Building ed && (ed.def == ThingDefOf.Wall || ed.def == ThingDefOf.Door || ed.def.mineable))
                                    wallBlock += 1.0f;
                            }

                            defenseRisk += EvaluateDefensiveStructureRisk(pawn, cell);
                        }
                                                
                        float timeCost = pawn.Position.DistanceTo(target.Position) / 30f + wallBlock * 2.5f;
                        float baseScore = 2.0f;
                        float targetScore = EvaluateTarget(pawn, target);

                        float defensePenalty = defenseRisk > 1.5f ? 40.0f : 0f; 

                        float wallThicknessPenalty = CalculateWallThicknessPenalty(pawn, pathCells);

                        float wallPenalty = wallBlock > 0 ? (wallBlock * 20.0f + wallThicknessPenalty) : 0f;

                        float trapPenalty = trapRisk; 

                        if (allyCount == 1 && wallBlock > 0)
                        {
                            baseScore += 20.0f;
                        }

                        if (trapRisk > 2.0f) 
                        {
                            baseScore = Math.Max(0.1f, baseScore - 50.0f);
                        }

                        if (pathCells.Count == 0)
                        {
                            continue;
                        }

                        if (trapRisk > 80.0f || (trapRisk > 30.0f && defenseRisk > 5.0f))
                        {
                            continue;
                        }

                        if (wallBlock == 0 && trapRisk < 1.0f && defenseRisk < 1.0f)
                            baseScore += 35.0f;

                            
                        if (wallBlock > 0 && trapRisk > 10.0f)
                        {
                            continue;
                        }

                        if (trapRisk > 100.0f || defenseRisk > 10.0f)
                        {
                            continue;
                        }

                        if (wallBlock > 0 && !IsWallOnPathToTarget(pawn, pathCells, target.Position))
                        {
                            continue;
                        }


                        float score = baseScore + targetScore - trapPenalty - defenseRisk - timeCost - defensePenalty - wallPenalty;

                        if (allyCount > 1)
                        {
                            score += 2.0f; //* allyCount; 
                            
                            if (wallBlock > 0)
                            {
                                score -= 10.0f; 
                            }
                        }


                        if (trapRisk > 3.0f)
                        {
                            score -= 75.0f;
                        }

                        if (trapRisk > 50.0f || (trapRisk > 20.0f && defenseRisk > 5.0f))
                        {
                            continue;
                        }

                        Job job = null;
                        
                        if (wallBlock > 0)
                        {
                            var optimalWall = FindOptimalWallToBreak(pawn, pathCells, allyCount);

                            if (optimalWall != null)
                            {
                                if (optimalWall.def == ThingDefOf.Door)
                                {
                                    score += 50f;
                                }

                                PawnPath pathToWall = (PawnPath)findPathMethod.Invoke(pawn.Map.pathFinder, new object[] { pawn.Position, new LocalTargetInfo(optimalWall), TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors, false), PathEndMode.Touch });
                                if (pathToWall != null && pathToWall.Found)
                                {
                                    float pathTrapRisk = 0f;
                                    foreach (var cell in pathToWall.NodesReversed)
                                    {
                                        if (HasTrapIn3x3(cell, pawn.Map))
                                            pathTrapRisk += 100f;
                                    }

                                    if (pathTrapRisk > 40f)
                                    {
                                        continue;
                                    }

                                    score += trapPenalty;
                                    score -= pathTrapRisk; 
                                    job = (optimalWall.def.mineable && !StatDefOf.MiningSpeed.Worker.IsDisabledFor(pawn) && pawn.CanReserve(optimalWall)) 
                                        ? JobMaker.MakeJob(JobDefOf.Mine, optimalWall) : JobMaker.MakeJob(JobDefOf.AttackMelee, optimalWall);
                                }
                            }
                        }

                        if (path != null && path.Found)
                        {
                            float pathTrapRisk = 0f;
                            foreach (var cell in path.NodesReversed)
                            {
                                if (HasTrapIn3x3(cell, pawn.Map))
                                    pathTrapRisk += 100f;
                            }

                            float pathLengthPenalty = path.TotalCost / 15f;
                            float targetValue = EvaluateTarget(pawn, target);
                            float gotoScore = 150f + targetValue - pathTrapRisk - pathLengthPenalty;

                            if (pawn.Position != target.Position)
                            {
                                if (pathTrapRisk < 1.0f)
                                {
                                    Job bypassJob = JobMaker.MakeJob(JobDefOf.Goto, target.Position);
                                    bypassJob.expiryInterval = Rand.RangeInclusive(200, 400);
                                    candidates.Add((bypassJob, gotoScore, $"Safe alternate path to {target.LabelShort} (cost: {path.TotalCost})"));
                                }
                                else
                                {
                                    Job trappedPathJob = JobMaker.MakeJob(JobDefOf.Goto, target.Position);
                                    trappedPathJob.expiryInterval = Rand.RangeInclusive(200, 400);
                                    candidates.Add((trappedPathJob, gotoScore, $"Trapped alternate path to {target.LabelShort} (risk: {pathTrapRisk}, cost: {path.TotalCost})"));
                                }
                            }
                        }
                        else 
                        {
                            if (wallBlock > 0)
                            {
                                var optimalWall = FindOptimalWallToBreak(pawn, pathCells, allyCount);
                                if (optimalWall != null)
                                {
                                    PawnPath pathToWall = (PawnPath)findPathMethod.Invoke(pawn.Map.pathFinder, new object[] { pawn.Position, new LocalTargetInfo(optimalWall), TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors, false), PathEndMode.Touch });
                                    if (pathToWall != null && pathToWall.Found)
                                    {
                                        float pathTrapRisk = 0f;
                                        foreach (var cell in pathToWall.NodesReversed)
                                        {
                                            if (HasTrapIn3x3(cell, pawn.Map))
                                                pathTrapRisk += 100f;
                                        }

                                        if (pathTrapRisk > 80f) 
                                        {
                                            continue;
                                        }

                                        Job sapJob = (optimalWall.def.mineable && !StatDefOf.MiningSpeed.Worker.IsDisabledFor(pawn) && pawn.CanReserve(optimalWall)) ? JobMaker.MakeJob(JobDefOf.Mine, optimalWall) : JobMaker.MakeJob(JobDefOf.AttackMelee, optimalWall);
                                        sapJob.expireRequiresEnemiesNearby = false;
                                        float distancePenalty = pathToWall.TotalCost / 6f;
                                        float wallHpPenalty = optimalWall.HitPoints / 15f;
                                        float sapScore = EvaluateTarget(pawn, target) + 15f - pathTrapRisk - distancePenalty - wallHpPenalty;
                                        candidates.Add((sapJob, sapScore, $"Sap {optimalWall.LabelShort} to reach {target.LabelShort}"));
                                    }
                                }
                            }
                        }


                        if (wallBlock > 0)
                        {

                        }
                        else if (target is Pawn col && col.Downed && col.RaceProps.Humanlike)
                        {
                            bool safeToKidnap = trapRisk < 1.0f && defenseRisk < 1.0f && wallBlock == 0;

                            if (safeToKidnap && !IsReservedForKidnap(col, pawn))
                                job = JobMaker.MakeJob(JobDefOf.Kidnap, target);
                        }
                        else if (target is Pawn col2)
                            job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                        else if (target is Building bld)
                            job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);

                        if (job != null)
                        {
                            job.expireRequiresEnemiesNearby = false;
                            job.expiryInterval = Rand.RangeInclusive(200, 400);
                            job.collideWithPawns = true;
                            candidates.Add((job, score, $"Path({offset}) to {target.LabelShort} at {target.Position} (score={score:F2})"));
                        }
                    }
                }

                foreach (var target in highValueTargets)
                {
                    if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) continue;
                    foreach (var cell in PointsOnLine(pawn.Position, target.Position))
                    {
                        var things = cell.GetThingList(pawn.Map);
                        foreach (var thing in things)
                        {
                            if (thing is Building edifice && (edifice.def == ThingDefOf.Wall || edifice.def == ThingDefOf.Door || edifice.def.mineable))
                            {
                                if (edifice.Faction != Faction.OfPlayer) continue;
                                if (!pawn.CanReach(edifice, PathEndMode.Touch, Danger.Deadly) || !pawn.CanReserve(edifice)) continue;

                                if (candidates.Any(c => c.job.targetA.Thing == edifice)) continue;

                                float trapRisk = 0f;
                                float timeCost;

                                if (findPathMethod != null)
                                {
                                    PawnPath path_to_edifice = (PawnPath)findPathMethod.Invoke(pawn.Map.pathFinder, new object[] {
                                        pawn.Position,
                                        new LocalTargetInfo(edifice),
                                        TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors, false),
                                        PathEndMode.Touch
                                    });

                                    if (path_to_edifice == null || !path_to_edifice.Found)
                                    {
                                        continue;
                                    }

                                    foreach (var pathCell in path_to_edifice.NodesReversed)
                                    {
                                        if (HasTrapIn3x3(pathCell, pawn.Map))
                                        {
                                            trapRisk += 100f;
                                        }
                                    }
                                    timeCost = (path_to_edifice.TotalCost / 35f);
                                }
                                else
                                {
                                    timeCost = pawn.Position.DistanceTo(edifice.Position) / 10f;
                                }

                                if (HasTrapIn3x3(edifice.Position, pawn.Map))
                                {
                                    trapRisk += 50f;
                                }
                                timeCost += edifice.def.mineable ? 2.0f : 1.0f;
                                float playerThreat = edifice.def.mineable ? 0.5f : 1.5f;
                                float score = playerThreat - trapRisk - timeCost;
                                Job job = null;
                                if (edifice.def.mineable && !StatDefOf.MiningSpeed.Worker.IsDisabledFor(pawn))
                                    job = JobMaker.MakeJob(JobDefOf.Mine, edifice);
                                else
                                    job = JobMaker.MakeJob(JobDefOf.AttackMelee, edifice);
                                    
                                if (job != null)
                                {
                                    job.expireRequiresEnemiesNearby = false;
                                    job.expiryInterval = 120;
                                    job.collideWithPawns = true;
                                    candidates.Add((job, score, $"Break {edifice.LabelShort} at {edifice.Position} (score={score:F2})"));
                                }
                            }
                        }
                    }
                }

                IntVec3 baseEntry = pawn.mindState.duty.focus.Cell;
                if (baseEntry.IsValid && pawn.CanReach(baseEntry, PathEndMode.OnCell, Danger.Deadly) && baseEntry != pawn.Position)
                {
                    float trapRisk = 0f;
                    float fireRisk = 0f;
                    foreach (var cell in PointsOnLine(pawn.Position, baseEntry))
                    {
                        if (HasTrapIn3x3(cell, pawn.Map)) trapRisk += 10.0f;
                        fireRisk += EvaluateCellDanger(pawn, cell);
                    }
                    float timeCost = pawn.Position.DistanceTo(baseEntry) / 30f;
                    float playerThreat = 2.0f;
                    float score = playerThreat - trapRisk - fireRisk - timeCost;
                    Job job = JobMaker.MakeJob(JobDefOf.Goto, baseEntry);
                    job.expireRequiresEnemiesNearby = false;
                    job.expiryInterval = Rand.RangeInclusive(200, 400);
                    job.collideWithPawns = true;
                    candidates.Add((job, score, $"Go through killzone to {baseEntry} (score={score:F2})"));
                }

                string myRole = GetPawnRole(pawn);
                if (myRole == "tank")
                {
                    var coverCell = GenRadial.RadialCellsAround(pawn.Position, 5f, true)
                        .Where(c => c.InBounds(pawn.Map) && !HasTrapIn3x3(c, pawn.Map) && c.Standable(pawn.Map))
                        .OrderBy(c => c.DistanceTo(pawn.Position))
                        .FirstOrDefault();
                    if (coverCell.IsValid && coverCell != pawn.Position)
                    {
                        Job tankJob = JobMaker.MakeJob(JobDefOf.Goto, coverCell);
                        tankJob.expiryInterval = Rand.RangeInclusive(200, 400);
                        tankJob.collideWithPawns = true;
                        
                        candidates.Add((tankJob, 2.5f, $"Tank advance to cover at {coverCell}"));
                    }
                }
                else if (myRole == "ranged")
                {
                    var tanks = pawn.Map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == pawn.Faction && GetPawnRole(p) == "tank");
                    IntVec3 fallback = pawn.Position;
                    foreach (var tank in tanks)
                    {
                        int dx = Math.Sign(pawn.Position.x - tank.Position.x);
                        int dz = Math.Sign(pawn.Position.z - tank.Position.z);
                        var dir = new IntVec3(dx, 0, dz);
                        var behindTank = tank.Position - dir;
                        if (behindTank.InBounds(pawn.Map) && behindTank.Standable(pawn.Map) && !HasTrapIn3x3(behindTank, pawn.Map))
                        {
                            fallback = behindTank;
                            break;
                        }
                    }
                    if (fallback != pawn.Position)
                    {
                        Job fallbackJob = JobMaker.MakeJob(JobDefOf.Goto, fallback);
                        fallbackJob.expiryInterval = Rand.RangeInclusive(200, 400);
                        fallbackJob.collideWithPawns = true;
                        candidates.Add((fallbackJob, 2.0f, $"Ranged fallback behind tank at {fallback}"));
                    }
                }

                var sapperGroup = pawn.Map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == pawn.Faction && p != pawn && p.Position.DistanceTo(pawn.Position) < 15).ToList();
                if (sapperGroup.Count >= 2)
                {
                    var targetColonist = pawn.Map.mapPawns.AllPawnsSpawned
                        .Where(p => p.Faction == Faction.OfPlayer && !p.Downed && pawn.CanReach(p, PathEndMode.Touch, Danger.Deadly))
                        .OrderBy(p => sapperGroup.Average(s => s.Position.DistanceTo(p.Position)))
                        .FirstOrDefault();
                    if (targetColonist != null)
                    {
                        Job groupAssault = JobMaker.MakeJob(JobDefOf.AttackMelee, targetColonist);
                        groupAssault.expiryInterval = Rand.RangeInclusive(200, 400);
                        groupAssault.collideWithPawns = true;
                        candidates.Add((groupAssault, 4.0f, $"Group assault on {targetColonist.LabelShort} at {targetColonist.Position}"));
                    }
                }

                if (pawn.health?.summaryHealth?.SummaryHealthPercent < 0.5f)
                {
                    var lureCell = GenRadial.RadialCellsAround(pawn.Position, 8f, true)
                        .Where(c => c.InBounds(pawn.Map) && IsTrapAt(c, pawn.Map)).OrderBy(c => c.DistanceTo(pawn.Position)).FirstOrDefault();
                    if (lureCell.IsValid && lureCell != pawn.Position) 
                    {
                        Job lureJob = JobMaker.MakeJob(JobDefOf.Goto, lureCell);
                        lureJob.expiryInterval = Rand.RangeInclusive(200, 400);
                        lureJob.collideWithPawns = true;
                        candidates.Add((lureJob, 1.0f, $"Lure player to trap at {lureCell}"));
                    }
                }

                float traitWeight = PawnTraitWeight(pawn);
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    candidates[i] = (c.job, c.score * traitWeight, c.desc + $" (trait x{traitWeight:F2})");
                }

                var validCandidates = candidates.Where(x => x.score > -50.0f).ToList();



                if (validCandidates.Count > 0)
                {
                    var best = validCandidates.OrderByDescending(x => x.score).First();
                    __result = best.job;
#if DEBUG
                    Log.Message($"[AI] {pawn} chooses: {best.desc} (score: {best.score})");
#endif
                }
                else
                {
                    IntVec3 wanderDest = CellFinder.RandomClosewalkCellNear(pawn.Position, pawn.Map, 8, c => !HasTrapIn3x3(c, pawn.Map));

                    if (wanderDest.IsValid && wanderDest != pawn.Position)
                    {
                        __result = JobMaker.MakeJob(JobDefOf.Goto, wanderDest);
                        __result.expiryInterval = Rand.RangeInclusive(200, 400);
                        __result.locomotionUrgency = LocomotionUrgency.Amble;
#if DEBUG
                        Log.Message($"[AI] {pawn} has no good options, wandering to {wanderDest}");
#endif
                    }

                    var memoryValue = pathCostCache.OrderBy(x => pawn.Position.DistanceTo(x.cellBefore)).FirstOrDefault(x =>
                        x.blockingThing == null || pawn.HasReserved(x.blockingThing) || pawn.CanReserve(x.blockingThing));
                    IAttackTarget attackTarget = null;
                    var cacheIndex = -1;

                    if (memoryValue != null)
                    {
                        intVec = memoryValue.attackTarget.Thing.Position;
                        attackTarget = memoryValue.attackTarget;
                        cacheIndex = pathCostCache.IndexOf(memoryValue);
                    }

                    if (memoryValue == null)
                    {
                        attackTarget = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn)
                            .Where(x => !x.ThreatDisabled(pawn) && !x.Thing.Destroyed && x.Thing.Faction == Faction.OfPlayer && !pathCostCache.Any(y => y.pawn == x.Thing))
                            .OrderBy(x => ((Thing)x).Position.DistanceToSquared(pawn.Position)).FirstOrDefault();

                        if (attackTarget == null)
                        {
                            findNewPaths = false;
                        }
                        else
                        {
                            intVec = attackTarget.Thing.Position;
#if DEBUG
                            Find.CurrentMap.debugDrawer.FlashCell(attackTarget.Thing.Position, 0.8f, $"{attackTarget.Thing}", 60);
#endif
                        }
                    }

                    if (findNewPaths && memoryValue == null && pathCostCache.Count <= Init.settings.maxSappers)
                    {

                    }
                    else if (memoryValue == null)
                    {
                        if (attackTarget != null)
                        {
                            var findTarget = pathCostCache.FirstOrDefault(x => x.attackTarget == attackTarget && x.blockingThing == null);
                            if (findTarget != null)
                            {
                                memoryValue = findTarget;
                            }
                        }
                        if (memoryValue == null)
                        {
                            var sappingPawn = pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction).Where(x => x.CurJobDef == JobDefOf.Mine || x.CurJobDef == JobDefOf.AttackMelee)
                                .OrderBy(x => x.Position.DistanceTo(pawn.Position)).FirstOrDefault();
                            if (sappingPawn != null && pawn.Position.DistanceTo(sappingPawn.Position) >= 10)
                            {
                                __result = JobMaker.MakeJob(JobDefOf.Follow, sappingPawn);
                            }
                        }
                    }

                    if (memoryValue != null)
                    {
                        __result = GetSapJob(pawn, memoryValue);
#if DEBUG
                        if (__result != null)
                        {
                            Find.CurrentMap.debugDrawer.FlashCell(pawn.Position, 0.8f, $"{cacheIndex}" +
                                $"\n{__result.def.defName.Substring(0, 3)}\n{__result.targetA.Cell.x},{__result.targetA.Cell.z}", 60);
                        }
#endif
                    }

                    if (__result != null)
                    {
                        __result.collideWithPawns = true;
                        __result.expiryInterval = Rand.RangeInclusive(200, 400);
                        __result.ignoreDesignations = true;
                        __result.checkOverrideOnExpire = true;
                        __result.expireRequiresEnemiesNearby = false;
                    }
                }

                if (__result != null && pawn.CurJob != null)
                {
                    if (pawn.CurJob.def == __result.def &&
                        pawn.CurJob.targetA == __result.targetA &&
                        pawn.CurJob.targetB == __result.targetB)
                    {
                        __result = null;
                    }
                }

                if (__result == null)
                {
                    IntVec3 wanderDest = CellFinder.RandomClosewalkCellNear(pawn.Position, pawn.Map, 8, c => !HasTrapIn3x3(c, pawn.Map));
                    if (wanderDest.IsValid && wanderDest != pawn.Position)
                    {
                        __result = JobMaker.MakeJob(JobDefOf.Goto, wanderDest);
                        __result.expiryInterval = Rand.RangeInclusive(200, 400);
                        __result.locomotionUrgency = LocomotionUrgency.Amble;
#if DEBUG
                        Log.Message($"[AI] {pawn} has no specific job, wandering to {wanderDest}");
#endif
                    }
                }

                return false;
            }

            private static bool IsDefensiveStructureAt(IntVec3 cell, Map map)
            {
                var things = cell.GetThingList(map);
                foreach (var thing in things)
                {
                    if (thing is Building_Trap)
                        return true;

                    if (thing is Building_Turret)
                        return true;
                }
                return false;
            }
            
            private static float CalculateWallThicknessPenalty(Pawn pawn, List<IntVec3> pathCells)
            {
                float penalty = 0f;
                var map = pawn.Map;
                
                foreach (var cell in pathCells)
                {
                    if (!cell.InBounds(map)) continue;
                    
                    var building = cell.GetFirstBuilding(map);
                    if (building != null && (building.def == ThingDefOf.Wall || building.def.mineable))
                    {
                        float hpRatio = building.HitPoints / (float)building.MaxHitPoints;
                        penalty += hpRatio * 0.01f;
                        
                        int adjacentWalls = GenAdj.CellsAdjacent8Way(building)
                            .Count(c => c.InBounds(map) && c.GetFirstBuilding(map) is Building adjBuilding && 
                                    (adjBuilding.def == ThingDefOf.Wall || adjBuilding.def.mineable));
                        penalty += adjacentWalls * 4.0f; 
                    }
                }
                
                return penalty;
            }
                        
            private static Thing FindOptimalWallToBreak(Pawn pawn, List<IntVec3> pathCells, int allyCount)
            {
                var map = pawn.Map;
                var candidateWalls = new List<(Thing wall, float score)>();

                Thing currentTargetWall = null;
                if (pawn.CurJob != null && (pawn.CurJob.def == JobDefOf.AttackMelee || pawn.CurJob.def == JobDefOf.Mine))
                {
                    var target = pawn.CurJob.targetA.Thing;
                    if (target != null && (target.def == ThingDefOf.Wall || target.def.mineable || target.def == ThingDefOf.Door))
                    {
                        currentTargetWall = target;
                    }
                }

                for (int i = 0; i < pathCells.Count; i++)
                {
                    var cell = pathCells[i];
                    if (!cell.InBounds(map)) continue;

                    var wall = cell.GetFirstBuilding(map);
                    if (wall != null && (wall.Faction == Faction.OfPlayer || wall.Faction == null) && (wall.def == ThingDefOf.Wall || wall.def.mineable || wall.def == ThingDefOf.Door) && pawn.CanReach(wall, PathEndMode.Touch, Danger.Deadly))
                    {
                        if (candidateWalls.Any(c => c.wall == wall)) continue;

                        float score = 0f;

                        if (wall.def == ThingDefOf.Door)
                        {
                            score += 100f;
                        }

                        score += (1f - (wall.HitPoints / (float)wall.MaxHitPoints)) * 50f;

                        if (HasTrapIn3x3(wall.Position, map))
                        {
                            score -= 300f;
                        }

                        int nextCellIndex = i + 1;
                        if (nextCellIndex < pathCells.Count)
                        {
                            IntVec3 cellBehind = pathCells[nextCellIndex];
                            if (cellBehind.InBounds(map) && HasTrapIn3x3(cellBehind, map))
                            {
                                score -= 500f;
                            }
                        }

                        var closestColonist = map.mapPawns.FreeColonists.OrderBy(c => c.Position.DistanceTo(cell)).FirstOrDefault();
                        if (closestColonist != null)
                        {
                            score += 10f / (closestColonist.Position.DistanceTo(cell) + 1f);
                        }

                        if (IsReservedByOtherSapper(wall, pawn))
                        {
                            score += 20f;
                        }

                        if (wall == currentTargetWall)
                        {
                            score += 20f;
                        }

                        candidateWalls.Add((wall, score));
                    }
                }

                if (!candidateWalls.Any()) return null;
                return candidateWalls.OrderByDescending(x => x.score).First().wall;
            }
            private static bool IsReservedByOtherSapper(Thing thing, Pawn excludingPawn)
            {
                foreach (var pawn in thing.Map.mapPawns.AllPawnsSpawned)
                {                    
                    if (pawn != excludingPawn && pawn.Faction == excludingPawn.Faction
                        && pawn.CurJob != null && pawn.CurJob.targetA.Thing == thing 
                        && (pawn.CurJob.def == JobDefOf.Mine || pawn.CurJob.def == JobDefOf.AttackMelee)
                        && thing.Map.reservationManager.ReservedBy(thing, pawn))
                    {
                        return true;
                    }
                }
                return false;
            }

            private static bool IsPathValid(Pawn pawn, List<IntVec3> pathCells)
            {
                if (pathCells.Count == 0) return false;
                
                if (!pathCells[0].Walkable(pawn.Map) || !pathCells[pathCells.Count - 1].Walkable(pawn.Map))
                    return false;
                
                foreach (var cell in pathCells)
                {
                    if (!cell.Walkable(pawn.Map) && !cell.GetThingList(pawn.Map).Any(t => t.def.mineable))
                        return false;
                }
                
                return true;
            }

            private static float EvaluateDefensiveStructureRisk(Pawn pawn, IntVec3 cell)
            {
                float risk = 0f;
                foreach (var thing in cell.GetThingList(pawn.Map))
                {
                    if (thing is Building_Turret turret && turret.Faction == Faction.OfPlayer)
                    {
                        float range = 0f;

                        if (turret.def.building?.turretGunDef != null)
                        {
                            var gunDef = turret.def.building.turretGunDef;
                            range = gunDef.Verbs?.FirstOrDefault()?.range ?? 0f;
                        }

                        if (range > 0f && turret.Position.DistanceTo(cell) <= range)
                            risk += 4.0f;
                    }

                    if (thing is Building_Trap)
                        risk += 3.0f;

                    if (thing.def.defName.ToLower().Contains("mine") || thing.def.defName.ToLower().Contains("drone"))
                        risk += 3.5f;
                }

                return risk;
            }

            private static Job GetSapJob(Pawn pawn, CachedPath memoryValue)
            {
                if (memoryValue.blockingThing != null &&
                    (memoryValue.blockingThing.Destroyed || !memoryValue.blockingThing.Spawned))
                {
                    return null;
                }

                var blockingThing = memoryValue.blockingThing ?? memoryValue.attackTarget.Thing;

                if (blockingThing.Destroyed || !blockingThing.Spawned)
                    return null;

                if (HasTrapIn3x3(blockingThing.Position, pawn.Map))
                {
                    IntVec3 fleeDest = CellFinder.RandomClosewalkCellNear(pawn.Position, pawn.Map, 10);
                    if (fleeDest == pawn.Position || !fleeDest.IsValid)
                    {
                        return null;
                    }
                    return JobMaker.MakeJob(JobDefOf.Goto, fleeDest);
                }

                if (blockingThing.def.mineable && !StatDefOf.MiningSpeed.Worker.IsDisabledFor(pawn))
                {
                    return JobMaker.MakeJob(JobDefOf.Mine, blockingThing);
                }
                else
                {
                    return JobMaker.MakeJob(JobDefOf.AttackMelee, blockingThing);
                }
            }

            private static bool IsTrapAt(IntVec3 cell, Map map)
            {
                var things = cell.GetThingList(map);
                foreach (var thing in things)
                {
                    if (thing is Building_Trap) return true;
                    var name = thing.def.defName.ToLower();
                    if (name.Contains("mine") || name.Contains("trap")) return true;
                }
                return false;
            }

            private static bool HasTrapIn3x3(IntVec3 center, Map map)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var c = new IntVec3(center.x + dx, 0, center.z + dz);
                        if (!c.InBounds(map)) continue;
                        if (IsTrapAt(c, map)) return true;
                    }
                }
                return false;
            }

            private static List<IntVec3> GetPathCells(Pawn pawn, IntVec3 start, IntVec3 end, int offset)
            {
                var path = new List<IntVec3>();

                if (offset == 0)
                {
                    path.AddRange(PointsOnLine(start, end));
                } else
                {
                    IntVec3 mid = new IntVec3(start.x + offset, 0, start.z);
                    path.AddRange(PointsOnLine(start, mid));
                    path.AddRange(PointsOnLine(mid, end));
                }
                return path;
            }

            private static bool HasDefensiveStructureInArea(IntVec3 center, Map map, float radius = 2.9f)
            {
                return GenRadial.RadialCellsAround(center, radius, true)
                    .Any(cell => cell.InBounds(map) && IsDefensiveStructureAt(cell, map));
            }

            private static IEnumerable<IntVec3> PointsOnLine(IntVec3 start, IntVec3 end)
            {
                int x0 = start.x, y0 = start.z;
                int x1 = end.x, y1 = end.z;
                int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
                int sx = x0 < x1 ? 1 : -1;
                int sy = y0 < y1 ? 1 : -1;
                int err = dx - dy;
                while (true)
                {
                    yield return new IntVec3(x0, 0, y0);
                    if (x0 == x1 && y0 == y1) break;
                    int e2 = 2 * err;
                    if (e2 > -dy) { err -= dy; x0 += sx; }
                    if (e2 < dx) { err += dx; y0 += sy; }
                }
            }

            private static string GetPawnRole(Pawn pawn)
            {
                if (pawn.equipment?.Primary?.def.IsRangedWeapon ?? false)
                    return "ranged";
                if (pawn.apparel?.WornApparel?.Any(a => a.GetStatValue(StatDefOf.ArmorRating_Sharp) > 0.3f) ?? false)
                    return "tank";
                return "melee";
            }

            private static bool IsWallOnPathToTarget(Pawn pawn, List<IntVec3> pathCells, IntVec3 targetPos)
            {
                foreach (var cell in pathCells)
                {
                    if (!cell.InBounds(pawn.Map)) continue;
                    
                    if (!GenSight.LineOfSight(cell, targetPos, pawn.Map, true))
                    {
                        return true;
                    }
                }
                return false;
            }
            
            private static bool IsReservedForKidnap(Pawn target, Pawn excludingPawn)
            {
                foreach (var pawn in target.Map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn != excludingPawn && pawn.CurJobDef == JobDefOf.Kidnap &&
                        pawn.CurJob.targetA.Thing == target)
                    {
                        return true;
                    }
                }
                return false;
            }
            
            private static float EvaluateTarget(Pawn pawn, Thing target)
            {
                float score = 0f;
                if (target is Pawn colonist && colonist.Faction == Faction.OfPlayer)
                {
                    if (colonist.Downed) score += 5f;

                    score += 2f;
                }
                if (target is Building building)
                {
                    if (building.def.defName.ToLower().Contains("storage") || building.def.defName.ToLower().Contains("shelf")) score += 2f;
                    if (building.def.defName.ToLower().Contains("battery") || building.def.defName.ToLower().Contains("generator")) score += 1.5f;
                }
                return score;
            }

            private static float EvaluateCellDanger(Pawn pawn, IntVec3 cell)
            {
                float danger = 0f;
                foreach (var enemy in pawn.Map.mapPawns.AllPawnsSpawned)
                {
                    if (enemy.Faction == Faction.OfPlayer && !enemy.Downed && enemy.equipment?.Primary != null)
                    {
                        float range = enemy.equipment.Primary.def.Verbs?.FirstOrDefault()?.range ?? 0f;
                        if (range > 0f && enemy.Position.DistanceTo(cell) <= range)
                        {
                            if (GenSight.LineOfSight(enemy.Position, cell, pawn.Map))
                            {
                                float dph = enemy.equipment.Primary.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS, true);
                                danger += 1.5f + dph * 0.5f;
                            }
                        }
                    }
                }

                if (cell.GetCover(pawn.Map) != null)
                    danger *= 0.5f;
                return danger;
            }

            private static float PawnTraitWeight(Pawn pawn)
            {
                float weight = 1f;
                weight += (pawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0) * 0.1f;
                weight += (pawn.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0) * 0.1f;

                weight *= pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;

                if (pawn.story?.traits?.HasTrait(TraitDefOf.Wimp) ?? false) weight *= 0.7f;
                var toughDef = DefDatabase<TraitDef>.GetNamed("Tough", false);

                return weight;
            }
            
            private static bool IsWallAlreadyBreached(IntVec3 cell, Map map)
            {
                if (!cell.Walkable(map)) return false;

                return true;
            }

            private static List<IntVec3> GetFlankingPositions(IntVec3 targetPos, IntVec3 currentPos, int allyCount)
            {
                var positions = new List<IntVec3>();
                int flankers = Math.Min(allyCount, 4);

                IntVec3 toCurrent = currentPos - targetPos;
                if (toCurrent.x == 0 && toCurrent.z == 0)
                {
                    toCurrent = new IntVec3(1, 0, 0);
                }

                IntVec3 primaryDirection;
                if (Math.Abs(toCurrent.x) > Math.Abs(toCurrent.z))
                {
                    primaryDirection = new IntVec3(Math.Sign(toCurrent.x), 0, 0);
                }
                else
                {
                    primaryDirection = new IntVec3(0, 0, Math.Sign(toCurrent.z));
                }

                IntVec3 perpendicular1 = new IntVec3(primaryDirection.z, 0, primaryDirection.x);
                IntVec3 perpendicular2 = new IntVec3(-primaryDirection.z, 0, -primaryDirection.x);

                positions.Add(targetPos + primaryDirection * 5);
                positions.Add(targetPos - primaryDirection * 5);
                positions.Add(targetPos + perpendicular1 * 5);
                positions.Add(targetPos + perpendicular2 * 5);

                return positions.Take(flankers).ToList();
            }
        }
    }
}