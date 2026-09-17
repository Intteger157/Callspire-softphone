using System;
using System.Collections.Generic;
using System.Linq;
using Softphone.AppHost.ViewModels;

namespace Softphone.Service.Contracts
{
    public sealed class ChoiceDto
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public static ChoiceDto From(ChoiceOption o) => new() { Key = o.Key, Label = o.Label };
    }

    public sealed class AudioDeviceDto
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public static AudioDeviceDto From(AudioDeviceOption o) => new() { Index = o.Index, Name = o.Name };
    }

    /// <summary>
    /// Editable settings fields — the payload of Swift → C# `saveSettings` and the editable half of
    /// <see cref="SettingsDto"/>. Secrets are sent in plain text over the local socket exactly as the
    /// WPF/Avalonia editors hold them in memory; the ViewModel re-encrypts only the ones that changed.
    /// </summary>
    public class SettingsFieldsDto
    {
        // main line
        public string MainName { get; set; } = "";
        public string MainTransport { get; set; } = "Sip";
        public string MainServer { get; set; } = "";
        public string MainPort { get; set; } = "5060";
        public string MainUsername { get; set; } = "";
        public string MainPassword { get; set; } = "";
        public bool MainUseTls { get; set; }
        public bool MainUseSrtp { get; set; }
        public string MainWsUri { get; set; } = "";
        public string MainWebRtcUsername { get; set; } = "";
        public string MainWebRtcPassword { get; set; } = "";
        public string MainTurnUri { get; set; } = "";
        public string MainTurnUsername { get; set; } = "";
        public string MainTurnPassword { get; set; } = "";

        // secondary line
        public string SecondaryName { get; set; } = "";
        public string SecondaryTransport { get; set; } = "Sip";
        public string SecondaryServer { get; set; } = "";
        public string SecondaryRtpServer { get; set; } = "";
        public string SecondaryUsername { get; set; } = "";
        public string SecondaryPassword { get; set; } = "";
        public bool SecondaryUseTls { get; set; }
        public bool SecondaryUseSrtp { get; set; }
        public string SecondaryWsUri { get; set; } = "";
        public string SecondaryWebRtcUsername { get; set; } = "";
        public string SecondaryWebRtcPassword { get; set; } = "";
        public string SecondaryTurnUri { get; set; } = "";
        public string SecondaryTurnUsername { get; set; } = "";
        public string SecondaryTurnPassword { get; set; } = "";

        // audio
        public int SelectedMicrophone { get; set; } = -1;
        public int SelectedSpeaker { get; set; } = -1;
        public bool EchoCancellation { get; set; }
        public double RingtoneVolume { get; set; }
        public bool RingtoneWav { get; set; }
        public string SipCodec { get; set; } = "auto";
        public int SipSampleRate { get; set; }
        public int SipOpusBitrate { get; set; }

        // Kommo & gateway
        public bool GatewayEnabled { get; set; }
        public string GatewayUrl { get; set; } = "";
        public string GatewayToken { get; set; } = "";
        public string GatewayExtension { get; set; } = "";
        public bool KommoEnabled { get; set; }
        public string KommoSubdomain { get; set; } = "";
        public string KommoAuthMode { get; set; } = "manual";
        public string KommoToken { get; set; } = "";
        public string KommoClientId { get; set; } = "";
        public string KommoClientSecret { get; set; } = "";
        public string KommoRedirectUri { get; set; } = "";
        public bool KommoLeadSelection { get; set; }
        public bool KommoRecordingUpload { get; set; }
        /// <summary>"local" | "gateway"</summary>
        public string KommoSource { get; set; } = "local";

        // appearance / advanced
        public string Theme { get; set; } = "system";
        public bool CallRecording { get; set; }
        public bool WebRtcDebug { get; set; }
    }

    /// <summary>C# → Swift `getSettings` result / `settingsChanged` event: editable fields + read-only context.</summary>
    public sealed class SettingsDto : SettingsFieldsDto
    {
        public IReadOnlyList<ChoiceDto> TransportOptions { get; init; } = Array.Empty<ChoiceDto>();
        public IReadOnlyList<ChoiceDto> ThemeOptions { get; init; } = Array.Empty<ChoiceDto>();
        public IReadOnlyList<ChoiceDto> KommoAuthOptions { get; init; } = Array.Empty<ChoiceDto>();
        public IReadOnlyList<ChoiceDto> KommoSourceOptions { get; init; } = Array.Empty<ChoiceDto>();
        public IReadOnlyList<ChoiceDto> SipCodecOptions { get; init; } = Array.Empty<ChoiceDto>();
        public IReadOnlyList<int> SipSampleRateOptions { get; init; } = Array.Empty<int>();
        public IReadOnlyList<AudioDeviceDto> Microphones { get; init; } = Array.Empty<AudioDeviceDto>();
        public IReadOnlyList<AudioDeviceDto> Speakers { get; init; } = Array.Empty<AudioDeviceDto>();
        public string AudioBackendInfo { get; init; } = "";
        public string RecordingsFolder { get; init; } = "";
        public string LogsFolder { get; init; } = "";
        public string SettingsFile { get; init; } = "";
        public string VersionText { get; init; } = "";
        public string UpdateStatus { get; init; } = "";
        public bool UpdateAvailable { get; init; }
        public string UpdateUrl { get; init; } = "";
        public string StatusText { get; init; } = "";
        public bool StatusIsError { get; init; }
        public bool IsBusy { get; init; }
        public KommoOAuthStatusDto KommoOAuth { get; init; } = new();

        public static SettingsDto From(SettingsViewModel vm, KommoOAuthStatusDto oauth) => new()
        {
            MainName = vm.MainName, MainTransport = vm.MainTransport.Key, MainServer = vm.MainServer, MainPort = vm.MainPort,
            MainUsername = vm.MainUsername, MainPassword = vm.MainPassword, MainUseTls = vm.MainUseTls, MainUseSrtp = vm.MainUseSrtp,
            MainWsUri = vm.MainWsUri, MainWebRtcUsername = vm.MainWebRtcUsername, MainWebRtcPassword = vm.MainWebRtcPassword,
            MainTurnUri = vm.MainTurnUri, MainTurnUsername = vm.MainTurnUsername, MainTurnPassword = vm.MainTurnPassword,

            SecondaryName = vm.SecondaryName, SecondaryTransport = vm.SecondaryTransport.Key, SecondaryServer = vm.SecondaryServer,
            SecondaryRtpServer = vm.SecondaryRtpServer, SecondaryUsername = vm.SecondaryUsername, SecondaryPassword = vm.SecondaryPassword,
            SecondaryUseTls = vm.SecondaryUseTls, SecondaryUseSrtp = vm.SecondaryUseSrtp, SecondaryWsUri = vm.SecondaryWsUri,
            SecondaryWebRtcUsername = vm.SecondaryWebRtcUsername, SecondaryWebRtcPassword = vm.SecondaryWebRtcPassword,
            SecondaryTurnUri = vm.SecondaryTurnUri, SecondaryTurnUsername = vm.SecondaryTurnUsername, SecondaryTurnPassword = vm.SecondaryTurnPassword,

            SelectedMicrophone = vm.SelectedMicrophone?.Index ?? -1, SelectedSpeaker = vm.SelectedSpeaker?.Index ?? -1,
            EchoCancellation = vm.EchoCancellation, RingtoneVolume = vm.RingtoneVolume, RingtoneWav = vm.RingtoneWav,
            SipCodec = vm.SipCodec.Key, SipSampleRate = vm.SipSampleRate, SipOpusBitrate = vm.SipOpusBitrate,

            GatewayEnabled = vm.GatewayEnabled, GatewayUrl = vm.GatewayUrl, GatewayToken = vm.GatewayToken, GatewayExtension = vm.GatewayExtension,
            KommoEnabled = vm.KommoEnabled, KommoSubdomain = vm.KommoSubdomain, KommoAuthMode = vm.KommoAuthMode.Key, KommoToken = vm.KommoToken,
            KommoClientId = vm.KommoClientId, KommoClientSecret = vm.KommoClientSecret, KommoRedirectUri = vm.KommoRedirectUri,
            KommoLeadSelection = vm.KommoLeadSelection, KommoRecordingUpload = vm.KommoRecordingUpload, KommoSource = vm.KommoSource.Key,

            Theme = vm.Theme.Key, CallRecording = vm.CallRecording, WebRtcDebug = vm.WebRtcDebug,

            TransportOptions = SettingsViewModel.TransportOptions.Select(ChoiceDto.From).ToList(),
            ThemeOptions = SettingsViewModel.ThemeOptions.Select(ChoiceDto.From).ToList(),
            KommoAuthOptions = SettingsViewModel.KommoAuthOptions.Select(ChoiceDto.From).ToList(),
            KommoSourceOptions = SettingsViewModel.KommoSourceOptions.Select(ChoiceDto.From).ToList(),
            SipCodecOptions = SettingsViewModel.SipCodecOptions.Select(ChoiceDto.From).ToList(),
            SipSampleRateOptions = SettingsViewModel.SipSampleRateOptions,
            Microphones = vm.Microphones.Select(AudioDeviceDto.From).ToList(),
            Speakers = vm.Speakers.Select(AudioDeviceDto.From).ToList(),
            AudioBackendInfo = vm.AudioBackendInfo, RecordingsFolder = vm.RecordingsFolder, LogsFolder = vm.LogsFolder,
            SettingsFile = vm.SettingsFile, VersionText = vm.VersionText, UpdateStatus = vm.UpdateStatus,
            UpdateAvailable = vm.UpdateAvailable, UpdateUrl = vm.UpdateUrl,
            StatusText = vm.StatusText, StatusIsError = vm.StatusIsError, IsBusy = vm.IsBusy,
            KommoOAuth = oauth,
        };
    }

    /// <summary>Kommo OAuth panel state (WPF "Authorize with Kommo" parity).</summary>
    public sealed class KommoOAuthStatusDto
    {
        public bool IsAuthorized { get; init; }
        public string StatusText { get; init; } = "Not authorized";
        public DateTime? ExpiresAt { get; init; }
        public bool IsBusy { get; init; }
        public string? Error { get; init; }
    }
}
