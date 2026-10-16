using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddGuildWarsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BannerStyle",
                table: "Guilds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WarLosses",
                table: "Guilds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WarWins",
                table: "Guilds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GuildWars",
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
                    table.PrimaryKey("PK_GuildWars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildWars_Guilds_GuildAId",
                        column: x => x.GuildAId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuildWars_Guilds_GuildBId",
                        column: x => x.GuildBId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildWarDuels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    PlayedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarDuels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildWarDuels_GuildWars_WarId",
                        column: x => x.WarId,
                        principalTable: "GuildWars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuildWarDuels_WarId_UserId",
                table: "GuildWarDuels",
                columns: new[] { "WarId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildWars_GuildAId_StartsUtc",
                table: "GuildWars",
                columns: new[] { "GuildAId", "StartsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildWars_GuildBId_StartsUtc",
                table: "GuildWars",
                columns: new[] { "GuildBId", "StartsUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildWarDuels");

            migrationBuilder.DropTable(
                name: "GuildWars");

            migrationBuilder.DropColumn(
                name: "BannerStyle",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "WarLosses",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "WarWins",
                table: "Guilds");
        }
    }
}
