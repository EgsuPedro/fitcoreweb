using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanIsPublic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_public",
                schema: "subscriptions",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_public",
                schema: "subscriptions",
                table: "plans");
        }
    }
}
