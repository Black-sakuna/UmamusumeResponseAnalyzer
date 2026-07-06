using System.Collections.Generic;

namespace UmamusumeResponseAnalyzer.Entities
{
    public class SuccessionRelationTable
    {
        public Dictionary<int, int> PointDictionary { get; set; } = [];
        public Dictionary<int, List<int>> MemberDictionary { get; set; } = [];
    }
}
