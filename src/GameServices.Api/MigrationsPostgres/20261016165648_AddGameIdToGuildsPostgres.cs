using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddGameIdToGuildsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Guilds_NormalizedName",
                table: "Guilds");

            migrationBuilder.DropIndex(
                name: "IX_GuildMembers_UserId",
                table: "GuildMembers");

            migrationBuilder.AddColumn<string>(
                name: "GameId",
                table: "Guilds",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "yugioh");

            migrationBuilder.AddColumn<string>(
                name: "GameId",
                table: "GuildMembers",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "yugioh");

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_GameId_NormalizedName",
                table: "Guilds",
                columns: new[] { "GameId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildMembers_UserId_GameId",
                table: "GuildMembers",
                columns: new[] { "UserId", "GameId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Guilds_GameId_NormalizedName",
                table: "Guilds");

            migrationBuilder.DropIndex(
                name: "IX_GuildMembers_UserId_GameId",
                table: "GuildMembers");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "Guilds");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "GuildMembers");

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_NormalizedName",
                table: "Guilds",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildMembers_UserId",
                table: "GuildMembers",
                column: "UserId",
                unique: true);
        }
    }
}
