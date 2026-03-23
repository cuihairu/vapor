using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

[Collection(VaporCryptoHelperTestCollection.Name)]
public sealed class VaporCryptoHelperTests : IDisposable
{
	public VaporCryptoHelperTests()
	{
		VaporCryptoHelper.ResetForTests();
	}

	[Fact]
	public void EnsureSafeForEnvironment_WithDefaultKeyInProduction_Throws()
	{
		var environment = new Dictionary<string, string?>
		{
			["DOTNET_ENVIRONMENT"] = "Production"
		};

		Assert.Throws<InvalidOperationException>(
			() => VaporCryptoHelper.EnsureSafeForEnvironment(key => environment.TryGetValue(key, out var value) ? value : null));
	}

	[Fact]
	public void ConfigureFromEnvironment_WithCustomKey_DisablesDefaultKey()
	{
		var environment = new Dictionary<string, string?>
		{
			["VAPOR_ENCRYPTION_KEY"] = new string('K', 32)
		};

		VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

		Assert.False(VaporCryptoHelper.HasDefaultKey);
	}

	[Fact]
	public void EnsureSafeForEnvironment_WithCustomKeyInProduction_DoesNotThrow()
	{
		var environment = new Dictionary<string, string?>
		{
			["DOTNET_ENVIRONMENT"] = "Production",
			["VAPOR_ENCRYPTION_KEY"] = new string('K', 32)
		};

		VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);
		VaporCryptoHelper.EnsureSafeForEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

		Assert.False(VaporCryptoHelper.HasDefaultKey);
	}

	[Fact]
	public void EnsureSafeForEnvironment_WithExplicitUnsafeOverride_DoesNotThrow()
	{
		var environment = new Dictionary<string, string?>
		{
			["DOTNET_ENVIRONMENT"] = "Production",
			["VAPOR_ALLOW_INSECURE_DEFAULT_KEY"] = "true"
		};

		VaporCryptoHelper.EnsureSafeForEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

		Assert.True(VaporCryptoHelper.HasDefaultKey);
	}

	public void Dispose()
	{
		VaporCryptoHelper.ResetForTests();
	}
}
