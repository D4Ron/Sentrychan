using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMangaLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FetchedAt",
                table: "MangaChapters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceOrder",
                table: "MangaChapters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MangaCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MangaChapterBookmarks",
                columns: table => new
                {
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    BookmarkedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaChapterBookmarks", x => x.ChapterId);
                    table.ForeignKey(
                        name: "FK_MangaChapterBookmarks_MangaChapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "MangaChapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MangaReadingHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPage = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaReadingHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MangaReadingHistory_MangaChapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "MangaChapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MangaReadingHistory_Manga_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Manga",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MangaCategoryLinks",
                columns: table => new
                {
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaCategoryLinks", x => new { x.MangaId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_MangaCategoryLinks_MangaCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "MangaCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MangaCategoryLinks_Manga_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Manga",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MangaChapters_FetchedAt",
                table: "MangaChapters",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MangaCategoryLinks_CategoryId",
                table: "MangaCategoryLinks",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MangaReadingHistory_ChapterId",
                table: "MangaReadingHistory",
                column: "ChapterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MangaReadingHistory_MangaId",
                table: "MangaReadingHistory",
                column: "MangaId");

            migrationBuilder.CreateIndex(
                name: "IX_MangaReadingHistory_ReadAt",
                table: "MangaReadingHistory",
                column: "ReadAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MangaCategoryLinks");

            migrationBuilder.DropTable(
                name: "MangaChapterBookmarks");

            migrationBuilder.DropTable(
                name: "MangaReadingHistory");

            migrationBuilder.DropTable(
                name: "MangaCategories");

            migrationBuilder.DropIndex(
                name: "IX_MangaChapters_FetchedAt",
                table: "MangaChapters");

            migrationBuilder.DropColumn(
                name: "FetchedAt",
                table: "MangaChapters");

            migrationBuilder.DropColumn(
                name: "SourceOrder",
                table: "MangaChapters");
        }
    }
}
