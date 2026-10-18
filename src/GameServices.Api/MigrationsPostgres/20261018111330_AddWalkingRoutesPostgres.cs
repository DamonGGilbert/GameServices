using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddWalkingRoutesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalkingRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetMetres = table.Column<int>(type: "integer", nullable: false),
                    Metres = table.Column<int>(type: "integer", nullable: false),
                    QuietShare = table.Column<double>(type: "double precision", nullable: false),
                    BusyStretches = table.Column<int>(type: "integer", nullable: false),
                    PathJson = table.Column<string>(type: "text", nullable: false),
                    CheckpointsJson = table.Column<string>(type: "text", nullable: false),
                    NextCheckpoint = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckpointUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StepsAtStart = table.Column<long>(type: "bigint", nullable: false),
                    Biome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RewardGlimmer = table.Column<int>(type: "integer", nullable: false),
                    RewardMonster = table.Column<bool>(type: "boolean", nullable: false)
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
