using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Moq;

namespace Cocorra.Tests.Helpers;

public static class TestIdentityHelper
{
    public static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    public static Mock<RoleManager<IdentityRole<Guid>>> CreateMockRoleManager()
    {
        var store = new Mock<IRoleStore<IdentityRole<Guid>>>();
        return new Mock<RoleManager<IdentityRole<Guid>>>(
            store.Object, null!, null!, null!, null!);
    }
}
