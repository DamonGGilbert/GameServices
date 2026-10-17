using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddWalkingPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cheers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cheers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cheers_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Cheers_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepRaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Hours = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChallengerBaseline = table.Column<long>(type: "bigint", nullable: false),
                    OpponentBaseline = table.Column<long>(type: "bigint", nullable: false),
                    ChallengerSteps = table.Column<int>(type: "integer", nullable: false),
                    OpponentSteps = table.Column<int>(type: "integer", nullable: false),
                    WinnerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepRaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StepRaces_Users_ChallengerId",
                        column: x => x.ChallengerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StepRaces_Users_OpponentId",
                        column: x => x.OpponentId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalkingDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Steps = table.Column<int>(type: "integer", nullable: false),
                    RejectedSteps = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalkingDays_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalkingProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LifetimeSteps = table.Column<long>(type: "bigint", nullable: false),
                    LastReportUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShareSteps = table.Column<bool>(type: "boolean", nullable: false),
                    RacesWon = table.Column<int>(type: "integer", nullable: false),
                    RacesLost = table.Column<int>(type: "integer", nullable: false),
                    FlaggedReports = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalkingProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_WalkingProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cheers_FromUserId_ToUserId_SentUtc",
                table: "Cheers",
                columns: new[] { "FromUserId", "ToUserId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Cheers_ToUserId_Id",
                table: "Cheers",
                columns: new[] { "ToUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StepRaces_ChallengerId_Status",
                table: "StepRaces",
                columns: new[] { "ChallengerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StepRaces_OpponentId_Status",
                table: "StepRaces",
                columns: new[] { "OpponentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StepRaces_Status_EndsUtc",
                table: "StepRaces",
                columns: new[] { "Status", "EndsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WalkingDays_UserId_Day",
                table: "WalkingDays",
                columns: new[] { "UserId", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cheers");

            migrationBuilder.DropTable(
                name: "StepRaces");

            migrationBuilder.DropTable(
                name: "WalkingDays");

            migrationBuilder.DropTable(
                name: "WalkingProfiles");
        }
    }
}
