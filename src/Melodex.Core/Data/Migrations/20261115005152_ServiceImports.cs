using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Albums",
                table: "Imports",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "Imports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceList",
                table: "Imports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatesPlaylistId",
                table: "Imports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AlbumId",
                table: "ImportLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "ImportLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Imports_BatchId",
                table: "Imports",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Imports_BatchId",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "Albums",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "ServiceList",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "UpdatesPlaylistId",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                table: "ImportLines");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "ImportLines");
        }
    }
}
