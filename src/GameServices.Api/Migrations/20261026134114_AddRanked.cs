using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRanked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RankedDay",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RankedLosses",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RankedRewardedWinsToday",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RankedWins",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RankedEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenceStyle = table.Column<int>(type: "INTEGER", nullable: false),
                    Wins = table.Column<int>(type: "INTEGER", nullable: false),
                    Losses = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankedEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RankedEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RankedEntries_WalkingMonsters_MonsterId",
                        column: x => x.MonsterId,
                        principalTable: "WalkingMonsters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RankedMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttackerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttackerMonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttackerPower = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerWits = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerBst = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DefenderMonsterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Shadow = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefenderSpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DefenderSeed = table.Column<long>(type: "INTEGER", nullable: false),
                    DefenderTier = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTrainedPower = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTrainedGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTrainedTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTrainedWits = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderPower = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderWits = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderBst = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderStyle = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttackerStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerWon = table.Column<bool>(type: "INTEGER", nullable: false),
                    Chance = table.Column<float>(type: "REAL", nullable: false),
                    Roll = table.Column<float>(type: "REAL", nullable: false),
                    Reward = table.Column<int>(type: "INTEGER", nullable: false),
                    FoughtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankedMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RankedMatches_Users_AttackerId",
                        column: x => x.AttackerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RankedEntries_MonsterId",
                table: "RankedEntries",
                column: "MonsterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RankedEntries_UserId",
                table: "RankedEntries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RankedMatches_AttackerId_Status",
                table: "RankedMatches",
                columns: new[] { "AttackerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RankedMatches_DefenderId_Status",
                table: "RankedMatches",
                columns: new[] { "DefenderId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RankedEntries");

            migrationBuilder.DropTable(
                name: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "RankedDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RankedLosses",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RankedRewardedWinsToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RankedWins",
                table: "WalkingProfiles");
        }
    }
}
