using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonsterSwaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonsterSwaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromMonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToMonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonsterSwaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonsterSwaps_WalkingMonsters_FromMonsterId",
                        column: x => x.FromMonsterId,
                        principalTable: "WalkingMonsters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonsterSwaps_WalkingMonsters_ToMonsterId",
                        column: x => x.ToMonsterId,
                        principalTable: "WalkingMonsters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonsterSwaps_FromMonsterId",
                table: "MonsterSwaps",
                column: "FromMonsterId");

            migrationBuilder.CreateIndex(
                name: "IX_MonsterSwaps_FromUserId_Status",
                table: "MonsterSwaps",
                columns: new[] { "FromUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MonsterSwaps_ToMonsterId",
                table: "MonsterSwaps",
                column: "ToMonsterId");

            migrationBuilder.CreateIndex(
                name: "IX_MonsterSwaps_ToUserId_Status",
                table: "MonsterSwaps",
                columns: new[] { "ToUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonsterSwaps");
        }
    }
}
