using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryTidy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepFileNames",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MediaType",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TidyExcluded",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryFileOrigins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    OriginalName = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryFileOrigins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryFileOrigins_Path",
                table: "LibraryFileOrigins",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryFileOrigins");

            migrationBuilder.DropColumn(
                name: "KeepFileNames",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "MediaType",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "TidyExcluded",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Series");
        }
    }
}
