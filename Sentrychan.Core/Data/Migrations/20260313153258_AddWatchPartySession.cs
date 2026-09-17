using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentrychan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchPartySession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchPartySessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    HostedSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    HostedEpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchPartySessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchPartySessions_Series_HostedSeriesId",
                        column: x => x.HostedSeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchPartySessions_HostedSeriesId",
                table: "WatchPartySessions",
                column: "HostedSeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchPartySessions");
        }
    }
}
