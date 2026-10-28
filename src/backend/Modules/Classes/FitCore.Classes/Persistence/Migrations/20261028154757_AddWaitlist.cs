using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offer_expires_at",
                schema: "classes",
                table: "class_reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offered_at",
                schema: "classes",
                table: "class_reservations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "offer_expires_at",
                schema: "classes",
                table: "class_reservations");

            migrationBuilder.DropColumn(
                name: "offered_at",
                schema: "classes",
                table: "class_reservations");
        }
    }
}
