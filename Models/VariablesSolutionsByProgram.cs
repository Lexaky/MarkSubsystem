namespace MarkSubsystem.Models
{
    public class VariablesSolutionsByProgram
    {
        public int ProgramStep { get; set; } // Номер шага программы
        public int ProgramLineNumber { get; set; } // Номер строки
        public int OrderNumber { get; set; } // Порядковый номер
        public int TestId { get; set; } // Идентификатор теста
        public string VarName { get; set; } // Имя переменной
        public string VarValue { get; set; } // Значение переменной

        // Составной первичный ключ будет настроен в DbContext
    }
}
