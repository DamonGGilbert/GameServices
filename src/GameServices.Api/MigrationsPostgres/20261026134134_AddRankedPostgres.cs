using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddRankedPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RankedDay",
                table: "WalkingProfiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RankedLosses",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RankedRewardedWinsToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RankedWins",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RankedEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MonsterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    DefenceStyle = table.Column<int>(type: "integer", nullable: false),
                    Wins = table.Column<int>(type: "integer", nullable: false),
                    Losses = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttackerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttackerMonsterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttackerPower = table.Column<int>(type: "integer", nullable: false),
                    AttackerGuard = table.Column<int>(type: "integer", nullable: false),
                    AttackerTricks = table.Column<int>(type: "integer", nullable: false),
                    AttackerWits = table.Column<int>(type: "integer", nullable: false),
                    AttackerBst = table.Column<int>(type: "integer", nullable: false),
                    DefenderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefenderMonsterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Shadow = table.Column<bool>(type: "boolean", nullable: false),
                    DefenderSpeciesId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefenderSeed = table.Column<long>(type: "bigint", nullable: false),
                    DefenderTier = table.Column<int>(type: "integer", nullable: false),
                    DefenderLevel = table.Column<int>(type: "integer", nullable: false),
                    DefenderTrainedPower = table.Column<int>(type: "integer", nullable: false),
                    DefenderTrainedGuard = table.Column<int>(type: "integer", nullable: false),
                    DefenderTrainedTricks = table.Column<int>(type: "integer", nullable: false),
                    DefenderTrainedWits = table.Column<int>(type: "integer", nullable: false),
                    DefenderPower = table.Column<int>(type: "integer", nullable: false),
                    DefenderGuard = table.Column<int>(type: "integer", nullable: false),
                    DefenderTricks = table.Column<int>(type: "integer", nullable: false),
                    DefenderWits = table.Column<int>(type: "integer", nullable: false),
                    DefenderBst = table.Column<int>(type: "integer", nullable: false),
                    DefenderStyle = table.Column<int>(type: "integer", nullable: false),
                    WindowPercent = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttackerStrategy = table.Column<int>(type: "integer", nullable: false),
                    DefenderStrategy = table.Column<int>(type: "integer", nullable: false),
                    AttackerWon = table.Column<bool>(type: "boolean", nullable: false),
                    Chance = table.Column<float>(type: "real", nullable: false),
                    Roll = table.Column<float>(type: "real", nullable: false),
                    Reward = table.Column<int>(type: "integer", nullable: false),
                    FoughtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
