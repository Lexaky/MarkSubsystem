namespace MarkSubsystem.DTO
{
    public class MismatchResponseDto
    {
        public int TestId { get; set; }
        public int Sequence { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
