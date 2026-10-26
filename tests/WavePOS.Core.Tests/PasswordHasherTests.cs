using WavePOS.Core.Security;

namespace WavePOS.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Correct_password_verifies()
    {
        var hash = PasswordHasher.Hash("1234");
        Assert.True(PasswordHasher.Verify("1234", hash));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var hash = PasswordHasher.Hash("1234");
        Assert.False(PasswordHasher.Verify("1235", hash));
    }

    [Fact]
    public void Same_password_gets_different_salt()
    {
        Assert.NotEqual(PasswordHasher.Hash("1234"), PasswordHasher.Hash("1234"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$abc$x$y")]
    public void Malformed_hash_fails_safely(string stored)
    {
        Assert.False(PasswordHasher.Verify("1234", stored));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("123", false)]
    [InlineData("1234", true)]
    public void Validate_enforces_minimum_length(string password, bool ok)
    {
        Assert.Equal(ok, PasswordHasher.Validate(password) is null);
    }
}
