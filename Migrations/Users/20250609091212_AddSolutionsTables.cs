using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarkSubsystem.Migrations.Users
{
    /// <inheritdoc />
    public partial class AddSolutionsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolutionsByPrograms",
                columns: table => new
                {
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    TestId = table.Column<int>(type: "integer", nullable: false),
                    ProgramStep = table.Column<int>(type: "integer", nullable: false),
                    ProgramLineNumber = table.Column<int>(type: "integer", nullable: false),
                    OrderNumber = table.Column<int>(type: "integer", nullable: false),
                    StepDifficult = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionsByPrograms", x => new { x.SessionId, x.TestId, x.ProgramStep, x.ProgramLineNumber, x.OrderNumber });
                    table.ForeignKey(
                        name: "FK_SolutionsByPrograms_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SolutionsByUsers",
                columns: table => new
                {
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    UserStep = table.Column<int>(type: "integer", nullable: false),
                    UserLineNumber = table.Column<int>(type: "integer", nullable: false),
                    OrderNumber = table.Column<int>(type: "integer", nullable: false),
                    TestId = table.Column<int>(type: "integer", nullable: false),
                    StepDifficult = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionsByUsers", x => new { x.SessionId, x.UserId, x.UserStep, x.UserLineNumber, x.OrderNumber, x.TestId });
                    table.ForeignKey(
                        name: "FK_SolutionsByUsers_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SolutionsByUsers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VariablesSolutionsByPrograms",
                columns: table => new
                {
                    ProgramStep = table.Column<int>(type: "integer", nullable: false),
                    ProgramLineNumber = table.Column<int>(type: "integer", nullable: false),
                    OrderNumber = table.Column<int>(type: "integer", nullable: false),
                    TestId = table.Column<int>(type: "integer", nullable: false),
                    VarName = table.Column<string>(type: "text", nullable: false),
                    VarValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariablesSolutionsByPrograms", x => new { x.ProgramStep, x.ProgramLineNumber, x.OrderNumber, x.TestId, x.VarName });
                });

            migrationBuilder.CreateTable(
                name: "VariablesSolutionsByUsers",
                columns: table => new
                {
                    UserStep = table.Column<int>(type: "integer", nullable: false),
                    UserLineNumber = table.Column<int>(type: "integer", nullable: false),
                    OrderNumber = table.Column<int>(type: "integer", nullable: false),
                    TestId = table.Column<int>(type: "integer", nullable: false),
                    VarName = table.Column<string>(type: "text", nullable: false),
                    VarValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariablesSolutionsByUsers", x => new { x.UserStep, x.UserLineNumber, x.OrderNumber, x.TestId, x.VarName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolutionsByUsers_UserId",
                table: "SolutionsByUsers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolutionsByPrograms");

            migrationBuilder.DropTable(
                name: "SolutionsByUsers");

            migrationBuilder.DropTable(
                name: "VariablesSolutionsByPrograms");

            migrationBuilder.DropTable(
                name: "VariablesSolutionsByUsers");
        }
    }
}
