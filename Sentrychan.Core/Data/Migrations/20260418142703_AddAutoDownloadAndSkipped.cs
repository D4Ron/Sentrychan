using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoDownloadAndSkipped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AutoDownload — genuinely new column
            migrationBuilder.AddColumn<bool>(
                name: "AutoDownload",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // SkippedDownloads — genuinely new table
            migrationBuilder.CreateTable(
                name: "SkippedDownloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPermanent = table.Column<bool>(type: "INTEGER", nullable: false),
                    SkippedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SkipCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkippedDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkippedDownloads_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkippedDownloads_SeriesId_EpisodeNumber",
                table: "SkippedDownloads",
                columns: new[] { "SeriesId", "EpisodeNumber" });

            // SeasonNumber       — already in AddSeasonNumber (20260327153657)
            // Backend/ExpectedFileName/FinalFilePath/TorrentHash — already in ExtendDownloadJob (20260327153827)
            // UnmatchedFiles table + indexes — already in AddUnmatchedFiles (20260417103111)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkippedDownloads");

            migrationBuilder.DropColumn(
                name: "AutoDownload",
                table: "Series");
        }
    }
}
