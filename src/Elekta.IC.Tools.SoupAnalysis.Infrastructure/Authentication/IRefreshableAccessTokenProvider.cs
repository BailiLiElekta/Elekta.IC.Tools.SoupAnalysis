using Elekta.IC.Tools.SoupAnalysis.Application;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Authentication;

public interface IRefreshableAccessTokenProvider : IAccessTokenProvider
{
    Task<AccessToken> RefreshTokenAsync(CancellationToken cancellationToken);
}