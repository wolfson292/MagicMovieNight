using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MagicMovieNight.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitPeopleFromProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written. EF scaffolded this as DropTable("Viewers") + CreateTable("Profiles"),
            // which would have cascaded through the foreign keys and destroyed every watch
            // event in the database. Viewers already *were* profiles — accounts, not people —
            // so the tables are renamed in place and the rows kept.

            migrationBuilder.DropForeignKey(name: "FK_Ratings_Viewers_ViewerId", table: "Ratings");
            migrationBuilder.DropForeignKey(name: "FK_WatchEvents_Viewers_ViewerId", table: "WatchEvents");
            migrationBuilder.DropForeignKey(name: "FK_ViewerIdentities_Viewers_ViewerId", table: "ViewerIdentities");

            // --- Viewers becomes Profiles, rows intact ---
            migrationBuilder.RenameTable(name: "Viewers", newName: "Profiles");
            migrationBuilder.RenameIndex(name: "IX_Viewers_DisplayName", table: "Profiles", newName: "IX_Profiles_DisplayName");
            migrationBuilder.DropPrimaryKey(name: "PK_Viewers", table: "Profiles");
            migrationBuilder.AddPrimaryKey(name: "PK_Profiles", table: "Profiles", column: "Id");

            // Household membership and rating ceilings are properties of a person now.
            migrationBuilder.DropColumn(name: "IncludeInHousehold", table: "Profiles");
            migrationBuilder.DropColumn(name: "ContentRatingCeiling", table: "Profiles");

            // --- ViewerIdentities becomes ProfileIdentities, rows intact ---
            migrationBuilder.RenameTable(name: "ViewerIdentities", newName: "ProfileIdentities");
            migrationBuilder.RenameColumn(name: "ViewerId", table: "ProfileIdentities", newName: "ProfileId");
            migrationBuilder.RenameIndex(name: "IX_ViewerIdentities_Source_ExternalId", table: "ProfileIdentities", newName: "IX_ProfileIdentities_Source_ExternalId");
            migrationBuilder.RenameIndex(name: "IX_ViewerIdentities_ViewerId", table: "ProfileIdentities", newName: "IX_ProfileIdentities_ProfileId");
            migrationBuilder.DropPrimaryKey(name: "PK_ViewerIdentities", table: "ProfileIdentities");
            migrationBuilder.AddPrimaryKey(name: "PK_ProfileIdentities", table: "ProfileIdentities", column: "Id");

            // --- Watch events point at a profile; the column is renamed, never rebuilt ---
            migrationBuilder.RenameColumn(name: "ViewerId", table: "WatchEvents", newName: "ProfileId");
            migrationBuilder.RenameIndex(name: "IX_WatchEvents_ViewerId_WatchedAt", table: "WatchEvents", newName: "IX_WatchEvents_ProfileId_WatchedAt");

            migrationBuilder.RenameColumn(name: "ViewerIds", table: "RecommendationRuns", newName: "PersonIds");

            // --- Ratings move from profile to person: an opinion is held by a human ---
            migrationBuilder.RenameColumn(name: "ViewerId", table: "Ratings", newName: "PersonId");
            migrationBuilder.RenameIndex(
                name: "IX_Ratings_ViewerId_MediaItemId_SeasonNumber_EpisodeNumber",
                table: "Ratings",
                newName: "IX_Ratings_PersonId_MediaItemId_SeasonNumber_EpisodeNumber");

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    InHousehold = table.Column<bool>(type: "boolean", nullable: false),
                    IsResident = table.Column<bool>(type: "boolean", nullable: false),
                    ContentRatingCeiling = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_People", x => x.Id));

            migrationBuilder.CreateTable(
                name: "ProfileMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileMemberships_People_PersonId",
                        column: x => x.PersonId, principalTable: "People", principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileMemberships_Profiles_ProfileId",
                        column: x => x.ProfileId, principalTable: "Profiles", principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileMemberships_PersonId_ProfileId",
                table: "ProfileMemberships", columns: ["PersonId", "ProfileId"], unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileMemberships_ProfileId",
                table: "ProfileMemberships", column: "ProfileId");

            migrationBuilder.CreateIndex(name: "IX_People_Name", table: "People", column: "Name", unique: true);

            // Ratings were attached to profiles and now belong to people, so existing rows
            // have to be re-pointed before the foreign key can exist.
            //
            // There is no automatic answer for a shared account: a rating made on a
            // profile representing two people was made by a human the database never
            // recorded. The safe default gives each profile holding ratings a person of
            // the same name, so nothing is lost and the mapping is visible in the UI to
            // correct by hand. An existing person of that name is reused.
            //
            // A scratch column carries the original profile id, because person ids and
            // profile ids overlap — remapping in place would otherwise match rows that
            // had already been converted.
            migrationBuilder.Sql("""
                ALTER TABLE "Ratings" ADD COLUMN "_oldProfileId" integer;
                UPDATE "Ratings" SET "_oldProfileId" = "PersonId";

                INSERT INTO "People" ("Name", "InHousehold", "IsResident", "CreatedAt")
                SELECT DISTINCT p."DisplayName", FALSE, TRUE, now()
                FROM "Ratings" r
                JOIN "Profiles" p ON p."Id" = r."_oldProfileId"
                WHERE NOT EXISTS (
                    SELECT 1 FROM "People" pe WHERE lower(pe."Name") = lower(p."DisplayName"));

                UPDATE "Ratings" r
                SET "PersonId" = pe."Id"
                FROM "Profiles" p
                JOIN "People" pe ON lower(pe."Name") = lower(p."DisplayName")
                WHERE p."Id" = r."_oldProfileId";

                DELETE FROM "Ratings" r
                WHERE r."_oldProfileId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "People" pe WHERE pe."Id" = r."PersonId");

                ALTER TABLE "Ratings" DROP COLUMN "_oldProfileId";

                UPDATE "Ratings"
                SET "SourceKey" = 'ui:' || "PersonId" || ':' || "MediaItemId" || ':'
                    || coalesce("SeasonNumber"::text, '') || ':'
                    || coalesce("EpisodeNumber"::text, '')
                WHERE "Source" = 6;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ProfileIdentities_Profiles_ProfileId", table: "ProfileIdentities",
                column: "ProfileId", principalTable: "Profiles", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchEvents_Profiles_ProfileId", table: "WatchEvents",
                column: "ProfileId", principalTable: "Profiles", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Ratings_People_PersonId", table: "Ratings",
                column: "PersonId", principalTable: "People", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_Ratings_People_PersonId", table: "Ratings");
            migrationBuilder.DropForeignKey(name: "FK_WatchEvents_Profiles_ProfileId", table: "WatchEvents");
            migrationBuilder.DropForeignKey(name: "FK_ProfileIdentities_Profiles_ProfileId", table: "ProfileIdentities");

            migrationBuilder.DropTable(name: "ProfileMemberships");
            migrationBuilder.DropTable(name: "People");

            migrationBuilder.RenameColumn(name: "PersonId", table: "Ratings", newName: "ViewerId");
            migrationBuilder.RenameIndex(
                name: "IX_Ratings_PersonId_MediaItemId_SeasonNumber_EpisodeNumber",
                table: "Ratings",
                newName: "IX_Ratings_ViewerId_MediaItemId_SeasonNumber_EpisodeNumber");

            migrationBuilder.RenameColumn(name: "PersonIds", table: "RecommendationRuns", newName: "ViewerIds");

            migrationBuilder.RenameColumn(name: "ProfileId", table: "WatchEvents", newName: "ViewerId");
            migrationBuilder.RenameIndex(name: "IX_WatchEvents_ProfileId_WatchedAt", table: "WatchEvents", newName: "IX_WatchEvents_ViewerId_WatchedAt");

            migrationBuilder.DropPrimaryKey(name: "PK_ProfileIdentities", table: "ProfileIdentities");
            migrationBuilder.RenameIndex(name: "IX_ProfileIdentities_ProfileId", table: "ProfileIdentities", newName: "IX_ViewerIdentities_ViewerId");
            migrationBuilder.RenameIndex(name: "IX_ProfileIdentities_Source_ExternalId", table: "ProfileIdentities", newName: "IX_ViewerIdentities_Source_ExternalId");
            migrationBuilder.RenameColumn(name: "ProfileId", table: "ProfileIdentities", newName: "ViewerId");
            migrationBuilder.RenameTable(name: "ProfileIdentities", newName: "ViewerIdentities");
            migrationBuilder.AddPrimaryKey(name: "PK_ViewerIdentities", table: "ViewerIdentities", column: "Id");

            migrationBuilder.AddColumn<bool>(name: "IncludeInHousehold", table: "Profiles", type: "boolean", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<string>(name: "ContentRatingCeiling", table: "Profiles", type: "character varying(20)", maxLength: 20, nullable: true);

            migrationBuilder.DropPrimaryKey(name: "PK_Profiles", table: "Profiles");
            migrationBuilder.RenameIndex(name: "IX_Profiles_DisplayName", table: "Profiles", newName: "IX_Viewers_DisplayName");
            migrationBuilder.RenameTable(name: "Profiles", newName: "Viewers");
            migrationBuilder.AddPrimaryKey(name: "PK_Viewers", table: "Viewers", column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ViewerIdentities_Viewers_ViewerId", table: "ViewerIdentities",
                column: "ViewerId", principalTable: "Viewers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchEvents_Viewers_ViewerId", table: "WatchEvents",
                column: "ViewerId", principalTable: "Viewers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Ratings_Viewers_ViewerId", table: "Ratings",
                column: "ViewerId", principalTable: "Viewers", principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
