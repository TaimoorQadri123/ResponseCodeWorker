namespace FMSRaastService.Models
{
    public class ParameterValue
    {
        public long ParameterValueId { get; set; }
        public string DisplayName { get; set; }
        public string Value { get; set; }
        public long? ParameterId { get; set; }
        public string ParameterType { get; set; }
        public Parameter Parameter { get; set; }
    }
}