namespace FMSRaastService.Models
{
    public class ParameterCriteriasViewModel
    {
        public long ParameterId { get; set; }
        public string ParameterName { get; set; }
        public string FieldType { get; set; }
        public long CriteriaId { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
        public long RuleId { get; set; }
        public string CriteriaType { get; set; }
    }
}