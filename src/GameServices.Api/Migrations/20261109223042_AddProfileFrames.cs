using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileFrames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Frame",
                table: "WalkingProfiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Frame",
                table: "WalkingProfiles");
        }
    }
}
