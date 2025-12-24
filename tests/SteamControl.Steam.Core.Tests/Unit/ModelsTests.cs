using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit;

/// <summary>
/// 测试数据模型和枚举类型
/// </summary>
public class ModelsTests
{
	#region SessionState 枚举测试

	[Fact]
	public void SessionState_HasAllExpectedValues()
	{
		// 获取所有枚举值
		var values = Enum.GetValues<SessionState>();

		// Assert - 验证所有预期值存在
		var expectedValues = new[]
		{
			SessionState.Disconnected,
			SessionState.Connecting,
			SessionState.ConnectingWaitAuthCode,
			SessionState.ConnectingWait2FA,
			SessionState.Connected,
			SessionState.Reconnecting,
			SessionState.DisconnectedByUser,
			SessionState.Disconnecting,
			SessionState.FatalError
		};

		Assert.Equal(expectedValues.Length, values.Length);
		Assert.All(expectedValues, ev => Assert.Contains(ev, values));
	}

	[Fact]
	public void SessionState_Disconnected_IsDefault()
	{
		// Act & Assert
		Assert.Equal(0, (int)SessionState.Disconnected);
	}

	[Fact]
	public void SessionState_ValuesAreUnique()
	{
		// Arrange
		var values = Enum.GetValues<SessionState>();

		// Act & Assert - 验证所有值唯一
		var uniqueValues = new HashSet<SessionState>(values);
		Assert.Equal(values.Length, uniqueValues.Count);
	}

	[Theory]
	[InlineData("Disconnected")]
	[InlineData("Connecting")]
	[InlineData("ConnectingWaitAuthCode")]
	[InlineData("ConnectingWait2FA")]
	[InlineData("Connected")]
	[InlineData("Reconnecting")]
	[InlineData("DisconnectedByUser")]
	[InlineData("Disconnecting")]
	[InlineData("FatalError")]
	public void SessionState_CanParseFromString(string stateName)
	{
		// Act
		var success = Enum.TryParse<SessionState>(stateName, out var result);

		// Assert
		Assert.True(success);
		Assert.NotEqual(default, result);
	}

	#endregion

	#region SessionEventType 枚举测试

	[Fact]
	public void SessionEventType_HasAllExpectedValues()
	{
		// 获取所有枚举值
		var values = Enum.GetValues<SessionEventType>();

		// Assert
		var expectedValues = new[]
		{
			SessionEventType.StateChanged,
			SessionEventType.AuthCodeNeeded,
			SessionEventType.TwoFactorCodeNeeded,
			SessionEventType.Connected,
			SessionEventType.Disconnected,
			SessionEventType.Error
		};

		Assert.Equal(expectedValues.Length, values.Length);
		Assert.All(expectedValues, ev => Assert.Contains(ev, values));
	}

	[Fact]
	public void SessionEventType_StateChanged_IsDefault()
	{
		// Act & Assert
		Assert.Equal(0, (int)SessionEventType.StateChanged);
	}

	[Fact]
	public void SessionEventType_ValuesAreUnique()
	{
		// Arrange
		var values = Enum.GetValues<SessionEventType>();

		// Act & Assert
		var uniqueValues = new HashSet<SessionEventType>(values);
		Assert.Equal(values.Length, uniqueValues.Count);
	}

	#endregion

	#region ActionMetadata 测试

	[Fact]
	public void ActionMetadata_WithRequiredParameters_CreatesInstance()
	{
		// Act
		var metadata = new ActionMetadata("test_action", "Test description");

		// Assert
		Assert.Equal("test_action", metadata.Name);
		Assert.Equal("Test description", metadata.Description);
		Assert.False(metadata.RequiresLogin);
		Assert.Null(metadata.TimeoutSeconds);
	}

	[Fact]
	public void ActionMetadata_WithAllParameters_CreatesInstance()
	{
		// Act
		var metadata = new ActionMetadata(
			"test_action",
			"Test description",
			RequiresLogin: true,
			TimeoutSeconds: 60
		);

		// Assert
		Assert.Equal("test_action", metadata.Name);
		Assert.Equal("Test description", metadata.Description);
		Assert.True(metadata.RequiresLogin);
		Assert.Equal(60, metadata.TimeoutSeconds);
	}

	[Fact]
	public void ActionMetadata_IsRecordType_SupportsWithEquality()
	{
		// Arrange
		var metadata1 = new ActionMetadata("test", "desc", true, 30);
		var metadata2 = new ActionMetadata("test", "desc", true, 30);

		// Act & Assert
		Assert.Equal(metadata1, metadata2);
		Assert.True(metadata1 == metadata2);
	}

