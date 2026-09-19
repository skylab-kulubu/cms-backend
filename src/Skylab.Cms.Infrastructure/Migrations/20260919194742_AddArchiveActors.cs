using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skylab.Cms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveActors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchivedBy",
                table: "content_blocks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedBy",
                table: "collection_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                table: "content_blocks");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                table: "collection_items");
        }
    }
}
