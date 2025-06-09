namespace MarkSubsystem.DTO
{
    public class IncompleteSolutionResponseDto
    {
        public string Message { get; set; } = string.Empty;
        public List<int>? ExtraSteps { get; set; }
        public List<int>? MissingSteps { get; set; }
        public List<MismatchResponseDto>? Mismatches { get; set; }
    }
}
