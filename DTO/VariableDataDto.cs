namespace MarkSubsystem.DTO
{
    public class VariableDataDto
    {
        public int Sequence { get; set; }
        public int Step { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public string VariableValue { get; set; } = string.Empty;
    }
}
