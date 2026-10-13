using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineDuels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Draws",
                table: "Guilds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Losses",
                table: "Guilds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Guilds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Wins",
                table: "Guilds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DuelMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoomName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Player0Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Player1Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Player0Name = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Player1Name = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    WinnerSeat = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Turns = table.Column<int>(type: "INTEGER", nullable: false),
                    GuildMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    Guild0Id = table.Column<Guid>(type: "TEXT", nullable: true),
                    Guild1Id = table.Column<Guid>(type: "TEXT", nullable: true),
                    Guild0Delta = table.Column<int>(type: "INTEGER", nullable: false),
                    Guild1Delta = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuelMatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuelMatches_EndedUtc",
                table: "DuelMatches",
                column: "EndedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DuelMatches_Player0Id",
                table: "DuelMatches",
                column: "Player0Id");

            migrationBuilder.CreateIndex(
                name: "IX_DuelMatches_Player1Id",
                table: "DuelMatches",
                column: "Player1Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuelMatches");

            migrationBuilder.DropColumn(
                name: "Draws",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "Losses",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "Wins",
                table: "Guilds");
        }
    }
}
