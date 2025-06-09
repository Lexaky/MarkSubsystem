namespace MarkSubsystem.DTO
{
    public record StatsResponseDto
    {
        public UserSolutionDto UserSolution { get; init; }
        public ProgramSolutionDto ProgramSolution { get; init; }
    }
}
