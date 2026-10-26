using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWalkTogether : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TogetherBonusToday",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TogetherCellX",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TogetherCellY",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TogetherDay",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TogetherDaysTotal",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "TogetherSeenUtc",
                table: "WalkingProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TogetherStepRemainder",
                table: "WalkingProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TogetherBonusToday",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherCellX",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherCellY",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherDay",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherDaysTotal",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherSeenUtc",
                table: "WalkingProfiles");

            migrationBuilder.DropColumn(
                name: "TogetherStepRemainder",
                table: "WalkingProfiles");
        }
    }
}
