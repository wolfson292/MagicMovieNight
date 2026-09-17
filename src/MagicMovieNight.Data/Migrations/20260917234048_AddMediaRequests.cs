using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MagicMovieNight.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestNote",
                table: "MediaItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAt",
                table: "MediaItems",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestNote",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                table: "MediaItems");
        }
    }
}