	[Fact]
	public void ActionMetadata_WithDifferentParameters_AreNotEqual()
	{
		// Arrange
		var metadata1 = new ActionMetadata("test1", "desc1", true, 30);
		var metadata2 = new ActionMetadata("test2", "desc2", false, 60);

		// Act & Assert
		Assert.NotEqual(metadata1, metadata2);
	}

	[Fact]
	public void ActionMetadata_WithExpression_SupportsWithUpdate()
	{
		// Arrange
		var metadata = new ActionMetadata("test", "desc", false, 30);

		// Act
		var updated = metadata with { RequiresLogin = true };

		// Assert
		Assert.Equal("test", updated.Name);
		Assert.Equal("desc", updated.Description);
		Assert.False(metadata.RequiresLogin);
		Assert.True(updated.RequiresLogin);
	}

	#endregion

	#region ActionResult 测试

	[Fact]
	public void ActionResult_WithSuccessOnly_CreatesInstance()
	{
		// Act
		var result = new ActionResult(true);

		// Assert
		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public void ActionResult_WithError_CreatesInstance()
	{
		// Arrange
		var errorMessage = "Test error";

		// Act
		var result = new ActionResult(false, errorMessage, null);

		// Assert
		Assert.False(result.Success);
		Assert.Equal(errorMessage, result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public void ActionResult_WithOutput_CreatesInstance()
	{
		// Arrange
		var output = new Dictionary<string, object?>
		{
			["key1"] = "value1",
			["key2"] = 42
		};

		// Act
		var result = new ActionResult(true, null, output);

		// Assert
		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.NotNull(result.Output);
		Assert.Equal(2, result.Output!.Count);
	}

	[Fact]
	public void ActionResult_WithAllParameters_CreatesInstance()
	{
		// Arrange
		var output = new Dictionary<string, object?> { ["result"] = "success" };

		// Act
		var result = new ActionResult(true, null, output);

		// Assert
		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.NotNull(result.Output);
		Assert.Equal("success", result.Output!["result"]?.ToString());
	}

	[Fact]
	public void ActionResult_IsRecordType_SupportsWithEquality()
	{
		// Arrange
		var output = new Dictionary<string, object?> { ["test"] = "value" };
		var result1 = new ActionResult(true, null, output);
		var result2 = new ActionResult(true, null, output);

		// Act & Assert
		// 注意: 对于引用类型的 Output，由于是同一个实例，应该相等
		Assert.Equal(result1.Success, result2.Success);
		Assert.Same(output, result1.Output);
	}

	[Fact]
	public void ActionResult_WithDifferentSuccessValue_AreNotEqual()
	{
		// Arrange
		var result1 = new ActionResult(true, null, null);
		var result2 = new ActionResult(false, "error", null);

		// Act & Assert
		Assert.NotEqual(result1.Success, result2.Success);
	}

	#endregion

	#region AccountCredentials 测试

	[Fact]
	public void AccountCredentials_WithRequiredParameters_CreatesInstance()
	{
		// Act
		var credentials = new AccountCredentials("test_account", "password123");

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("password123", credentials.Password);
		Assert.Null(credentials.AuthCode);
		Assert.Null(credentials.TwoFactorCode);
		Assert.Null(credentials.RefreshToken);
		Assert.Null(credentials.AccessToken);
	}

	[Fact]
	public void AccountCredentials_WithAuthCode_CreatesInstance()
	{
		// Act
		var credentials = new AccountCredentials("test_account", "password123", "AUTH123");

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("password123", credentials.Password);
		Assert.Equal("AUTH123", credentials.AuthCode);
		Assert.Null(credentials.TwoFactorCode);
	}

	[Fact]
	public void AccountCredentials_WithTwoFactorCode_CreatesInstance()
	{
		// Act
		var credentials = new AccountCredentials("test_account", "password123", TwoFactorCode: "2FA456");

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("password123", credentials.Password);
		Assert.Null(credentials.AuthCode);
		Assert.Equal("2FA456", credentials.TwoFactorCode);
	}

	[Fact]
	public void AccountCredentials_WithTokens_CreatesInstance()
	{
		// Act
		var credentials = new AccountCredentials(
			"test_account",
			"password123",
			RefreshToken: "refresh_token_value",
			AccessToken: "access_token_value"
		);

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("refresh_token_value", credentials.RefreshToken);
		Assert.Equal("access_token_value", credentials.AccessToken);
	}

	[Fact]
	public void AccountCredentials_WithAllParameters_CreatesInstance()
	{
		// Act
		var credentials = new AccountCredentials(
			"test_account",
			"password123",
			"AUTH123",
			"2FA456",
			"refresh_token",
			"access_token"
		);

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("password123", credentials.Password);
		Assert.Equal("AUTH123", credentials.AuthCode);
		Assert.Equal("2FA456", credentials.TwoFactorCode);
		Assert.Equal("refresh_token", credentials.RefreshToken);
		Assert.Equal("access_token", credentials.AccessToken);
	}

	[Fact]
	public void AccountCredentials_IsRecordType_SupportsWith()
	{
		// Arrange
		var credentials = new AccountCredentials("test_account", "password123");

		// Act
		var updated = credentials with { Password = "new_password" };

		// Assert
		Assert.Equal("test_account", credentials.AccountName);
		Assert.Equal("password123", credentials.Password);
		Assert.Equal("test_account", updated.AccountName);
		Assert.Equal("new_password", updated.Password);
	}

	[Fact]
	public void AccountCredentials_WithSameValues_AreEqual()
	{
		// Arrange
		var credentials1 = new AccountCredentials("test_account", "password123");
		var credentials2 = new AccountCredentials("test_account", "password123");

		// Act & Assert
		Assert.Equal(credentials1, credentials2);
	}

	[Fact]
	public void AccountCredentials_WithDifferentValues_AreNotEqual()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "pass1");
		var credentials2 = new AccountCredentials("account2", "pass2");

		// Act & Assert
		Assert.NotEqual(credentials1, credentials2);
	}

	#endregion

	#region SessionEvent 测试

	[Fact]
	public void SessionEvent_DefaultConstructor_CreatesEventWithDefaultTimestamp()
	{
		// Act
		var evt = new SessionEvent();

		// Assert
		Assert.Equal(default, evt.Type);
		Assert.Equal(string.Empty, evt.AccountName);
		Assert.Null(evt.NewState);
		Assert.Null(evt.Message);
		Assert.NotEqual(default, evt.Timestamp);
	}

	[Fact]
	public void SessionEvent_WithParameters_CreatesEvent()
	{
		// Arrange
		var type = SessionEventType.StateChanged;
		var accountName = "test_account";
		var newState = SessionState.Connected;
		var message = "Connected successfully";
		var timestamp = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

		// Act
		var evt = new SessionEvent(type, accountName, newState, message, timestamp);

		// Assert
		Assert.Equal(type, evt.Type);
		Assert.Equal(accountName, evt.AccountName);
		Assert.Equal(newState, evt.NewState);
		Assert.Equal(message, evt.Message);
		Assert.Equal(timestamp, evt.Timestamp);
	}

	[Fact]
	public void SessionEvent_WithMinimalParameters_CreatesEvent()
	{
		// Act
		var evt = new SessionEvent(
			SessionEventType.Connected,
			"test_account"
		);

		// Assert
		Assert.Equal(SessionEventType.Connected, evt.Type);
		Assert.Equal("test_account", evt.AccountName);
		Assert.Null(evt.NewState);
		Assert.Null(evt.Message);
	}

	[Fact]
	public void SessionEvent_WithStateOnly_CreatesEvent()
	{
		// Act
		var evt = new SessionEvent(
			SessionEventType.StateChanged,
			"test_account",
			SessionState.Connected
		);

		// Assert
		Assert.Equal(SessionEventType.StateChanged, evt.Type);
		Assert.Equal("test_account", evt.AccountName);
		Assert.Equal(SessionState.Connected, evt.NewState);
		Assert.Null(evt.Message);
	}

	[Fact]
	public void SessionEvent_DefaultTimestamp_IsUtcNow()
	{
		// Arrange
		var before = DateTimeOffset.UtcNow;

		// Act
		var evt = new SessionEvent();

		var after = DateTimeOffset.UtcNow;

		// Assert
		Assert.InRange(evt.Timestamp, before.AddSeconds(-1), after.AddSeconds(1));
	}

	[Fact]
	public void SessionEvent_IsRecordType_SupportsWith()
	{
		// Arrange
		var evt = new SessionEvent(
			SessionEventType.StateChanged,
			"test_account",
			SessionState.Connected,
			"Connected"
		);

		// Act
		var updated = evt with { NewState = SessionState.Disconnected };

		// Assert
		Assert.Equal(SessionState.Connected, evt.NewState);
		Assert.Equal(SessionState.Disconnected, updated.NewState);
	}

	[Fact]
	public void SessionEvent_DifferentEventTypes_AreNotEqual()
	{
		// Arrange
		var evt1 = new SessionEvent(SessionEventType.Connected, "account");
		var evt2 = new SessionEvent(SessionEventType.Disconnected, "account");

		// Act & Assert
		Assert.NotEqual(evt1.Type, evt2.Type);
	}

	#endregion
}
