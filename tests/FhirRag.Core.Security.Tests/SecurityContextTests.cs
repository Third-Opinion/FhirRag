using FluentAssertions;
using FhirRag.Core.Security;
using Xunit;

namespace FhirRag.Core.Security.Tests;

public class SecurityContextTests
{
    [Fact]
    public void SecurityContext_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var context = new SecurityContext();

        // Assert
        context.UserId.Should().BeEmpty();
        context.TenantId.Should().BeEmpty();
        context.Permissions.Should().NotBeNull().And.BeEmpty();
        context.Roles.Should().NotBeNull().And.BeEmpty();
        context.IsSystemUser.Should().BeFalse();
        context.IsAuthenticated.Should().BeFalse();
        context.AuthenticatedAt.Should().BeNull();
        context.ExpiresAt.Should().BeNull();
        context.SessionId.Should().BeEmpty();
        context.IpAddress.Should().BeEmpty();
        context.UserAgent.Should().BeEmpty();
        context.Claims.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void HasPermission_Should_Return_True_For_System_User()
    {
        // Arrange
        var context = new SecurityContext
        {
            IsSystemUser = true
        };

        // Act
        var hasPermission = context.HasPermission("any.permission");

        // Assert
        hasPermission.Should().BeTrue();
    }

    [Fact]
    public void HasPermission_Should_Return_True_When_Permission_Exists()
    {
        // Arrange
        var context = new SecurityContext();
        context.Permissions.Add("read.patient");
        context.Permissions.Add("write.observation");

        // Act
        var hasPermission = context.HasPermission("read.patient");

        // Assert
        hasPermission.Should().BeTrue();
    }

    [Fact]
    public void HasPermission_Should_Return_False_When_Permission_Does_Not_Exist()
    {
        // Arrange
        var context = new SecurityContext();
        context.Permissions.Add("read.patient");

        // Act
        var hasPermission = context.HasPermission("write.patient");

        // Assert
        hasPermission.Should().BeFalse();
    }

    [Fact]
    public void HasPermission_Should_Be_Case_Insensitive()
    {
        // Arrange
        var context = new SecurityContext();
        context.Permissions.Add("READ.PATIENT");

        // Act
        var hasPermission = context.HasPermission("read.patient");

        // Assert
        hasPermission.Should().BeTrue();
    }

    [Fact]
    public void HasRole_Should_Return_True_When_Role_Exists()
    {
        // Arrange
        var context = new SecurityContext();
        context.Roles.Add("doctor");
        context.Roles.Add("admin");

        // Act
        var hasRole = context.HasRole("doctor");

        // Assert
        hasRole.Should().BeTrue();
    }

    [Fact]
    public void HasRole_Should_Return_False_When_Role_Does_Not_Exist()
    {
        // Arrange
        var context = new SecurityContext();
        context.Roles.Add("doctor");

        // Act
        var hasRole = context.HasRole("nurse");

        // Assert
        hasRole.Should().BeFalse();
    }

    [Fact]
    public void HasRole_Should_Be_Case_Insensitive()
    {
        // Arrange
        var context = new SecurityContext();
        context.Roles.Add("DOCTOR");

        // Act
        var hasRole = context.HasRole("doctor");

        // Assert
        hasRole.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_Should_Return_False_When_ExpiresAt_Is_Null()
    {
        // Arrange
        var context = new SecurityContext
        {
            ExpiresAt = null
        };

        // Act
        var isExpired = context.IsExpired();

        // Assert
        isExpired.Should().BeFalse();
    }

    [Fact]
    public void IsExpired_Should_Return_True_When_ExpiresAt_Is_In_Past()
    {
        // Arrange
        var context = new SecurityContext
        {
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };

        // Act
        var isExpired = context.IsExpired();

        // Assert
        isExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_Should_Return_False_When_ExpiresAt_Is_In_Future()
    {
        // Arrange
        var context = new SecurityContext
        {
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        // Act
        var isExpired = context.IsExpired();

        // Assert
        isExpired.Should().BeFalse();
    }

    [Fact]
    public void GetClaim_Should_Return_Claim_Value_When_Exists()
    {
        // Arrange
        var context = new SecurityContext();
        context.Claims["sub"] = "user123";
        context.Claims["name"] = "John Doe";

        // Act
        var claimValue = context.GetClaim("name");

        // Assert
        claimValue.Should().Be("John Doe");
    }

    [Fact]
    public void GetClaim_Should_Return_Null_When_Claim_Does_Not_Exist()
    {
        // Arrange
        var context = new SecurityContext();
        context.Claims["sub"] = "user123";

        // Act
        var claimValue = context.GetClaim("email");

        // Assert
        claimValue.Should().BeNull();
    }

    [Fact]
    public void GetPermissions_Should_Return_All_Permissions()
    {
        // Arrange
        var context = new SecurityContext();
        context.Permissions.Add("read.patient");
        context.Permissions.Add("write.observation");
        context.Permissions.Add("delete.condition");

        // Act
        var permissions = context.GetPermissions();

        // Assert
        permissions.Should().HaveCount(3);
        permissions.Should().Contain("read.patient");
        permissions.Should().Contain("write.observation");
        permissions.Should().Contain("delete.condition");
    }

    [Fact]
    public void GetRoles_Should_Return_All_Roles()
    {
        // Arrange
        var context = new SecurityContext();
        context.Roles.Add("doctor");
        context.Roles.Add("admin");
        context.Roles.Add("researcher");

        // Act
        var roles = context.GetRoles();

        // Assert
        roles.Should().HaveCount(3);
        roles.Should().Contain("doctor");
        roles.Should().Contain("admin");
        roles.Should().Contain("researcher");
    }
}