using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public static class VerifierSignatureService
{
    public static string CreateNonce()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return Convert.ToHexString(random).ToLowerInvariant();
    }

    public static string BuildEvidenceSignature(EvidenceSubmission submission)
    {
        var confidence = submission.Detection.Confidence
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var payload = JsonSerializer.Serialize(
            new
            {
                matchId = submission.MatchId,
                userId = submission.UserId,
                type = submission.Type,
                result = submission.Detection.Result,
                frameHash = submission.Detection.FrameHash ?? string.Empty,
                detectedAgents = submission.Detection.DetectedAgents,
                confidence,
                nonce = submission.VerifierNonce
            }
        );
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(submission.VerifierSessionToken));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
