using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Cocorra.BLL.DTOS.Auth;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.ProfileDto;
using Cocorra.DAL.DTOS.Role;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// Cocorra is 18+. Every DTO that accepts an age must reject minors at model validation.
/// </summary>
public class AgePolicyTests
{
    private static List<ValidationResult> ValidateAge(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains("Age")).ToList();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(121)]
    public void RegisterDto_RejectsAgesOutsideAdultRange(int age)
    {
        var errors = ValidateAge(new RegisterDto { Age = age });

        var error = Assert.Single(errors);
        Assert.Equal(AgePolicy.RangeErrorMessage, error.ErrorMessage);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(45)]
    [InlineData(120)]
    public void RegisterDto_AcceptsAdultAges(int age)
    {
        Assert.Empty(ValidateAge(new RegisterDto { Age = age }));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(0)]
    public void UpdateProfileDto_RejectsMinors(int age)
    {
        Assert.Single(ValidateAge(new UpdateProfileDto { Age = age }));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(17)]
    public void CreateUserDto_RejectsMinors(int age)
    {
        Assert.Single(ValidateAge(new CreateUserDto { Age = age }));
    }

    [Fact]
    public void CreateUserDto_DefaultAgeIsValid()
    {
        Assert.Empty(ValidateAge(new CreateUserDto()));
    }
}
