using System;

namespace FMSRaastService.Models
{
    public class ModifyPoints
    {
        public string ModifyType { get; set; }
        public DateTime ModifiedOn { get; set; }
        public string TrxLogId2 { get; set; }
        public bool IsSeeded { get; set; }
    }
}