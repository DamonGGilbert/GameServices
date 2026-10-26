using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddChallengesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "WalkingProfiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromMonsterId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStrategy = table.Column<int>(type: "integer", nullable: false),
                    Stake = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ToMonsterId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToStrategy = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    FromWon = table.Column<bool>(type: "boolean", nullable: false),
                    Chance = table.Column<float>(type: "real", nullable: false),
                    Roll = table.Column<float>(type: "real", nullable: false),
                    Ranked = table.Column<bool>(type: "boolean", nullable: false),
                    FromPointsChange = table.Column<int>(type: "integer", nullable: false),
                    ToPointsChange = table.Column<int>(type: "integer", nullable: false),
                    FromSpeciesId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FromSeed = table.Column<long>(type: "bigint", nullable: false),
                    FromTier = table.Column<int>(type: "integer", nullable: false),
                    FromLevel = table.Column<int>(type: "integer", nullable: false),
                    FromTrainedPower = table.Column<int>(type: "integer", nullable: false),
                    FromTrainedGuard = table.Column<int>(type: "integer", nullable: false),
                    FromTrainedTricks = table.Column<int>(type: "integer", nullable: false),
                    FromTrainedWits = table.Column<int>(type: "integer", nullable: false),
                    FromPower = table.Column<int>(type: "integer", nullable: false),
                    FromGuard = table.Column<int>(type: "integer", nullable: false),
                    FromTricks = table.Column<int>(type: "integer", nullable: false),
                    FromWits = table.Column<int>(type: "integer", nullable: false),
                    FromBst = table.Column<int>(type: "integer", nullable: false),
                    ToSpeciesId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToSeed = table.Column<long>(type: "bigint", nullable: false),
                    ToTier = table.Column<int>(type: "integer", nullable: false),
                    ToLevel = table.Column<int>(type: "integer", nullable: false),
                    ToTrainedPower = table.Column<int>(type: "integer", nullable: false),
                    ToTrainedGuard = table.Column<int>(type: "integer", nullable: false),
                    ToTrainedTricks = table.Column<int>(type: "integer", nullable: false),
                    ToTrainedWits = table.Column<int>(type: "integer", nullable: false),
                    ToPower = table.Column<int>(type: "integer", nullable: false),
                    ToGuard = table.Column<int>(type: "integer", nullable: false),
                    ToTricks = table.Column<int>(type: "integer", nullable: false),
                    ToWits = table.Column<int>(type: "integer", nullable: false),
                    ToBst = table.Column<int>(type: "integer", nullable: false)
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
