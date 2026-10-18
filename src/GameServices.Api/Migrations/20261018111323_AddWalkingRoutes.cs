using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWalkingRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalkingRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetMetres = table.Column<int>(type: "INTEGER", nullable: false),
                    Metres = table.Column<int>(type: "INTEGER", nullable: false),
                    QuietShare = table.Column<double>(type: "REAL", nullable: false),
                    BusyStretches = table.Column<int>(type: "INTEGER", nullable: false),
                    PathJson = table.Column<string>(type: "TEXT", nullable: false),
                    CheckpointsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NextCheckpoint = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckpointUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StepsAtStart = table.Column<long>(type: "INTEGER", nullable: false),
                    Biome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RewardGlimmer = table.Column<int>(type: "INTEGER", nullable: false),
                    RewardMonster = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingRoutes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingRoutes_UserId_Status",
                table: "WalkingRoutes",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkingRoutes");
        }
    }
}
