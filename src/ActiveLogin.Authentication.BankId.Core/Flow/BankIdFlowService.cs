using ActiveLogin.Authentication.BankId.Api;
using ActiveLogin.Authentication.BankId.Api.Models;
using ActiveLogin.Authentication.BankId.Api.UserMessage;
using ActiveLogin.Authentication.BankId.Core.CertificatePolicies;
using ActiveLogin.Authentication.BankId.Core.Events;
using ActiveLogin.Authentication.BankId.Core.Events.Infrastructure;
using ActiveLogin.Authentication.BankId.Core.Launcher;
using ActiveLogin.Authentication.BankId.Core.Models;
using ActiveLogin.Authentication.BankId.Core.Qr;
using ActiveLogin.Authentication.BankId.Core.SupportedDevice;
using ActiveLogin.Authentication.BankId.Core.UserContext;
using ActiveLogin.Authentication.BankId.Core.UserData;
using ActiveLogin.Authentication.BankId.Core.UserMessage;

namespace ActiveLogin.Authentication.BankId.Core.Flow;

public class BankIdFlowService : IBankIdFlowService
{
    private const int MaxRetryLoginAttempts = 5;

    private readonly IBankIdAppApiClient _bankIdAppApiClient;
    private readonly IBankIdFlowSystemClock _bankIdFlowSystemClock;
    private readonly IBankIdEventTrigger _bankIdEventTrigger;
    private readonly IBankIdUserMessage _bankIdUserMessage;
    private readonly IBankIdUserMessageLocalizer _bankIdUserMessageLocalizer;
    private readonly IBankIdSupportedDeviceDetector _bankIdSupportedDeviceDetector;
    private readonly IBankIdEndUserIpResolver _bankIdEndUserIpResolver;
    private readonly IBankIdAuthRequestUserDataResolver _bankIdAuthUserDataResolver;
    private readonly IBankIdQrCodeGenerator _bankIdQrCodeGenerator;
    private readonly IBankIdLauncher _bankIdLauncher;
    private readonly IBankIdCertificatePolicyResolver _bankIdCertificatePolicyResolver;

    public BankIdFlowService(
        IBankIdAppApiClient bankIdAppApiClient,
        IBankIdFlowSystemClock bankIdFlowSystemClock,
        IBankIdEventTrigger bankIdEventTrigger,
        IBankIdUserMessage bankIdUserMessage,
        IBankIdUserMessageLocalizer bankIdUserMessageLocalizer,
        IBankIdSupportedDeviceDetector bankIdSupportedDeviceDetector,
        IBankIdEndUserIpResolver bankIdEndUserIpResolver,
        IBankIdAuthRequestUserDataResolver bankIdAuthUserDataResolver,
        IBankIdQrCodeGenerator bankIdQrCodeGenerator,
        IBankIdLauncher bankIdLauncher,
        IBankIdCertificatePolicyResolver bankIdCertificatePolicyResolver
    )
    {
        _bankIdAppApiClient = bankIdAppApiClient;
        _bankIdFlowSystemClock = bankIdFlowSystemClock;
        _bankIdEventTrigger = bankIdEventTrigger;
        _bankIdUserMessage = bankIdUserMessage;
        _bankIdUserMessageLocalizer = bankIdUserMessageLocalizer;
        _bankIdSupportedDeviceDetector = bankIdSupportedDeviceDetector;
        _bankIdEndUserIpResolver = bankIdEndUserIpResolver;
        _bankIdAuthUserDataResolver = bankIdAuthUserDataResolver;
        _bankIdQrCodeGenerator = bankIdQrCodeGenerator;
        _bankIdLauncher = bankIdLauncher;
        _bankIdCertificatePolicyResolver = bankIdCertificatePolicyResolver;
    }

    public async Task<BankIdFlowInitializeResult> InitializeAuth(BankIdFlowOptions flowOptions, string returnRedirectUrl)
    {
        var detectedUserDevice = _bankIdSupportedDeviceDetector.Detect();
        var response = await GetAuthResponse(flowOptions, detectedUserDevice);

        await _bankIdEventTrigger.TriggerAsync(new BankIdInitializeSuccessEvent(personalIdentityNumber: null, response.OrderRef, detectedUserDevice, flowOptions));

        if (flowOptions.SameDevice)
        {
            var launchInfo = await GetBankIdLaunchInfo(returnRedirectUrl, response.AutoStartToken);
            return new BankIdFlowInitializeResult(response, detectedUserDevice, new BankIdFlowInitializeLaunchTypeSameDevice(launchInfo));
        }
        else
        {
            var qrStartState = new BankIdQrStartState(
                _bankIdFlowSystemClock.UtcNow,
                response.QrStartToken,
                response.QrStartSecret
            );

            var qrCodeAsBase64 = GetQrCodeAsBase64(qrStartState);
            return new BankIdFlowInitializeResult(response, detectedUserDevice, new BankIdFlowInitializeLaunchTypeOtherDevice(qrStartState, qrCodeAsBase64));
        }
    }

