namespace MarkSubsystem.Models
{
    public class VariablesSolutionsByUsers
    {
        public int UserStep { get; set; } // Номер шага пользователя
        public int UserLineNumber { get; set; } // Номер строки
        public int OrderNumber { get; set; } // Порядковый номер
        public int TestId { get; set; } // Идентификатор теста
        public string VarName { get; set; } // Имя переменной
        public string VarValue { get; set; } // Значение переменной

        // Составной первичный ключ будет настроен в DbContext
    }
}
