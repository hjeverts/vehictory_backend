using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vehictory.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleDepreciationAndSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Aanschafprijs",
                table: "Vehicles",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Afschrijvingstabel",
                table: "Vehicles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Restwaarde",
                table: "Vehicles",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Verkoopdatum",
                table: "Vehicles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Verkoopprijs",
                table: "Vehicles",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Aanschafprijs",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Afschrijvingstabel",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Restwaarde",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Verkoopdatum",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Verkoopprijs",
                table: "Vehicles");
        }
    }
}
