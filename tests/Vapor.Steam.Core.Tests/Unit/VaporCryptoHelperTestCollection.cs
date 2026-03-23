using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VaporCryptoHelperTestCollection
{
	public const string Name = "VaporCryptoHelper";
}
