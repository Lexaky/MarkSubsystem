using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarkSubsystem.Migrations.Users
{
    /// <inheritdoc />
    public partial class FixedVarSolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "VariablesSolutionsByUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "VariablesSolutionsByUsers");
        }
    }
}
