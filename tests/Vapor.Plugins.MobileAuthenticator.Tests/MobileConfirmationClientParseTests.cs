using Xunit;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class MobileConfirmationClientParseTests
{
	[Fact]
	public void ParseConfirmationsList_ParsesEntries()
	{
		var body = """
			{
				"success": true,
				"conf": [
					{
						"type": 2,
						"type_name": "Trade Offer",
						"id": "1111111111",
						"creator_id": "2222222222",
						"nonce": "3333333333",
						"creation_time": 1610000000,
						"headline": "Trade with testuser",
						"summary": "You will give: 1 item"
					},
					{
						"type": 3,
						"type_name": "Market Listing",
						"id": 4444444444,
						"creator_id": 5555555555,
						"nonce": 6666666666,
						"headline": "Sell item",
						"summary": null
					}
				]
			}
			""";

		var result = MobileConfirmationClient.ParseConfirmationsList(body);

		Assert.True(result.Success);
		Assert.Equal(2, result.Confirmations!.Count);

		var first = result.Confirmations[0];
		Assert.Equal(1111111111UL, first.Id);
		Assert.Equal(3333333333UL, first.Nonce);
		Assert.Equal(2222222222UL, first.CreatorId);
		Assert.Equal("Trade with testuser", first.Headline);
		Assert.Equal("trade", first.Type);

		// Numeric ids are accepted as well.
		Assert.Equal(4444444444UL, result.Confirmations[1].Id);
		Assert.Equal("market", result.Confirmations[1].Type);
	}

	[Fact]
	public void ParseConfirmationsList_NormalizesTypeCodes()
	{
		var body = """
			{ "success": true, "conf": [
				{ "id": "1", "nonce": "1", "type": 1 },
				{ "id": "2", "nonce": "2", "type": 42 },
				{ "id": "3", "nonce": "3", "type": "Market" }
			] }
			""";

		var result = MobileConfirmationClient.ParseConfirmationsList(body);

		Assert.True(result.Success);
		Assert.Equal(3, result.Confirmations!.Count);
		Assert.Equal("generic", result.Confirmations[0].Type);
		Assert.Equal("42", result.Confirmations[1].Type);
		Assert.Equal("market", result.Confirmations[2].Type);
	}

	[Fact]
	public void ParseConfirmationsList_MissingType_YieldsNull()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList(
			"{ \"success\": true, \"conf\": [ { \"id\": \"1\", \"nonce\": \"2\" } ] }");

		Assert.True(result.Success);
		Assert.Null(result.Confirmations!.Single().Type);
	}

	[Fact]
	public void ParseConfirmationsList_EmptyList()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList("{\"success\":true,\"conf\":[]}");

		Assert.True(result.Success);
		Assert.Empty(result.Confirmations!);
	}

	[Fact]
	public void ParseConfirmationsList_SkipsEntriesMissingIds()
	{
		var body = """
			{ "success": true, "conf": [ { "id": "1" }, { "nonce": "2" }, { "id": "3", "nonce": "4" } ] }
			""";

		var result = MobileConfirmationClient.ParseConfirmationsList(body);

		Assert.True(result.Success);
		var confirmation = Assert.Single(result.Confirmations!);
		Assert.Equal(3UL, confirmation.Id);
		Assert.Equal(4UL, confirmation.Nonce);
	}

	[Fact]
	public void ParseConfirmationsList_FailureWithMessage()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList("{\"success\":false,\"message\":\"Invalid authenticator\"}");

		Assert.False(result.Success);
		Assert.Equal("Invalid authenticator", result.Error);
	}

	[Fact]
	public void ParseConfirmationsList_InvalidJson()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList("not json");

		Assert.False(result.Success);
		Assert.Contains("parse", result.Error);
	}

	[Fact]
	public void ParseOperationResult_Success()
	{
		var result = MobileConfirmationClient.ParseOperationResult("{\"success\":true}");

		Assert.True(result.Success);
	}

	[Fact]
	public void ParseOperationResult_FailureWithMessage()
	{
		var result = MobileConfirmationClient.ParseOperationResult("{\"success\":false,\"message\":\"Oh no\"}");

		Assert.False(result.Success);
		Assert.Equal("Oh no", result.Error);
	}

	[Fact]
	public void ParseOperationResult_InvalidJson()
	{
		var result = MobileConfirmationClient.ParseOperationResult("<html>error</html>");

		Assert.False(result.Success);
	}
}
