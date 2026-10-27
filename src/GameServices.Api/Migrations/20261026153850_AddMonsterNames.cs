using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonsterNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NameTags",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);   // everyone already walking gets their free Name Tag too

            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "WalkingMonsters",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NameTags",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "WalkingMonsters");
        }
    }
}
