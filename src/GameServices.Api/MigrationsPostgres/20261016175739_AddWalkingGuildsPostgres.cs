using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddWalkingGuildsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildJourneyClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStart = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Glimmer = table.Column<int>(type: "integer", nullable: false),
                    ClaimedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildJourneyClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildJourneyClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildStepRaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildAId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildBId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ScoreA = table.Column<int>(type: "integer", nullable: false),
                    ScoreB = table.Column<int>(type: "integer", nullable: false),
                    WinnerGuildId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildStepRaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuildJourneyClaims_UserId_WeekStart",
                table: "GuildJourneyClaims",
                columns: new[] { "UserId", "WeekStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildStepRaces_GuildAId",
                table: "GuildStepRaces",
                column: "GuildAId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildStepRaces_GuildBId",
                table: "GuildStepRaces",
                column: "GuildBId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildStepRaces_StartsUtc",
                table: "GuildStepRaces",
                column: "StartsUtc");

            migrationBuilder.CreateIndex(
                name: "IX_GuildStepRaces_Status_EndsUtc",
                table: "GuildStepRaces",
                columns: new[] { "Status", "EndsUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildJourneyClaims");

            migrationBuilder.DropTable(
                name: "GuildStepRaces");
        }
    }
}
