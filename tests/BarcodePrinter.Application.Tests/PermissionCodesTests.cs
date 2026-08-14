using BarcodePrinter.Contracts;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Application.Tests;

public class PermissionCodesTests
{
    [Fact]
    public void All_permission_codes_are_distinct_and_well_formed()
    {
        PermissionCodes.All.Should().OnlyHaveUniqueItems();
        PermissionCodes.All.Should().HaveCount(30);
        PermissionCodes.All.Should().OnlyContain(c =>
            c.Contains('.') && !c.StartsWith('.') && !c.EndsWith('.'));
    }

    [Fact]
    public void Reprint_is_a_distinct_permission_from_execute()
    {
        // A-22: RBAC must allow Reprint to become a separate permission —
        // it is one from day one.
        PermissionCodes.PrintReprint.Should().NotBe(PermissionCodes.PrintExecute);
        PermissionCodes.All.Should().Contain(PermissionCodes.PrintReprint);
    }
}
