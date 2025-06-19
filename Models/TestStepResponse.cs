namespace MarkSubsystem.Models
{
    public class TestStepResponse
    {
        public int TestId { get; set; }
        public int Algoid { get; set; }
        public int AlgoStep { get; set; }
        public int CorrectCount { get; set; }
        public int IncorrectCount { get; set; }
    }
}
