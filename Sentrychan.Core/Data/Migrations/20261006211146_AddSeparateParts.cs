using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeparateParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeparateParts",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeparateParts",
                table: "Series");
        }
    }
}
