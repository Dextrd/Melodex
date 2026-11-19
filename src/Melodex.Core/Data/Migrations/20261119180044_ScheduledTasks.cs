using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An install set up before scheduled tasks keeps its upgrade schedule, and has nothing else scheduled until
            // it's ticked; a new one starts with the defaults.
            migrationBuilder.Sql("INSERT INTO Settings (Key, ValueJson) SELECT 'schedule.fromUpgrades', 'true' WHERE EXISTS (SELECT 1 FROM Users);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM Settings WHERE Key IN ('schedule.fromUpgrades', 'schedule', 'schedule.runs');");
        }
    }
}
