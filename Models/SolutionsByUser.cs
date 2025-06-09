using MarkSubsystem.Data;

namespace MarkSubsystem.Models
{
    public class SolutionsByUser
    {
        public int SessionId { get; set; } // FK to Sessions
        public int UserId { get; set; } // FK to Users
        public int UserStep { get; set; } // Номер шага пользователя
        public int UserLineNumber { get; set; } // Номер строки для шага
        public int OrderNumber { get; set; } // Порядковый номер
        public int TestId { get; set; } // Идентификатор теста
        public float StepDifficult { get; set; } = 0.5f; // Сложность шага (default 0.5)

        // Навигационные свойства
        public Session Session { get; set; }
        public User User { get; set; }
    }
}
