
using RimWorld;
using Verse;
using Verse.AI;
using System.Collections.Generic;

namespace RunayAI.Patches
{
    public class JobDriver_Steal : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);

            Toil getNextItem = new Toil
            {
                initAction = () =>
                {
                    if (job.targetQueueA.NullOrEmpty())
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.Succeeded);
                        return;
                    }
                    job.targetA = job.targetQueueA[0];
                    job.targetQueueA.RemoveAt(0);
                    pawn.Reserve(job.targetA, job, 1, -1, null);
                }
            };

            Toil goToItem = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);

            Toil carryItem = Toils_Haul.StartCarryThing(TargetIndex.A, false, true, false);

            Toil jumpToNext = Toils_Jump.Jump(getNextItem);

            yield return Toils_Jump.Jump(getNextItem);

            yield return goToItem;

            yield return new Toil
            {
                initAction = () =>
                {
                    Thing thing = job.GetTarget(TargetIndex.A).Thing;
                    if (thing.def.Minifiable)
                    {
                        // This is a placeholder. A full implementation would require a dynamic
                        // insertion of the uninstall toil, which is complex.
                        // For now, we will just wait.
                        // A future improvement would be to create a custom toil for this.
                    }
                }
            };

            yield return carryItem;

            yield return jumpToNext;

            Toil gotoExit = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return gotoExit;

            yield return new Toil
            {
                initAction = ()
                => {
                    if (pawn.carryTracker.CarriedThing != null)
                    {
                        pawn.carryTracker.innerContainer.ClearAndDestroyContents();
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
        }
    }
}
