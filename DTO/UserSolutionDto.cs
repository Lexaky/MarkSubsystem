namespace MarkSubsystem.DTO
{
    public record UserSolutionDto
    {
        public List<UserStepDto> Steps { get; init; }
    }
}
