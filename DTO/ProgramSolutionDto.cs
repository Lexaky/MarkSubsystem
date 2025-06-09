namespace MarkSubsystem.DTO
{
    public record ProgramSolutionDto
    {
        public List<ProgramStepDto> Steps { get; init; }
    }
}
