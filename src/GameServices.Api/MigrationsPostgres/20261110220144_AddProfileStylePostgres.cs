using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddProfileStylePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Motto",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<int>(
                name: "ProfileColour",
                table: "WalkingProfiles",
                type: "integer",
                nullable: false,
                defaultValue: -1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Motto",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "ProfileColour",
                table: "WalkingProfiles");
        }
    }
}
