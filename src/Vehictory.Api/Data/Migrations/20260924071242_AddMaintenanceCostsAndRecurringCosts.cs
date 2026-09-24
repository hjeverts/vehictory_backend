using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vehictory.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceCostsAndRecurringCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Kosten",
                table: "MaintenanceEntries",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecurringCosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VehicleId = table.Column<int>(type: "integer", nullable: false),
                    Soort = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Bedrag = table.Column<decimal>(type: "numeric", nullable: false),
                    Frequentie = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Startdatum = table.Column<DateOnly>(type: "date", nullable: false),
                    Einddatum = table.Column<DateOnly>(type: "date", nullable: true),
                    Notitie = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringCosts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringCosts_VehicleId",
                table: "RecurringCosts",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringCosts");

            migrationBuilder.DropColumn(
                name: "Kosten",
                table: "MaintenanceEntries");
        }
    }
}
