using System.Runtime.CompilerServices;
using Verse.AI.Group;

namespace RunayAI.Extensions
{
    public class CustomBreachData
    {
        public bool breachMineables = false;
        public bool enforceMinimumRange = true;
        public bool doneReset = false;

        public void Reset()
        {
            breachMineables = false;
            enforceMinimumRange = true;
            doneReset = false;
        }
    }

    public static class LordDataExtensions
    {
        private static readonly ConditionalWeakTable<Lord, CustomBreachData> lordData = new ConditionalWeakTable<Lord, CustomBreachData>();

        public static CustomBreachData GetCustomBreachData(this Lord lord) => lordData.GetOrCreateValue(lord);
    }
}