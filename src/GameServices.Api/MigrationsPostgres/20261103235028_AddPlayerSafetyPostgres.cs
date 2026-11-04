using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddPlayerSafetyPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "XpDay",
                table: "WalkingMonsters",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "XpToday",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AgeConfirmedUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuspendedUntilUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RankedMatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "MonsterSwaps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "MonsterOffers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "MarketListings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Challenges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlayerReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportedName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerReports_Users_ReportedUserId",
                        column: x => x.ReportedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlayerReports_Users_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ClosedUtc",
                table: "PlayerReports",
                column: "ClosedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ReportedUserId",
                table: "PlayerReports",
                column: "ReportedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ReporterId_CreatedUtc",
                table: "PlayerReports",
                columns: new[] { "ReporterId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerReports");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "XpDay",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "XpToday",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "AgeConfirmedUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SuspendedUntilUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RankedMatches");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MonsterSwaps");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MonsterOffers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MarketListings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Challenges");
        }
    }
}
