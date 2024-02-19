using System.Text;
using System.Text.Json;
using Bit.Core.Abstractions;
using Bit.Core.Enums;
using Bit.Core.Utilities;
using Bit.Core.Utilities.Fido2;

namespace Bit.Core.Services
{
    public class Fido2ClientService : IFido2ClientService
    {
        private readonly IStateService _stateService;
        private readonly IEnvironmentService _environmentService;
        private readonly ICryptoFunctionService _cryptoFunctionService;
        private readonly IFido2AuthenticatorService _fido2AuthenticatorService;

        public Fido2ClientService(
            IStateService stateService,
            IEnvironmentService environmentService,
            ICryptoFunctionService cryptoFunctionService,
            IFido2AuthenticatorService fido2AuthenticatorService)
        {
            _stateService = stateService;
            _environmentService = environmentService;
            _cryptoFunctionService = cryptoFunctionService;
            _fido2AuthenticatorService = fido2AuthenticatorService;
        }

        public async Task<Fido2ClientCreateCredentialResult> CreateCredentialAsync(Fido2ClientCreateCredentialParams createCredentialParams) 
        {
            var blockedUris = await _stateService.GetAutofillBlacklistedUrisAsync();
            var domain = CoreHelpers.GetHostname(createCredentialParams.Origin);
            if (blockedUris.Contains(domain))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.UriBlockedError,
                    "Origin is blocked by the user");
            }

