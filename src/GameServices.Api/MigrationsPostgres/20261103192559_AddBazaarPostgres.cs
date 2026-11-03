using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddBazaarPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BazaarDay",
                table: "WalkingProfiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CardStyles",
                table: "WalkingProfiles",
                type: "character varying(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "MendersToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TonicsToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TraderBuysToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TraderSellsToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WildBitesToday",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WildDay",
                table: "WalkingProfiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CardStyle",
                table: "WalkingMonsters",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BazaarDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "CardStyles",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "MendersToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TonicsToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TraderBuysToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TraderSellsToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "WildBitesToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "WildDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "CardStyle",
                table: "WalkingMonsters");
        }
    }
}
