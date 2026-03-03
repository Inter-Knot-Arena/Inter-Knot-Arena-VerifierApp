using System.Security.Cryptography;
using System.Text;
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
        var payload = string.Join(
            ":",
            submission.MatchId,
            submission.UserId,
            submission.Type,
            submission.Detection.Result,
            submission.Detection.FrameHash,
            submission.VerifierNonce
        );
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(submission.VerifierSessionToken));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