            if (!await _stateService.IsAuthenticatedAsync())
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.InvalidStateError,
                    "No user is logged in");
            }

            if (createCredentialParams.Origin == _environmentService.GetWebVaultUrl())
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.NotAllowedError,
                    "Saving Bitwarden credentials in a Bitwarden vault is not allowed");
            }

            if (!createCredentialParams.SameOriginWithAncestors)
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.NotAllowedError,
                    "Credential creation is now allowed from embedded contexts with different origins");
            }

            if (createCredentialParams.User.Id.Length < 1 || createCredentialParams.User.Id.Length > 64)
            {
                // TODO: Should we use ArgumentException here instead?
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.TypeError,
                    "The length of user.id is not between 1 and 64 bytes (inclusive)");
            }

            if (!createCredentialParams.Origin.StartsWith("https://"))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.SecurityError,
                    "Origin is not a valid https origin");
            }

            if (!Fido2DomainUtils.IsValidRpId(createCredentialParams.Rp.Id, createCredentialParams.Origin))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.SecurityError,
                    "RP ID cannot be used with this origin");
            }

            PublicKeyCredentialParameters[] credTypesAndPubKeyAlgs;
            if (createCredentialParams.PubKeyCredParams?.Length > 0)
            {
                // Filter out all unsupported algorithms
                credTypesAndPubKeyAlgs = createCredentialParams.PubKeyCredParams
                    .Where(kp => kp.Alg == -7 && kp.Type == "public-key")
                    .ToArray();
            }
            else
            {
                // Assign default algorithms
                credTypesAndPubKeyAlgs = [
                    new PublicKeyCredentialParameters { Alg = -7, Type = "public-key" },
                    new PublicKeyCredentialParameters { Alg = -257, Type = "public-key" }
                ];
            }

            if (credTypesAndPubKeyAlgs.Length == 0)
            {
                throw new Fido2ClientException(Fido2ClientException.ErrorCode.NotSupportedError, "No supported algorithms found");
            }

            var clientDataJSON = JsonSerializer.Serialize(new {
                type = "webauthn.create",
                challenge = CoreHelpers.Base64UrlEncode(createCredentialParams.Challenge),
                origin = createCredentialParams.Origin,
                crossOrigin = !createCredentialParams.SameOriginWithAncestors,
                // tokenBinding: {} // Not supported
            });
            var clientDataJSONBytes = Encoding.UTF8.GetBytes(clientDataJSON);
            var clientDataHash = await _cryptoFunctionService.HashAsync(clientDataJSONBytes, CryptoHashAlgorithm.Sha256);
            var makeCredentialParams = MapToMakeCredentialParams(createCredentialParams, credTypesAndPubKeyAlgs, clientDataHash);

            try {
                var makeCredentialResult = await _fido2AuthenticatorService.MakeCredentialAsync(makeCredentialParams);

                return new Fido2ClientCreateCredentialResult {
                    CredentialId = makeCredentialResult.CredentialId,
                    AttestationObject = makeCredentialResult.AttestationObject,
                    AuthData = makeCredentialResult.AuthData,
                    ClientDataJSON = clientDataJSONBytes,
                    PublicKey = makeCredentialResult.PublicKey,
                    PublicKeyAlgorithm = makeCredentialResult.PublicKeyAlgorithm,
                    Transports = createCredentialParams.Rp.Id == "google.com" ? ["internal", "usb"] : ["internal"] // workaround for a bug on Google's side
                };
            } catch (InvalidStateError) {
                throw new Fido2ClientException(Fido2ClientException.ErrorCode.InvalidStateError, "Unknown invalid state encountered");
            } catch (Exception) {
                throw new Fido2ClientException(Fido2ClientException.ErrorCode.UnknownError, $"An unknown error occurred");
            }
        }

        public async Task<Fido2ClientAssertCredentialResult> AssertCredentialAsync(Fido2ClientAssertCredentialParams assertCredentialParams)
        {
            var blockedUris = await _stateService.GetAutofillBlacklistedUrisAsync();
            var domain = CoreHelpers.GetHostname(assertCredentialParams.Origin);
            if (blockedUris.Contains(domain))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.UriBlockedError,
                    "Origin is blocked by the user");
            }

            if (!await _stateService.IsAuthenticatedAsync())
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.InvalidStateError,
                    "No user is logged in");
            }

            if (assertCredentialParams.Origin == _environmentService.GetWebVaultUrl())
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.NotAllowedError,
                    "Saving Bitwarden credentials in a Bitwarden vault is not allowed");
            }

            if (!assertCredentialParams.Origin.StartsWith("https://"))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.SecurityError,
                    "Origin is not a valid https origin");
            }

            if (!Fido2DomainUtils.IsValidRpId(assertCredentialParams.RpId, assertCredentialParams.Origin))
            {
                throw new Fido2ClientException(
                    Fido2ClientException.ErrorCode.SecurityError,
                    "RP ID cannot be used with this origin");
            }

            var clientDataJSON = JsonSerializer.Serialize(new {
                type = "webauthn.get",
                challenge = CoreHelpers.Base64UrlEncode(assertCredentialParams.Challenge),
                origin = assertCredentialParams.Origin,
                crossOrigin = !assertCredentialParams.SameOriginWithAncestors,
            });
            var clientDataJSONBytes = Encoding.UTF8.GetBytes(clientDataJSON);
            var clientDataHash = await _cryptoFunctionService.HashAsync(clientDataJSONBytes, CryptoHashAlgorithm.Sha256);
            var getAssertionParams = MapToGetAssertionParams(assertCredentialParams, clientDataHash);

            try {
                var getAssertionResult = await _fido2AuthenticatorService.GetAssertionAsync(getAssertionParams);

                return new Fido2ClientAssertCredentialResult {
                    AuthenticatorData = getAssertionResult.AuthenticatorData,
                    ClientDataJSON = clientDataJSONBytes,
                    Id = CoreHelpers.Base64UrlEncode(getAssertionResult.SelectedCredential.Id),
                    RawId = getAssertionResult.SelectedCredential.Id,
                    Signature = getAssertionResult.Signature,
                    UserHandle = getAssertionResult.SelectedCredential.UserHandle
                };
            } catch (InvalidStateError) {
                throw new Fido2ClientException(Fido2ClientException.ErrorCode.InvalidStateError, "Unknown invalid state encountered");
            } catch (Exception) {
                throw new Fido2ClientException(Fido2ClientException.ErrorCode.UnknownError, $"An unknown error occurred");
            }

            throw new NotImplementedException();
        }

        private Fido2AuthenticatorMakeCredentialParams MapToMakeCredentialParams(
            Fido2ClientCreateCredentialParams createCredentialParams,
            PublicKeyCredentialParameters[] credTypesAndPubKeyAlgs,
            byte[] clientDataHash)
        {
            var requireResidentKey = createCredentialParams.AuthenticatorSelection?.ResidentKey == "required" ||
                createCredentialParams.AuthenticatorSelection?.ResidentKey == "preferred" ||
                (createCredentialParams.AuthenticatorSelection?.ResidentKey == null &&
                createCredentialParams.AuthenticatorSelection?.RequireResidentKey == true);
            
            var requireUserVerification = createCredentialParams.AuthenticatorSelection?.UserVerification == "required" ||
                createCredentialParams.AuthenticatorSelection?.UserVerification == "preferred" ||
                createCredentialParams.AuthenticatorSelection?.UserVerification == null;

            return new Fido2AuthenticatorMakeCredentialParams {
                RequireResidentKey = requireResidentKey,
                RequireUserVerification = requireUserVerification,
                ExcludeCredentialDescriptorList = createCredentialParams.ExcludeCredentials,
                CredTypesAndPubKeyAlgs = credTypesAndPubKeyAlgs,
                Hash = clientDataHash,
                RpEntity = createCredentialParams.Rp,
                UserEntity = createCredentialParams.User,
                Extensions = createCredentialParams.Extensions
            };
        }

        private Fido2AuthenticatorGetAssertionParams MapToGetAssertionParams(
            Fido2ClientAssertCredentialParams assertCredentialParams,
            byte[] cliendDataHash)
        {
            var requireUserVerification = assertCredentialParams.UserVerification == "required" ||
                assertCredentialParams.UserVerification == "preferred" ||
                assertCredentialParams.UserVerification == null;

            return new Fido2AuthenticatorGetAssertionParams {
                RpId = assertCredentialParams.RpId,
                Challenge = assertCredentialParams.Challenge,
                AllowCredentialDescriptorList = assertCredentialParams.AllowCredentials,
                RequireUserPresence = true,
                RequireUserVerification = requireUserVerification,
                Hash = cliendDataHash
            };
        }
    }
}