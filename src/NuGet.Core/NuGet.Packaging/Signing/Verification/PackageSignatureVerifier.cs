// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;

namespace NuGet.Packaging.Signing
{
    /// <summary>
    /// Loads trust providers and verifies package signatures.
    /// </summary>
    public class PackageSignatureVerifier : IPackageSignatureVerifier
    {
        private readonly List<ISignatureVerificationProvider> _verificationProviders;
        private readonly SignedPackageVerifierSettings _settings;

        public PackageSignatureVerifier(IEnumerable<ISignatureVerificationProvider> verificationProviders, SignedPackageVerifierSettings settings)
        {
            _verificationProviders = verificationProviders?.ToList() ?? throw new ArgumentNullException(nameof(verificationProviders));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task<VerifySignaturesResult> VerifySignaturesAsync(ISignedPackageReader package, CancellationToken token)
        {
            var valid = false;
            var trustResults = new List<PackageVerificationResult>();

            var isSigned = await package.IsSignedAsync(token);
            if (isSigned)
            {
                try
                {
                    var signature = await package.GetSignatureAsync(token);

                    if (signature != null)
                    {
                        // Verify that the signature is trusted
                        var sigTrustResults = await Task.WhenAll(_verificationProviders.Select(e => e.GetTrustResultAsync(package, signature, _settings, token)));
                        valid = IsValid(sigTrustResults, _settings.AllowUntrusted);
                        trustResults.AddRange(sigTrustResults);
                    }
                    else
                    {
                        valid = false;
                    }
                }
                catch (SignatureException e)
                {
                    // SignatureException generated while parsing signatures
                    var issues = new[] {
                        SignatureLog.Issue(!_settings.AllowUntrusted, e.Code, e.Message),
                        SignatureLog.DebugLog(e.ToString())
                    };
                    trustResults.Add(new InvalidSignaturePackageVerificationResult(SignatureVerificationStatus.Untrusted, issues));
                    valid = _settings.AllowUntrusted;
                }
                catch (CryptographicException e)
                {
                    // CryptographicException generated while parsing the SignedCms object
                    var issues = new[] {
                        SignatureLog.Issue(!_settings.AllowUntrusted, NuGetLogCode.NU3003, Strings.ErrorPackageSignatureInvalid),
                        SignatureLog.DebugLog(e.ToString())
                    };
                    trustResults.Add(new InvalidSignaturePackageVerificationResult(SignatureVerificationStatus.Untrusted, issues));
                    valid = _settings.AllowUntrusted;
                }
            }
            else if (_settings.AllowUnsigned)
            {
                // An unsigned package is valid only if unsigned packages are allowed.
                valid = true;
            }
            else
            {
                var issues = new[] { SignatureLog.Issue(fatal: true, code: NuGetLogCode.NU3004, message: Strings.ErrorPackageNotSigned) };
                trustResults.Add(new UnsignedPackageVerificationResult(SignatureVerificationStatus.Invalid, issues));
                valid = false;
            }

            return new VerifySignaturesResult(valid, trustResults);
        }

        /// <summary>
        /// True if a provider trusts the package signature.
        /// </summary>
        private static bool IsValid(IEnumerable<PackageVerificationResult> trustResults, bool allowUntrusted)
        {
            var hasItems = trustResults.Any();
            var valid = trustResults.All(e => e.Trust == SignatureVerificationStatus.Trusted || (allowUntrusted && SignatureVerificationStatus.Untrusted == e.Trust));

            return valid && hasItems;
        }
    }
}