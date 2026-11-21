namespace Vapor.ControlPlane;

/// <summary>Registers the account action routes.</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountActionEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapAccountTradeOfferEndpoints(auditLogger);
		app.MapAccountAchievementEndpoints(auditLogger);
		app.MapAccountInventoryEndpoints(auditLogger);
		app.MapAccountMarketEndpoints(auditLogger);
		app.MapAccountPointsShopEndpoints(auditLogger);
		app.MapAccountConfirmationEndpoints(auditLogger);
		app.MapAccountProxyEndpoints(auditLogger);
		app.MapAccountItemCollectionEndpoints(auditLogger);
		app.MapAccountLicenseEndpoints(auditLogger);
	}
}
