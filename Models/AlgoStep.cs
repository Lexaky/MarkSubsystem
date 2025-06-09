namespace MarkSubsystem.Models
{
    public class AlgoStep
    {
        public int AlgoId { get; set; }
        public int Step { get; set; }
        public float Difficult { get; set; } = 0.5f;
        public string Description { get; set; } = string.Empty;
    }
}
