using MarkSubsystem.Data;

namespace MarkSubsystem.Models
{
    public class SolutionsByProgram
    {
        public int SessionId { get; set; } // FK to Sessions
        public int TestId { get; set; } // Идентификатор теста
        public int ProgramStep { get; set; } // Номер шага программы
        public int ProgramLineNumber { get; set; } // Номер строки для шага
        public int OrderNumber { get; set; } // Порядковый номер
        public float StepDifficult { get; set; } = 0.5f; // Сложность шага (default 0.5)

        // Навигационное свойство
        public Session Session { get; set; }
    }
}
