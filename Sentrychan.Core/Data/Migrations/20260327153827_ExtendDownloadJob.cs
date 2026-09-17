using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendDownloadJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedFileName",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FileSizeBytes",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalFilePath",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseGroup",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TorrentHash",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Backend",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "ExpectedFileName",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "FileSizeBytes",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "FinalFilePath",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "ReleaseGroup",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "TorrentHash",
                table: "DownloadJobs");
        }
    }
}