    private async Task<AuthResponse> GetAuthResponse(BankIdFlowOptions flowOptions, BankIdSupportedDevice detectedUserDevice)
    {
        try
        {
            var request = await GetAuthRequest(flowOptions);
            return await _bankIdAppApiClient.AuthAsync(request);
        }
        catch (BankIdApiException bankIdApiException)
        {
            await _bankIdEventTrigger.TriggerAsync(new BankIdInitializeErrorEvent(personalIdentityNumber: null, bankIdApiException, detectedUserDevice, flowOptions));
            throw;
        }
    }

    private async Task<AuthRequest> GetAuthRequest(BankIdFlowOptions flowOptions)
    {
        var endUserIp = _bankIdEndUserIpResolver.GetEndUserIp();
        var resolvedCertificatePolicies = GetResolvedCertificatePolicies(flowOptions);
        var resolvedRiskLevel = flowOptions.AllowedRiskLevel == Risk.BankIdAllowedRiskLevel.NoRiskLevel ? null : flowOptions.AllowedRiskLevel.ToString().ToLower();

        var authRequestRequirement = new Requirement(resolvedCertificatePolicies, resolvedRiskLevel, flowOptions.RequirePinCode, flowOptions.RequireMrtd);

        var authRequestContext = new BankIdAuthRequestContext(endUserIp, authRequestRequirement);
        var userData = await _bankIdAuthUserDataResolver.GetUserDataAsync(authRequestContext);

        return new AuthRequest(
            endUserIp,
            authRequestRequirement,
            userData.UserVisibleData,
            userData.UserNonVisibleData,
            userData.UserVisibleDataFormat
        );
    }
    
    public async Task<BankIdFlowInitializeResult> InitializeSign(BankIdFlowOptions flowOptions, BankIdSignData bankIdSignData, string returnRedirectUrl)
    {
        var detectedUserDevice = _bankIdSupportedDeviceDetector.Detect();
        var response = await GetSignResponse(flowOptions, bankIdSignData, detectedUserDevice);

        await _bankIdEventTrigger.TriggerAsync(new BankIdInitializeSuccessEvent(personalIdentityNumber: null, response.OrderRef, detectedUserDevice, flowOptions));

        if (flowOptions.SameDevice)
        {
            var launchInfo = await GetBankIdLaunchInfo(returnRedirectUrl, response.AutoStartToken);
            return new BankIdFlowInitializeResult(response, detectedUserDevice, new BankIdFlowInitializeLaunchTypeSameDevice(launchInfo));
        }
        else
        {
            var qrStartState = new BankIdQrStartState(
                _bankIdFlowSystemClock.UtcNow,
                response.QrStartToken,
                response.QrStartSecret
            );

            var qrCodeAsBase64 = GetQrCodeAsBase64(qrStartState);
            return new BankIdFlowInitializeResult(response, detectedUserDevice, new BankIdFlowInitializeLaunchTypeOtherDevice(qrStartState, qrCodeAsBase64));
        }
    }

    private async Task<SignResponse> GetSignResponse(BankIdFlowOptions flowOptions, BankIdSignData bankIdSignData, BankIdSupportedDevice detectedUserDevice)
    {
        try
        {
            var request = GetSignRequest(flowOptions, bankIdSignData);
            return await _bankIdAppApiClient.SignAsync(request);
        }
        catch (BankIdApiException bankIdApiException)
        {
            await _bankIdEventTrigger.TriggerAsync(new BankIdInitializeErrorEvent(personalIdentityNumber: null, bankIdApiException, detectedUserDevice, flowOptions));
            throw;
        }
    }

    private SignRequest GetSignRequest(BankIdFlowOptions flowOptions, BankIdSignData bankIdSignData)
    {
        var endUserIp = _bankIdEndUserIpResolver.GetEndUserIp();
        var resolvedCertificatePolicies = GetResolvedCertificatePolicies(flowOptions);
        var resolvedRiskLevel = flowOptions.AllowedRiskLevel.ToString().ToLower();

        var requestRequirement = new Requirement(resolvedCertificatePolicies, resolvedRiskLevel, flowOptions.RequirePinCode, flowOptions.RequireMrtd, flowOptions.RequiredPersonalIdentityNumber?.To12DigitString());

        return new SignRequest(
            endUserIp,
            bankIdSignData.UserVisibleData,
            userNonVisibleData: bankIdSignData.UserNonVisibleData,
            userVisibleDataFormat: bankIdSignData.UserVisibleDataFormat,
            requirement: requestRequirement
        );
    }

    private List<string>? GetResolvedCertificatePolicies(BankIdFlowOptions flowOptions)
    {
        var certificatePolicies = flowOptions.CertificatePolicies;
        if (!certificatePolicies.Any())
        {
            if (!flowOptions.SameDevice)
            {
                // Enforce mobile bank id for other device if no other policy is set
                certificatePolicies = [BankIdCertificatePolicy.MobileBankId];
            }
            else
            {
                return null;
            }
        }

        return certificatePolicies.Select(x => _bankIdCertificatePolicyResolver.Resolve(x)).ToList();
    }

