using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddLuckyDrawPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpinTickets",
                table: "CardGameProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "OpponentRewards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpponentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Prestige = table.Column<int>(type: "integer", nullable: false),
                    LastClaimUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpponentRewards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpponentRewards_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketGrants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpponentRewards_UserId_OpponentId",
                table: "OpponentRewards",
                columns: new[] { "UserId", "OpponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketGrants_UserId_Reason",
                table: "TicketGrants",
                columns: new[] { "UserId", "Reason" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpponentRewards");

            migrationBuilder.DropTable(
                name: "TicketGrants");

            migrationBuilder.DropColumn(
                name: "SpinTickets",
                table: "CardGameProfiles");
        }
    }
}
