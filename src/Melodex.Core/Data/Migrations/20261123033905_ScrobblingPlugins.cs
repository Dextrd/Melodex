using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScrobblingPlugins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Accounts were linked by service; now they're linked with a scrobbling plugin (#236). Scrobbling came in
            // after the last release, so there are none to carry over.
            migrationBuilder.Sql("DELETE FROM \"ScrobbleSends\"; DELETE FROM \"ScrobbleAccounts\";");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleAccounts_UserId_Service",
                table: "ScrobbleAccounts");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "ScrobbleAccounts");

            migrationBuilder.AddColumn<Guid>(
                name: "PluginInstanceId",
                table: "ScrobbleAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleAccounts_UserId_PluginInstanceId",
                table: "ScrobbleAccounts",
                columns: new[] { "UserId", "PluginInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleAccounts_PluginInstanceId",
                table: "ScrobbleAccounts",
                column: "PluginInstanceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleAccounts_PluginInstances_PluginInstanceId",
                table: "ScrobbleAccounts",
                column: "PluginInstanceId",
                principalTable: "PluginInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"ScrobbleSends\"; DELETE FROM \"ScrobbleAccounts\";");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleAccounts_PluginInstances_PluginInstanceId",
                table: "ScrobbleAccounts");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleAccounts_PluginInstanceId",
                table: "ScrobbleAccounts");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleAccounts_UserId_PluginInstanceId",
                table: "ScrobbleAccounts");

            migrationBuilder.DropColumn(
                name: "PluginInstanceId",
                table: "ScrobbleAccounts");

            migrationBuilder.AddColumn<int>(
                name: "Service",
                table: "ScrobbleAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleAccounts_UserId_Service",
                table: "ScrobbleAccounts",
                columns: new[] { "UserId", "Service" },
                unique: true);
        }
    }
}
