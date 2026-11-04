using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServerProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AreasVisited",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Badges",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BattlesLost",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BattlesWon",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "BoostMultiplier",
                table: "WalkingProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 1f);

            migrationBuilder.AddColumn<DateTime>(
                name: "BoostUntilUtc",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChallengesCompleted",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ClanSteps",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Cosmetics",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CurrentStreak",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DailyGoalsCompleted",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveredSpecies",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FindsSinceMonster",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Fragments",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastGoalDay",
                table: "WalkingProfiles",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LegendaryFinds",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LifetimeMetres",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "LongestStreak",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PlayerLevel",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerXp",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ProgressStarted",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RareFinds",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RestDays",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RestDaysWeek",
                table: "WalkingProfiles",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RoutesCompleted",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "StartingSteps",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Titles",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TradesCompleted",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrainingFocus",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UtcOffsetMinutes",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BefriendedUtc",
                table: "WalkingMonsters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrainingDay",
                table: "WalkingMonsters",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Areas",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BattlesWon",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedMilestones",
                table: "WalkingDays",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Discoveries",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "GoalCompleted",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Metres",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Quests",
                table: "WalkingDays",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Trades",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "WalkingDays",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WalkingAchievements",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AchievementId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Unlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Claimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnlockedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingAchievements", x => new { x.UserId, x.AchievementId });
                    table.ForeignKey(
                        name: "FK_WalkingAchievements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalkingActivity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingActivity_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalkingMedals",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Reached = table.Column<int>(type: "INTEGER", nullable: false),
                    Claimed = table.Column<int>(type: "INTEGER", nullable: false),
                    EarnedUnix = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingMedals", x => new { x.UserId, x.TrackId });
                    table.ForeignKey(
                        name: "FK_WalkingMedals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingActivity_AtUtc",
                table: "WalkingActivity",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WalkingActivity_UserId_AtUtc",
                table: "WalkingActivity",
                columns: new[] { "UserId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalkingAchievements");

            migrationBuilder.DropTable(
                name: "WalkingActivity");

            migrationBuilder.DropTable(
                name: "WalkingMedals");

            migrationBuilder.DropColumn(
                name: "AreasVisited",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Badges",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BattlesLost",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BattlesWon",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BoostMultiplier",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BoostUntilUtc",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "ChallengesCompleted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "ClanSteps",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Cosmetics",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "CurrentStreak",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "DailyGoalsCompleted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "DiscoveredSpecies",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "FindsSinceMonster",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Fragments",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "LastGoalDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "LegendaryFinds",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "LifetimeMetres",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "LongestStreak",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PlayerLevel",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "PlayerXp",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "ProgressStarted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RareFinds",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RestDays",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RestDaysWeek",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "RoutesCompleted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "StartingSteps",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Titles",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TradesCompleted",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TrainingFocus",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "UtcOffsetMinutes",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "BefriendedUtc",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "TrainingDay",
                table: "WalkingMonsters");

            migrationBuilder.DropColumn(
                name: "Areas",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "BattlesWon",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "ClaimedMilestones",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "Discoveries",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "GoalCompleted",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "Metres",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "Quests",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "Trades",
                table: "WalkingDays");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "WalkingDays");
        }
    }
}
