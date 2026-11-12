using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringApp.Services.EFCore.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectoryUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LocalDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GivenName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    FamilyName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryUsers", x => x.Id);
                    table.CheckConstraint("CK_DirectoryUsers_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_DirectoryUsers_Origin", "[Origin] IN ('Sql', 'EntraLogin', 'Scim', 'Legacy')");
                    table.CheckConstraint("CK_DirectoryUsers_Revision", "[Revision] > 0");
                });

            migrationBuilder.CreateTable(
                name: "ScimConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimConnections", x => x.Id);
                    table.UniqueConstraint("AK_ScimConnections_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("CK_ScimConnections_Name", "LTRIM(RTRIM([Name])) <> ''");
                    table.CheckConstraint("CK_ScimConnections_Revision", "[Revision] > 0");
                    table.CheckConstraint("CK_ScimConnections_Tenant", "[TenantId] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "UserTeams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTeams", x => x.Id);
                    table.CheckConstraint("CK_UserTeams_Name", "LTRIM(RTRIM([Name])) <> ''");
                    table.CheckConstraint("CK_UserTeams_Revision", "[Revision] > 0");
                });

            migrationBuilder.CreateTable(
                name: "EntraUserIdentities",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntraUserIdentities", x => x.UserId);
                    table.UniqueConstraint("AK_EntraUserIdentities_UserId_TenantId_ObjectId", x => new { x.UserId, x.TenantId, x.ObjectId });
                    table.CheckConstraint("CK_EntraUserIdentities_Object", "[ObjectId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_EntraUserIdentities_Tenant", "[TenantId] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_EntraUserIdentities_DirectoryUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LegacyUserLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerificationNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyUserLinks", x => x.Id);
                    table.CheckConstraint("CK_LegacyUserLinks_Alias", "LTRIM(RTRIM([Alias])) <> ''");
                    table.CheckConstraint("CK_LegacyUserLinks_Approval", "[Status] <> 'Confirmed' OR ([TargetUserId] IS NOT NULL AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAt] IS NOT NULL AND [VerificationNote] IS NOT NULL AND LTRIM(RTRIM([VerificationNote])) <> '')");
                    table.CheckConstraint("CK_LegacyUserLinks_Revision", "[Revision] > 0");
                    table.CheckConstraint("CK_LegacyUserLinks_Status", "[Status] IN ('Pending', 'Confirmed', 'Conflict', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_LegacyUserLinks_DirectoryUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LegacyUserLinks_DirectoryUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SqlUserIdentities",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlUserIdentities", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SqlUserIdentities_AuthenticationUsers_Username",
                        column: x => x.Username,
                        principalTable: "AuthenticationUsers",
                        principalColumn: "Username");
                    table.ForeignKey(
                        name: "FK_SqlUserIdentities_DirectoryUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ScimGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SyncStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroups", x => x.Id);
                    table.UniqueConstraint("AK_ScimGroups_Id_ConnectionId", x => new { x.Id, x.ConnectionId });
                    table.CheckConstraint("CK_ScimGroups_ExternalId", "[ExternalId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ScimGroups_Revision", "[Revision] > 0");
                    table.CheckConstraint("CK_ScimGroups_Status", "[SyncStatus] IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "FK_ScimGroups_ScimConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "ScimConnections",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ScimUserStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SyncStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimUserStates", x => x.UserId);
                    table.UniqueConstraint("AK_ScimUserStates_UserId_ConnectionId", x => new { x.UserId, x.ConnectionId });
                    table.CheckConstraint("CK_ScimUserStates_Revision", "[Revision] > 0");
                    table.CheckConstraint("CK_ScimUserStates_Status", "[SyncStatus] IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "FK_ScimUserStates_EntraUserIdentities_UserId_TenantId_ExternalId",
                        columns: x => new { x.UserId, x.TenantId, x.ExternalId },
                        principalTable: "EntraUserIdentities",
                        principalColumns: new[] { "UserId", "TenantId", "ObjectId" });
                    table.ForeignKey(
                        name: "FK_ScimUserStates_ScimConnections_ConnectionId_TenantId",
                        columns: x => new { x.ConnectionId, x.TenantId },
                        principalTable: "ScimConnections",
                        principalColumns: new[] { "Id", "TenantId" });
                });

            migrationBuilder.CreateTable(
                name: "UserTeamGroupMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTeamGroupMappings", x => x.Id);
                    table.UniqueConstraint("AK_UserTeamGroupMappings_Id_TeamId_GroupId", x => new { x.Id, x.TeamId, x.GroupId });
                    table.CheckConstraint("CK_UserTeamGroupMappings_Revision", "[Revision] > 0");
                    table.ForeignKey(
                        name: "FK_UserTeamGroupMappings_ScimGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ScimGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserTeamGroupMappings_UserTeams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "UserTeams",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ScimGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ScimGroupMembers_ScimGroups_GroupId_ConnectionId",
                        columns: x => new { x.GroupId, x.ConnectionId },
                        principalTable: "ScimGroups",
                        principalColumns: new[] { "Id", "ConnectionId" });
                    table.ForeignKey(
                        name: "FK_ScimGroupMembers_ScimUserStates_UserId_ConnectionId",
                        columns: x => new { x.UserId, x.ConnectionId },
                        principalTable: "ScimUserStates",
                        principalColumns: new[] { "UserId", "ConnectionId" });
                });

            migrationBuilder.CreateTable(
                name: "UserTeamMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTeamMemberships", x => x.Id);
                    table.CheckConstraint("CK_UserTeamMemberships_Source", "([GroupMappingId] IS NULL AND [SourceGroupId] IS NULL) OR ([GroupMappingId] IS NOT NULL AND [SourceGroupId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_UserTeamMemberships_DirectoryUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "DirectoryUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserTeamMemberships_ScimGroupMembers_SourceGroupId_UserId",
                        columns: x => new { x.SourceGroupId, x.UserId },
                        principalTable: "ScimGroupMembers",
                        principalColumns: new[] { "GroupId", "UserId" });
                    table.ForeignKey(
                        name: "FK_UserTeamMemberships_UserTeamGroupMappings_GroupMappingId_TeamId_SourceGroupId",
                        columns: x => new { x.GroupMappingId, x.TeamId, x.SourceGroupId },
                        principalTable: "UserTeamGroupMappings",
                        principalColumns: new[] { "Id", "TeamId", "GroupId" });
                    table.ForeignKey(
                        name: "FK_UserTeamMemberships_UserTeams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "UserTeams",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntraUserIdentities_TenantId_ObjectId",
                table: "EntraUserIdentities",
                columns: new[] { "TenantId", "ObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegacyUserLinks_Alias",
                table: "LegacyUserLinks",
                column: "Alias",
                unique: true,
                filter: "[TenantId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyUserLinks_Alias_TenantId",
                table: "LegacyUserLinks",
                columns: new[] { "Alias", "TenantId" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyUserLinks_ApprovedByUserId",
                table: "LegacyUserLinks",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyUserLinks_TargetUserId",
                table: "LegacyUserLinks",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroupMembers_GroupId_ConnectionId",
                table: "ScimGroupMembers",
                columns: new[] { "GroupId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroupMembers_UserId_ConnectionId",
                table: "ScimGroupMembers",
                columns: new[] { "UserId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroups_ConnectionId_ExternalId",
                table: "ScimGroups",
                columns: new[] { "ConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimUserStates_ConnectionId_ExternalId",
                table: "ScimUserStates",
                columns: new[] { "ConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimUserStates_ConnectionId_TenantId",
                table: "ScimUserStates",
                columns: new[] { "ConnectionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimUserStates_UserId_TenantId_ExternalId",
                table: "ScimUserStates",
                columns: new[] { "UserId", "TenantId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlUserIdentities_Username",
                table: "SqlUserIdentities",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamGroupMappings_GroupId",
                table: "UserTeamGroupMappings",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamGroupMappings_TeamId_GroupId",
                table: "UserTeamGroupMappings",
                columns: new[] { "TeamId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamMemberships_GroupMappingId_TeamId_SourceGroupId",
                table: "UserTeamMemberships",
                columns: new[] { "GroupMappingId", "TeamId", "SourceGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamMemberships_SourceGroupId_UserId",
                table: "UserTeamMemberships",
                columns: new[] { "SourceGroupId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamMemberships_TeamId",
                table: "UserTeamMemberships",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamMemberships_UserId_TeamId",
                table: "UserTeamMemberships",
                columns: new[] { "UserId", "TeamId" },
                unique: true,
                filter: "[GroupMappingId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserTeamMemberships_UserId_TeamId_GroupMappingId",
                table: "UserTeamMemberships",
                columns: new[] { "UserId", "TeamId", "GroupMappingId" },
                unique: true,
                filter: "[GroupMappingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserTeams_Name",
                table: "UserTeams",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegacyUserLinks");

            migrationBuilder.DropTable(
                name: "SqlUserIdentities");

            migrationBuilder.DropTable(
                name: "UserTeamMemberships");

            migrationBuilder.DropTable(
                name: "ScimGroupMembers");

            migrationBuilder.DropTable(
                name: "UserTeamGroupMappings");

            migrationBuilder.DropTable(
                name: "ScimUserStates");

            migrationBuilder.DropTable(
                name: "ScimGroups");

            migrationBuilder.DropTable(
                name: "UserTeams");

            migrationBuilder.DropTable(
                name: "EntraUserIdentities");

            migrationBuilder.DropTable(
                name: "ScimConnections");

            migrationBuilder.DropTable(
                name: "DirectoryUsers");
        }
    }
}
