using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarkSubsystem.Migrations.Users
{
    /// <inheritdoc />
    public partial class SecondFixVarSolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_VariablesSolutionsByUsers",
                table: "VariablesSolutionsByUsers");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VariablesSolutionsByUsers",
                table: "VariablesSolutionsByUsers",
                columns: new[] { "UserStep", "UserLineNumber", "OrderNumber", "TestId", "VarName", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_VariablesSolutionsByUsers",
                table: "VariablesSolutionsByUsers");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VariablesSolutionsByUsers",
                table: "VariablesSolutionsByUsers",
                columns: new[] { "UserStep", "UserLineNumber", "OrderNumber", "TestId", "VarName" });
        }
    }
}
