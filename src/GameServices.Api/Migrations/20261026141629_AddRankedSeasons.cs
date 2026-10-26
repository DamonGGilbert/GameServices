using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRankedSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RankedLosses",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RankedWins",
                table: "WalkingMonsters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttackerPointsChange",
                table: "RankedMatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttackerTier",
                table: "RankedMatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefenderPointsChange",
                table: "RankedMatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HighValue",
                table: "RankedMatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Season",
                table: "RankedMatches",
                type: "TEXT",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RankedSeasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Points = table.Column<int>(type: "INTEGER", nullable: false),
                    Wins = table.Column<int>(type: "INTEGER", nullable: false),
                    Losses = table.Column<int>(type: "INTEGER", nullable: false),
                    HighValueWins = table.Column<int>(type: "INTEGER", nullable: false),
                    RewardClaimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RankedSeasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RankedSeasons_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RankedSeasons_Season_Points",
                table: "RankedSeasons",
                columns: new[] { "Season", "Points" });

            migrationBuilder.CreateIndex(
                name: "IX_RankedSeasons_UserId_Season",
                table: "RankedSeasons",
                columns: new[] { "UserId", "Season" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RankedSeasons");

            migrationBuilder.DropColumn(
                name: "RankedLosses",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "RankedWins",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "AttackerPointsChange",
                table: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "AttackerTier",
                table: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "DefenderPointsChange",
                table: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "HighValue",
                table: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "Season",
                table: "RankedMatches");
        }
    }
}
