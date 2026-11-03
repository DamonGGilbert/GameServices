using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddPowerBattlesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbsorbedGuard",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedPower",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedTricks",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedWits",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalPower",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalSteps",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalStepsLeft",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PowerBattle",
                table: "Challenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PowerMoved",
                table: "Challenges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AbsorbedGuard",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "AbsorbedPower",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "AbsorbedTricks",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "AbsorbedWits",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "CrystalPower",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "CrystalSteps",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "CrystalStepsLeft",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "PowerBattle",
                table: "Challenges");

            migrationBuilder.DropColumn(
                name: "PowerMoved",
                table: "Challenges");
        }
    }
}
