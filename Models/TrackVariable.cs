namespace MarkSubsystem.Models
{
    public class TrackVariable
    {
        public int AlgoId { get; set; }
        public int LineNumber { get; set; }
        public string VarType { get; set; } = string.Empty;
        public string VarName { get; set; } = string.Empty;
        public int Step { get; set; }
        public int Sequence { get; set; }
    }
}
