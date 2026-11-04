using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedWordsAndBattles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BattleDay",
                table: "WalkingProfiles",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BattleGlimmerToday",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CompanionStepRemainder",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GuestProgressTaken",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Tonics",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "BondSteps",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RestUntilSteps",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "BlockedWords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedWords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalkingBattles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpponentId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MonsterIds = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    OpponentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Style = table.Column<int>(type: "INTEGER", nullable: false),
                    MyPower = table.Column<int>(type: "INTEGER", nullable: false),
                    MyGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    MyTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    MyWits = table.Column<int>(type: "INTEGER", nullable: false),
                    TheirPower = table.Column<int>(type: "INTEGER", nullable: false),
                    TheirGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    TheirTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    TheirWits = table.Column<int>(type: "INTEGER", nullable: false),
                    MyArena = table.Column<bool>(type: "INTEGER", nullable: false),
                    TheirArena = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FoughtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    TheirStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Tonic = table.Column<bool>(type: "INTEGER", nullable: false),
                    Signature = table.Column<bool>(type: "INTEGER", nullable: false),
                    Stake = table.Column<int>(type: "INTEGER", nullable: false),
                    Won = table.Column<bool>(type: "INTEGER", nullable: false),
                    Chance = table.Column<float>(type: "REAL", nullable: false),
                    Roll = table.Column<float>(type: "REAL", nullable: false),
                    GlimmerChange = table.Column<int>(type: "INTEGER", nullable: false),
                    PowerChange = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingBattles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingBattles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedWords_Text_Kind",
                table: "BlockedWords",
                columns: new[] { "Text", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkingBattles_FoughtUtc",
                table: "WalkingBattles",
                column: "FoughtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WalkingBattles_UserId_Status",
                table: "WalkingBattles",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedWords");

            migrationBuilder.DropTable(
                name: "WalkingBattles");

            migrationBuilder.DropColumn(
                name: "BattleDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BattleGlimmerToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "CompanionStepRemainder",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "GuestProgressTaken",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Tonics",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BondSteps",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "RestUntilSteps",
                table: "WalkingMonsters");
        }
    }
}