    private Task<BankIdLaunchInfo> GetBankIdLaunchInfo(string redirectUrl, string autoStartToken)
    {
        var launchUrlRequest = new LaunchUrlRequest(redirectUrl, autoStartToken);

        return _bankIdLauncher.GetLaunchInfoAsync(launchUrlRequest);
    }

    public async Task<BankIdFlowCollectResult> Collect(string orderRef, int autoStartAttempts, BankIdFlowOptions flowOptions)
    {
        var detectedUserDevice = _bankIdSupportedDeviceDetector.Detect();

        var collectResponse = await GetCollectResponse(orderRef, flowOptions, detectedUserDevice);
        var statusMessage = GetStatusMessage(collectResponse, flowOptions, detectedUserDevice);

        var collectStatus = collectResponse.GetCollectStatus();
        switch (collectStatus)
        {
            case CollectStatus.Pending:
            {
                await _bankIdEventTrigger.TriggerAsync(new BankIdCollectPendingEvent(collectResponse.OrderRef, collectResponse.GetCollectHintCode(), detectedUserDevice, flowOptions));
                return new BankIdFlowCollectResultPending(statusMessage);
            }
            case CollectStatus.Complete:
            {
                if (collectResponse.CompletionData == null)
                {
                    throw new InvalidOperationException("Missing CompletionData from BankID API");
                }
                
                await _bankIdEventTrigger.TriggerAsync(new BankIdCollectCompletedEvent(collectResponse.OrderRef, collectResponse.CompletionData, detectedUserDevice, flowOptions));
                return new BankIdFlowCollectResultComplete(collectResponse.CompletionData);
            }
            case CollectStatus.Failed:
            {
                var hintCode = collectResponse.GetCollectHintCode();
                if (hintCode.Equals(CollectHintCode.StartFailed) && autoStartAttempts < MaxRetryLoginAttempts)
                {
                    return new BankIdFlowCollectResultRetry(statusMessage);
                }

                await _bankIdEventTrigger.TriggerAsync(new BankIdCollectFailureEvent(collectResponse.OrderRef, collectResponse.GetCollectHintCode(), detectedUserDevice, flowOptions));
                return new BankIdFlowCollectResultFailure(statusMessage);
            }
            default:
            {
                await _bankIdEventTrigger.TriggerAsync(new BankIdCollectFailureEvent(collectResponse.OrderRef, collectResponse.GetCollectHintCode(), detectedUserDevice, flowOptions));
                return new BankIdFlowCollectResultFailure(statusMessage);
            }
        }
    }

    private async Task<CollectResponse> GetCollectResponse(string orderRef, BankIdFlowOptions flowOptions, BankIdSupportedDevice detectedUserDevice)
    {
        try
        {
            return await _bankIdAppApiClient.CollectAsync(orderRef);
        }
        catch (BankIdApiException bankIdApiException)
        {
            await _bankIdEventTrigger.TriggerAsync(new BankIdCollectErrorEvent(orderRef, bankIdApiException, detectedUserDevice, flowOptions));
            throw;
        }
    }

    private string GetStatusMessage(CollectResponse collectResponse, BankIdFlowOptions unprotectedFlowOptions, BankIdSupportedDevice detectedDevice)
    {
        var accessedFromMobileDevice = detectedDevice.DeviceType == BankIdSupportedDeviceType.Mobile;
        var usingQrCode = !unprotectedFlowOptions.SameDevice;

        var messageShortName = _bankIdUserMessage.GetMessageShortNameForCollectResponse(
            collectResponse.GetCollectStatus(),
            collectResponse.GetCollectHintCode(),
            accessedFromMobileDevice,
            usingQrCode
        );
        var statusMessage = _bankIdUserMessageLocalizer.GetLocalizedString(messageShortName);

        return statusMessage;
    }

    public string GetQrCodeAsBase64(BankIdQrStartState qrStartState)
    {
        var elapsedTime = _bankIdFlowSystemClock.UtcNow - qrStartState.QrStartTime;
        var elapsedTotalSeconds = (int)Math.Round(elapsedTime.TotalSeconds);

        var qrCodeContent = BankIdQrCodeContentGenerator.Generate(qrStartState.QrStartToken, qrStartState.QrStartSecret, elapsedTotalSeconds);
        var qrCode = _bankIdQrCodeGenerator.GenerateQrCodeAsBase64(qrCodeContent);

        return qrCode;
    }

    public async Task Cancel(string orderRef, BankIdFlowOptions flowOptions)
    {
        var detectedDevice = _bankIdSupportedDeviceDetector.Detect();

        try
        {
            await _bankIdAppApiClient.CancelAsync(orderRef);
            await _bankIdEventTrigger.TriggerAsync(new BankIdCancelSuccessEvent(orderRef, detectedDevice, flowOptions));
        }
        catch (BankIdApiException exception)
        {
            // When we get exception in a cancellation request, chances
            // are that the orderRef has already been cancelled or we have
            // a network issue. We still want to provide the GUI with the
            // validated cancellation URL though.
            await _bankIdEventTrigger.TriggerAsync(new BankIdCancelErrorEvent(orderRef, exception, detectedDevice, flowOptions));
        }
    }
}
