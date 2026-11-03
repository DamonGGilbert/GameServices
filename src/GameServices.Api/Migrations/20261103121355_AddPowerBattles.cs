using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerBattles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbsorbedGuard",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedPower",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedTricks",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedWits",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalPower",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalSteps",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrystalStepsLeft",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PowerBattle",
                table: "Challenges",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PowerMoved",
                table: "Challenges",
                type: "INTEGER",
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
