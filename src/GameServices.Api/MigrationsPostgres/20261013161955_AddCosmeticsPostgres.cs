using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AddCosmeticsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CosmeticsJson",
                table: "Users",
                type: "character varying(2000)",
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
