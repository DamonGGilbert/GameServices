using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddShowcasePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShowEmblem",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowFrame",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowLevel",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShowPrestige",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ShowTitle",
                table: "Users",
                type: "text",
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
