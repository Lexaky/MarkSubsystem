namespace MarkSubsystem.Models
{
    public class Test
    {
        public int AlgoId { get; set; }
        public int TestId { get; set; }
        public string Description { get; set; } = string.Empty;
        public string TestName { get; set; } = string.Empty;
        public float difficult { get; set; } = 0.5f;
        public int SolvedCount { get; set; } = 0;
        public int UnsolvedCount { get; set; } = 0;
    }
}
