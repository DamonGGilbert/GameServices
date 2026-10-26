using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromMonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Stake = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ToMonsterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToStrategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    FromWon = table.Column<bool>(type: "INTEGER", nullable: false),
                    Chance = table.Column<float>(type: "REAL", nullable: false),
                    Roll = table.Column<float>(type: "REAL", nullable: false),
                    Ranked = table.Column<bool>(type: "INTEGER", nullable: false),
                    FromPointsChange = table.Column<int>(type: "INTEGER", nullable: false),
                    ToPointsChange = table.Column<int>(type: "INTEGER", nullable: false),
                    FromSpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FromSeed = table.Column<long>(type: "INTEGER", nullable: false),
                    FromTier = table.Column<int>(type: "INTEGER", nullable: false),
                    FromLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTrainedPower = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTrainedGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTrainedTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTrainedWits = table.Column<int>(type: "INTEGER", nullable: false),
                    FromPower = table.Column<int>(type: "INTEGER", nullable: false),
                    FromGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    FromWits = table.Column<int>(type: "INTEGER", nullable: false),
                    FromBst = table.Column<int>(type: "INTEGER", nullable: false),
                    ToSpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ToSeed = table.Column<long>(type: "INTEGER", nullable: false),
                    ToTier = table.Column<int>(type: "INTEGER", nullable: false),
                    ToLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTrainedPower = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTrainedGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTrainedTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTrainedWits = table.Column<int>(type: "INTEGER", nullable: false),
                    ToPower = table.Column<int>(type: "INTEGER", nullable: false),
                    ToGuard = table.Column<int>(type: "INTEGER", nullable: false),
                    ToTricks = table.Column<int>(type: "INTEGER", nullable: false),
                    ToWits = table.Column<int>(type: "INTEGER", nullable: false),
                    ToBst = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Challenges_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_FromUserId_Status",
                table: "Challenges",
                columns: new[] { "FromUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_ToUserId_Status",
                table: "Challenges",
                columns: new[] { "ToUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "WalkingProfiles");
        }
    }
}
