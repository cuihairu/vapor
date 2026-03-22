using SteamKit2;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Tests.Mocks;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.Steam.Core.Tests.RealSteam;

/// <summary>
/// Tests for the RedeemKey flow.
/// Tests requiring real Steam connections are skipped by default.
/// </summary>
[Trait("Category", "RealSteam")]
public sealed class RedeemKeyFlowTests
{
	private readonly ITestOutputHelper _output;

	public RedeemKeyFlowTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task RedeemKeyAction_Execute_IncludesRequestIdAndDuration()
	{
		// Unit test that doesn't require Steam connection
		var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RedeemKeyAction>.Instance;
		var action = new RedeemKeyAction(logger);

		// Create a mock session
		var mockManager = new MockSteamClientManager();

		// Mock a successful redemption
		mockManager.SetRedeemKeyResult(new RedeemKeyResult(
			EResult.OK,
			RequestId: "test-request-123",
			DurationMs: 1500,
			GrantedAppIDs: new List<uint> { 730, 440 },
			GrantedPackageIDs: null,
			ReceiptDetails: "Test receipt"
		));

		var session = TestHelpers.CreateMockSession(mockManager);

		// Act
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { { "key", "AAAA-BBBB-CCCC" } },
			CancellationToken.None
		);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);

		if (result.Output is not null)
		{
			Assert.True(result.Output.ContainsKey("requestId"));
			Assert.Equal("test-request-123", result.Output["requestId"]);
			Assert.True(result.Output.ContainsKey("durationMs"));
			Assert.Equal(1500L, result.Output["durationMs"]);
		}

		_output.WriteLine($"Action output: {System.Text.Json.JsonSerializer.Serialize(result.Output)}");
	}

	[Fact]
	public void ValueStopwatch_MeasureElapsed_ReturnsCorrectDuration()
	{
		// Arrange
		var stopwatch = Vapor.Steam.Core.Utilities.ValueStopwatch.StartNew();

		// Act - wait a bit
		Thread.Sleep(100);

		// Assert
		var elapsed = stopwatch.ElapsedMilliseconds;
		_output.WriteLine($"Elapsed milliseconds: {elapsed}");

		Assert.True(elapsed >= 90, $"Expected at least 90ms, got {elapsed}ms");
		Assert.True(elapsed < 500, $"Expected less than 500ms, got {elapsed}ms");
	}
}
