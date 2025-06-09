namespace MarkSubsystem.Models
{
    public class CodeModel
    {
        public int CodeId { get; set; }
        public string Path { get; set; } = string.Empty;
        public string StandardOutput { get; set; } = string.Empty;
        public string? ErrorOutput { get; set; }
        public string? WarningOutput { get; set; }
        public string OutputFilePath { get; set; } = string.Empty;
        public string ErrorFilePath { get; set; } = string.Empty;
        public string WarningFilePath { get; set; } = string.Empty;
        public bool IsSuccessful { get; set; }
    }
}
