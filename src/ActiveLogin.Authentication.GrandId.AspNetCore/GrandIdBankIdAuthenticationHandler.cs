﻿using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using ActiveLogin.Authentication.Common.Serialization;
using ActiveLogin.Authentication.GrandId.Api;
using ActiveLogin.Authentication.GrandId.Api.Models;
using ActiveLogin.Authentication.GrandId.AspNetCore.Models;
using ActiveLogin.Identity.Swedish;
using ActiveLogin.Identity.Swedish.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveLogin.Authentication.GrandId.AspNetCore
{
    public class GrandIdBankIdAuthenticationHandler : GrandIdAuthenticationHandler<GrandIdBankIdAuthenticationOptions, BankIdGetSessionResponse>
    {
        private readonly ILogger<GrandIdBankIdAuthenticationHandler> _logger;
        private readonly IGrandIdApiClient _grandIdApiClient;

        public GrandIdBankIdAuthenticationHandler(
            IOptionsMonitor<GrandIdBankIdAuthenticationOptions> options,
            ILoggerFactory loggerFactory,
            UrlEncoder encoder,
            ISystemClock clock,
            ILogger<GrandIdBankIdAuthenticationHandler> logger,
            IGrandIdApiClient grandIdApiClient
            )
            : base(options, loggerFactory, encoder, clock)
        {
            _logger = logger;
            _grandIdApiClient = grandIdApiClient;
        }

        protected override async Task<string> GetRedirectUrlAsync(AuthenticationProperties properties, string absoluteReturnUrl)
        {
            var swedishPersonalIdentityNumber = GetSwedishPersonalIdentityNumber(properties);
            var request = GetBankIdFederatedLoginRequest(absoluteReturnUrl, Options, swedishPersonalIdentityNumber);

            try
            {
                var federatedLoginResponse = await _grandIdApiClient.BankIdFederatedLoginAsync(request);
                _logger.GrandIdBankIdFederatedLoginSuccess(absoluteReturnUrl, federatedLoginResponse.SessionId);
                return federatedLoginResponse.RedirectUrl;
            }
            catch (Exception ex)
            {
                _logger.GrandIdBankIdFederatedLoginFailure(absoluteReturnUrl, ex);
                throw;
            }
        }

        private static BankIdFederatedLoginRequest GetBankIdFederatedLoginRequest(string callbackUrl, GrandIdBankIdAuthenticationOptions options, SwedishPersonalIdentityNumber swedishPersonalIdentityNumber)
        {
            bool? useChooseDevice;
            bool? useSameDevice;

            switch (options.GrandIdBankIdMode)
            {
                case GrandIdBankIdMode.ChooseDevice:
                    useChooseDevice = true;
                    useSameDevice = false;
                    break;
                case GrandIdBankIdMode.SameDevice:
                    useChooseDevice = false;
                    useSameDevice = true;
                    break;
                case GrandIdBankIdMode.OtherDevice:
                    useChooseDevice = false;
                    useSameDevice = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var personalIdentityNumber = swedishPersonalIdentityNumber?.To12DigitString();

            return new BankIdFederatedLoginRequest(
                callbackUrl,
                useChooseDevice: useChooseDevice,
                useSameDevice: useSameDevice,
                askForPersonalIdentityNumber: null,
                personalIdentityNumber: personalIdentityNumber,
                requireMobileBankId: options.RequireMobileBankId
            );
        }

        private SwedishPersonalIdentityNumber GetSwedishPersonalIdentityNumber(AuthenticationProperties properties)
        {
            if (properties.Items.TryGetValue(GrandIdAuthenticationConstants.AuthenticationPropertyItemSwedishPersonalIdentityNumber, out var swedishPersonalIdentityNumber))
            {
                if (!string.IsNullOrWhiteSpace(swedishPersonalIdentityNumber))
                {
                    return SwedishPersonalIdentityNumber.Parse(swedishPersonalIdentityNumber);
                }
            }

            return null;
        }

        protected override async Task<BankIdGetSessionResponse> GetSessionResponseAsync(string sessionId)
        {
            try
            {
                var sessionResponse = await _grandIdApiClient.BankIdGetSessionAsync(sessionId);
                _logger.GrandIdBankIdGetSessionSuccess(sessionResponse.SessionId);
                return sessionResponse;
            }
            catch (Exception ex)
            {
                _logger.GrandIdBankIdGetSessionFailure(sessionId, ex);
                throw;
            }
        }

        protected override IEnumerable<Claim> GetClaims(BankIdGetSessionResponse loginResult)
        {
            var personalIdentityNumber = SwedishPersonalIdentityNumber.Parse(loginResult.UserAttributes.PersonalIdentityNumber);
            var claims = new List<Claim>
            {
                new Claim(GrandIdClaimTypes.Subject, personalIdentityNumber.To12DigitString()),

                new Claim(GrandIdClaimTypes.Name, loginResult.UserAttributes.Name),
                new Claim(GrandIdClaimTypes.FamilyName, loginResult.UserAttributes.Surname),
                new Claim(GrandIdClaimTypes.GivenName, loginResult.UserAttributes.GivenName),

                new Claim(GrandIdClaimTypes.SwedishPersonalIdentityNumber, personalIdentityNumber.To10DigitString())
            };

            if (Options.IssueGenderClaim)
            {
                var jwtGender = JwtSerializer.GetGender(personalIdentityNumber.GetGenderHint());
                if (!string.IsNullOrEmpty(jwtGender))
                {
                    claims.Add(new Claim(GrandIdClaimTypes.Gender, jwtGender));
                }
            }

            if (Options.IssueBirthdateClaim)
            {
                var jwtBirthdate = JwtSerializer.GetBirthdate(personalIdentityNumber.GetDateOfBirthHint());
                claims.Add(new Claim(GrandIdClaimTypes.Birthdate, jwtBirthdate));
            }

            return claims;
        }
    }
}