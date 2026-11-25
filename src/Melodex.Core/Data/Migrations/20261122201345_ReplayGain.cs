using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplayGain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AlbumGain",
                table: "LibraryFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AlbumPeak",
                table: "LibraryFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GainsRead",
                table: "LibraryFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "TrackGain",
                table: "LibraryFiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrackPeak",
                table: "LibraryFiles",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumGain",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "AlbumPeak",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "GainsRead",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "TrackGain",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "TrackPeak",
                table: "LibraryFiles");
        }
    }
}
