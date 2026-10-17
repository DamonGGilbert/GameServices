using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWalkingMonsters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EncounterSteps",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MonsterSpeciesCounts",
                columns: table => new
                {
                    SpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Found = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonsterSpeciesCounts", x => x.SpeciesId);
                });

            migrationBuilder.CreateTable(
                name: "WalkingMonsters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RarityTier = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Potential = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Serial = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BiomeId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FoundDay = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    FoundUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seen = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingMonsters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingMonsters_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingMonsters_OwnerId_FoundDay",
                table: "WalkingMonsters",
                columns: new[] { "OwnerId", "FoundDay" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingMonsters_SpeciesId_Serial",
                table: "WalkingMonsters",
                columns: new[] { "SpeciesId", "Serial" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonsterSpeciesCounts");

            migrationBuilder.DropTable(
                name: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "EncounterSteps",
                table: "WalkingProfiles");
        }
    }
}
