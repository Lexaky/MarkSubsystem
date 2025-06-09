namespace MarkSubsystem.Models
{
    public class Mismatch
    {
        public int Step { get; set; }
        public int LineNumber { get; set; }
        public int TrackerNumber { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public string? ExpectedValue { get; set; }
        public string ActualValue { get; set; } = string.Empty;
    }
}
