using System.Collections.Generic;

namespace FMSRaastService.Models
{
    public class RuleViewModel
    {
        public long RuleId { get; set; }
        public string RuleName { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public int Days { get; set; }
        public int Hours { get; set; }
        public int Minutes { get; set; }
        public int Frequency { get; set; }
        public bool IsLookBack { get; set; }
        public bool IsCumulative { get; set; }
        public string RuleType { get; set; }
        public bool IsDeleted { get; set; }
        public int OrderOfExecution { get; set; }
        public bool AutoClose { get; set; }
        public bool Bal_Inq { get; set; }
        public bool Diff_CURR { get; set; }
        public List<ParameterCriteriasViewModel> ParameterCriteris { get; set; }
    }
}