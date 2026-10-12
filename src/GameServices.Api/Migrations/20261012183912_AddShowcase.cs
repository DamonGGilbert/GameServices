using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShowEmblem",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowFrame",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowLevel",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowPrestige",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ShowTitle",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowEmblem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShowFrame",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShowLevel",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShowPrestige",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ShowTitle",
                table: "Users");
        }
    }
}
