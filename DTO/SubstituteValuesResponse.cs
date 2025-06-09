using MarkSubsystem.Models;

namespace MarkSubsystem.DTO
{
    public class SubstituteValuesResponse
    {
        public CodeModel CodeModel { get; set; }
        public List<VariableValue> Values { get; set; } = new();
        public List<Mismatch> Mismatches { get; set; } = new();
        public MetaData Meta { get; set; }
    }
}
