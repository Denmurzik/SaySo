using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiErrorClassifierTests
{
    [Theory]
    [InlineData(1011, "You exceeded your current quota, please check your plan and billing details.", EngineErrorKind.QuotaExceeded)]
    [InlineData(1011, "[ORIGINAL ERROR] generic::resource_exhausted: RESOURCE_EXHAUSTED: Maximum concurrent sessions", EngineErrorKind.QuotaExceeded)]
    [InlineData(1008, "Your project has been denied access", EngineErrorKind.AuthFailed)]
    [InlineData(1008, "API key not valid. Please pass a valid API key.", EngineErrorKind.AuthFailed)]
    [InlineData(1008, "Your API key was reported as leaked.", EngineErrorKind.AuthFailed)]
    [InlineData(1008, "Method doesn't allow unregistered callers", EngineErrorKind.AuthFailed)]
    [InlineData(1008, "User location is not supported for the API use.", EngineErrorKind.RegionBlocked)]
    [InlineData(1008, "models/x is not found for API version v1beta, or is not supported for bidiGenerateContent", EngineErrorKind.ModelNotFound)]
    [InlineData(1007, "Request contains an invalid argument.", EngineErrorKind.Protocol)]
    [InlineData(1011, "The service is currently unavailable.", EngineErrorKind.Network)]
    [InlineData(1011, "Internal error occurred.", EngineErrorKind.Network)]
    [InlineData(null, null, EngineErrorKind.Network)]
    [InlineData(1006, "", EngineErrorKind.Network)]
    [InlineData(1008, "Operation is not implemented, or supported, or enabled.", EngineErrorKind.Protocol)]
    public void Classify(int? status, string? reason, EngineErrorKind expected) =>
        Assert.Equal(expected, GeminiErrorClassifier.Classify(status, reason));

    [Theory]
    [InlineData(401, EngineErrorKind.AuthFailed)]
    [InlineData(403, EngineErrorKind.AuthFailed)]
    [InlineData(429, EngineErrorKind.QuotaExceeded)]
    [InlineData(404, EngineErrorKind.ModelNotFound)]
    [InlineData(0, EngineErrorKind.Network)]
    [InlineData(502, EngineErrorKind.Network)]
    [InlineData(400, EngineErrorKind.Protocol)]
    public void ClassifyHandshake(int http, EngineErrorKind expected) =>
        Assert.Equal(expected, GeminiErrorClassifier.ClassifyHandshake(http));
}
