using Microsoft.Data.Sqlite;
using WavePOS.Core.Models;

namespace WavePOS.Data.Tests;

public sealed class UserRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wavepos-test-{Guid.NewGuid():N}.db");
    private readonly UserRepository _users;

    public UserRepositoryTests()
    {
        var db = new PosDatabase(_path);
        db.Initialize(seedSampleProducts: false);
        _users = new UserRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void New_database_has_no_users()
    {
        Assert.False(_users.AnyUsers());
    }

    [Fact]
    public void Created_user_can_log_in_with_any_username_case()
    {
        _users.Create("mary", "Mary Banda", "4321", UserRole.Manager);

        var result = _users.Authenticate("MARY", "4321");

        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.Equal("Mary Banda", result.User!.DisplayName);
        Assert.True(result.User.IsManager);
        Assert.True(_users.AnyUsers());
    }

    [Fact]
    public void Wrong_password_and_unknown_user_are_rejected()
    {
        _users.Create("john", "John Phiri", "1111", UserRole.Cashier);

        Assert.Equal(LoginStatus.InvalidCredentials, _users.Authenticate("john", "2222").Status);
        Assert.Equal(LoginStatus.InvalidCredentials, _users.Authenticate("nobody", "1111").Status);
    }

    [Fact]
    public void Account_locks_after_too_many_wrong_attempts_then_unlocks()
    {
        _users.Create("john", "John Phiri", "1111", UserRole.Cashier);
        var now = new DateTime(2026, 10, 27, 9, 0, 0);

        for (var i = 1; i < UserRepository.MaxFailedAttempts; i++)
            Assert.Equal(LoginStatus.InvalidCredentials, _users.Authenticate("john", "0000", now).Status);

        Assert.Equal(LoginStatus.LockedOut, _users.Authenticate("john", "0000", now).Status);

        // Even the right password is refused while locked.
        Assert.Equal(LoginStatus.LockedOut, _users.Authenticate("john", "1111", now.AddMinutes(1)).Status);

        // After the lockout period the right password works again.
        Assert.Equal(LoginStatus.Success,
            _users.Authenticate("john", "1111", now + UserRepository.LockoutDuration + TimeSpan.FromSeconds(1)).Status);
    }

    [Fact]
    public void Duplicate_username_is_rejected()
    {
        _users.Create("john", "John Phiri", "1111", UserRole.Cashier);

        var ex = Assert.Throws<ArgumentException>(() => _users.Create("JOHN", "Another John", "2222", UserRole.Cashier));
        Assert.Contains("already taken", ex.Message);
    }

    [Fact]
    public void Short_password_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => _users.Create("john", "John", "12", UserRole.Cashier));
    }
}
