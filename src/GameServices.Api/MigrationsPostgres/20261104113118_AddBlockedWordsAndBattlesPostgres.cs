using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddBlockedWordsAndBattlesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BattleDay",
                table: "WalkingProfiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BattleGlimmerToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CompanionStepRemainder",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GuestProgressTaken",
                table: "WalkingProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Tonics",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "BondSteps",
                table: "WalkingMonsters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RestUntilSteps",
                table: "WalkingMonsters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "BlockedWords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedWords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalkingBattles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpponentId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MonsterIds = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OpponentsJson = table.Column<string>(type: "text", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    Style = table.Column<int>(type: "integer", nullable: false),
                    MyPower = table.Column<int>(type: "integer", nullable: false),
                    MyGuard = table.Column<int>(type: "integer", nullable: false),
                    MyTricks = table.Column<int>(type: "integer", nullable: false),
                    MyWits = table.Column<int>(type: "integer", nullable: false),
                    TheirPower = table.Column<int>(type: "integer", nullable: false),
                    TheirGuard = table.Column<int>(type: "integer", nullable: false),
                    TheirTricks = table.Column<int>(type: "integer", nullable: false),
                    TheirWits = table.Column<int>(type: "integer", nullable: false),
                    MyArena = table.Column<bool>(type: "boolean", nullable: false),
                    TheirArena = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FoughtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Strategy = table.Column<int>(type: "integer", nullable: false),
                    TheirStrategy = table.Column<int>(type: "integer", nullable: false),
                    Tonic = table.Column<bool>(type: "boolean", nullable: false),
                    Signature = table.Column<bool>(type: "boolean", nullable: false),
                    Stake = table.Column<int>(type: "integer", nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    Chance = table.Column<float>(type: "real", nullable: false),
                    Roll = table.Column<float>(type: "real", nullable: false),
                    GlimmerChange = table.Column<int>(type: "integer", nullable: false),
                    PowerChange = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingBattles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingBattles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedWords_Text_Kind",
                table: "BlockedWords",
                columns: new[] { "Text", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalkingBattles_FoughtUtc",
                table: "WalkingBattles",
                column: "FoughtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WalkingBattles_UserId_Status",
                table: "WalkingBattles",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedWords");

            migrationBuilder.DropTable(
                name: "WalkingBattles");

            migrationBuilder.DropColumn(
                name: "BattleDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BattleGlimmerToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "CompanionStepRemainder",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "GuestProgressTaken",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Tonics",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BondSteps",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "RestUntilSteps",
                table: "WalkingMonsters");
        }
    }
}
