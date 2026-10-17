using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddWalkingTradesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompanionMonsterId",
                table: "WalkingProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Glimmer",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GlimmerStepRemainder",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PacksDay",
                table: "WalkingProfiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PacksToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PhoneGlimmerDay",
                table: "WalkingProfiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PhoneGlimmerToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WalletStarted",
                table: "WalkingProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcquiredUtc",
                table: "WalkingMonsters",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "ArrivedFromUserId",
                table: "WalkingMonsters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Experience",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TradeCount",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainedGuard",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainedPower",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainedTricks",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainedWits",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainingSteps",
                table: "WalkingMonsters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MonsterOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonsterId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonsterOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonsterOffers_WalkingMonsters_MonsterId",
                        column: x => x.MonsterId,
                        principalTable: "WalkingMonsters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalkingLedger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfter = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingLedger_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonsterOffers_FromUserId_Status",
                table: "MonsterOffers",
                columns: new[] { "FromUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MonsterOffers_MonsterId_Status",
                table: "MonsterOffers",
                columns: new[] { "MonsterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MonsterOffers_ToUserId_Status",
                table: "MonsterOffers",
                columns: new[] { "ToUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingLedger_UserId_CreatedUtc",
                table: "WalkingLedger",
                columns: new[] { "UserId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonsterOffers");

            migrationBuilder.DropTable(
                name: "WalkingLedger");

            migrationBuilder.DropColumn(
                name: "CompanionMonsterId",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Glimmer",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "GlimmerStepRemainder",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PacksDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PacksToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PhoneGlimmerDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PhoneGlimmerToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "WalletStarted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "AcquiredUtc",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "ArrivedFromUserId",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "Experience",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TradeCount",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainedGuard",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainedPower",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainedTricks",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainedWits",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainingSteps",
                table: "WalkingMonsters");
        }
    }
}
