namespace MarkSubsystem.Models
{
    public class VariableValue
    {
        public int Step { get; set; }
        public int TrackerHitId { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Rank { get; set; }
        public string Value { get; set; } = string.Empty;
    }
}
