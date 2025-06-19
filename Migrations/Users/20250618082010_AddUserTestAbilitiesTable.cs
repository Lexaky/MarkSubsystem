using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarkSubsystem.Migrations.Users
{
    /// <inheritdoc />
    public partial class AddUserTestAbilitiesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserTestAbilities",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    TestId = table.Column<int>(type: "integer", nullable: false),
                    Ability = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTestAbilities", x => new { x.UserId, x.TestId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTestAbilities");
        }
    }
}
