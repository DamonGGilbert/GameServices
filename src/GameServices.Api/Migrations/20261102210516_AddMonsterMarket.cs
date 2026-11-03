using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameServices.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonsterMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketListings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonsterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SellerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpeciesId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RarityTier = table.Column<int>(type: "INTEGER", nullable: false),
                    StartPrice = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyNowPrice = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentBid = table.Column<int>(type: "INTEGER", nullable: false),
                    TopBidderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BidCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BuyerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SoldPrice = table.Column<int>(type: "INTEGER", nullable: false),
                    BoughtNow = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketListings_Users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MarketListings_WalkingMonsters_MonsterId",
                        column: x => x.MonsterId,
                        principalTable: "WalkingMonsters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketBids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BidderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketBids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketBids_MarketListings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "MarketListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketBids_BidderId",
                table: "MarketBids",
                column: "BidderId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketBids_ListingId_Amount",
                table: "MarketBids",
                columns: new[] { "ListingId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketListings_MonsterId",
                table: "MarketListings",
                column: "MonsterId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketListings_SellerId_Status",
                table: "MarketListings",
                columns: new[] { "SellerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketListings_SpeciesId_RarityTier_Status",
                table: "MarketListings",
                columns: new[] { "SpeciesId", "RarityTier", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketListings_Status_EndsUtc",
                table: "MarketListings",
                columns: new[] { "Status", "EndsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketListings_TopBidderId_Status",
                table: "MarketListings",
                columns: new[] { "TopBidderId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketBids");

            migrationBuilder.DropTable(
                name: "MarketListings");
        }
    }
}
