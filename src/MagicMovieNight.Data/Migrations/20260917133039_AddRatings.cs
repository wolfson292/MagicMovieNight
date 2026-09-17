using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MagicMovieNight.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ratings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ViewerId = table.Column<int>(type: "integer", nullable: false),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<int>(type: "integer", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ratings_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Ratings_Viewers_ViewerId",
                        column: x => x.ViewerId,
                        principalTable: "Viewers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_MediaItemId",
                table: "Ratings",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_Source_SourceKey",
                table: "Ratings",
                columns: new[] { "Source", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_ViewerId_MediaItemId_SeasonNumber_EpisodeNumber",
                table: "Ratings",
                columns: new[] { "ViewerId", "MediaItemId", "SeasonNumber", "EpisodeNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ratings");
        }
    }
}
