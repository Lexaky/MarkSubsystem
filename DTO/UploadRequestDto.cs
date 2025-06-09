namespace MarkSubsystem.DTO
{
    public class UploadRequestDto
    {
        public int UserId { get; set; }
        public int SessionId { get; set; }
        public int Type { get; set; }
        public List<TestDataDto> Tests { get; set; } = new();
    }

}
