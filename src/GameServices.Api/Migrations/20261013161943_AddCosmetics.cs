using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCosmetics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CosmeticsJson",
                table: "Users",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CosmeticsJson",
                table: "Users");
        }
    }
}
