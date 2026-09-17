using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedHealthTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "RssFeeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedAt",
                table: "RssFeeds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "RssFeeds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessAt",
                table: "RssFeeds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "RssFeeds");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "RssFeeds");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "RssFeeds");

            migrationBuilder.DropColumn(
                name: "LastSuccessAt",
                table: "RssFeeds");
        }
    }
}
