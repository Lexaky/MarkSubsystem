namespace MarkSubsystem.DTO
{
    public record UserStepDto
    {
        public int Step { get; init; }
        public int LineNumber { get; init; }
        public int OrderNumber { get; init; }
        public float StepDifficult { get; init; }
        public List<VariableDto> Variables { get; init; }
    }
}
