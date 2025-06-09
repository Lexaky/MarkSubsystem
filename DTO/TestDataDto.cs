namespace MarkSubsystem.DTO
{
    public class TestDataDto
    {
        public int TestId { get; set; }
        public List<VariableDataDto> Variables { get; set; } = new();
    }
}
