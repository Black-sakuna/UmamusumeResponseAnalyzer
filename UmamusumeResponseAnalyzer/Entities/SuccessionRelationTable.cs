using System.Collections.Frozen;
using Newtonsoft.Json;

namespace UmamusumeResponseAnalyzer.Entities
{
    public sealed class SuccessionRelationTable
    {
        [JsonConstructor]
        public SuccessionRelationTable(
            IReadOnlyDictionary<int, int>? pointDictionary = null,
            IReadOnlyDictionary<int, int[]>? memberDictionary = null)
        {
            PointDictionary = (pointDictionary ?? new Dictionary<int, int>()).ToFrozenDictionary();
            MemberDictionary = (memberDictionary ?? new Dictionary<int, int[]>())
                .ToFrozenDictionary(x => x.Key, x => x.Value.ToArray());
        }

        public FrozenDictionary<int, int> PointDictionary { get; }
        public FrozenDictionary<int, int[]> MemberDictionary { get; }
    }
}
