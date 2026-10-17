using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddWalkingMonstersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EncounterSteps",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MonsterSpeciesCounts",
                columns: table => new
                {
                    SpeciesId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Found = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonsterSpeciesCounts", x => x.SpeciesId);
                });

            migrationBuilder.CreateTable(
                name: "WalkingMonsters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpeciesId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RarityTier = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    Potential = table.Column<int>(type: "integer", nullable: false),
                    RulesVersion = table.Column<int>(type: "integer", nullable: false),
                    Serial = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BiomeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FoundDay = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FoundUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Seen = table.Column<bool>(type: "boolean", nullable: false)
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
