using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mandate.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRunClaimCorrected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClaimCorrected",
                table: "agent_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimCorrected",
                table: "agent_runs");
        }
    }
}
