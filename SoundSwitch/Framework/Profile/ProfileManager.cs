﻿#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using RailSharp;
using RailSharp.Internal.Result;
using Serilog;
using SoundSwitch.Audio.Manager;
using SoundSwitch.Audio.Manager.Interop.Enum;
using SoundSwitch.Common.Framework.Audio.Device;
using SoundSwitch.Framework.Audio.Lister;
using SoundSwitch.Framework.Configuration;
using SoundSwitch.Framework.Profile.Trigger;
using SoundSwitch.Localization;
using SoundSwitch.Model;
using HotKey = SoundSwitch.Framework.WinApi.Keyboard.HotKey;
using WindowsAPIAdapter = SoundSwitch.Framework.WinApi.WindowsAPIAdapter;

namespace SoundSwitch.Framework.Profile
{
    public class ProfileManager
    {
        public delegate void ShowError(string errorMessage, string errorTitle);

        private readonly ForegroundProcess  _foregroundProcess;
        private readonly AudioSwitcher      _audioSwitcher;
        private readonly IAudioDeviceLister _activeDeviceLister;
        private readonly ShowError          _showError;

        private Profile? _steamProfile;

        private readonly Dictionary<HotKey, Profile> _profilesByHotkey     = new Dictionary<HotKey, Profile>();
        private readonly Dictionary<string, Profile> _profileByApplication = new Dictionary<string, Profile>();
        private readonly Dictionary<string, Profile> _profilesByWindowName = new Dictionary<string, Profile>();


        public IReadOnlyCollection<ProfileSetting> Profiles => AppConfigs.Configuration.ProfileSettings;

        public ProfileManager(ForegroundProcess  foregroundProcess,
                              AudioSwitcher      audioSwitcher,
                              IAudioDeviceLister activeDeviceLister,
                              ShowError          showError)
        {
            _foregroundProcess  = foregroundProcess;
            _audioSwitcher      = audioSwitcher;
            _activeDeviceLister = activeDeviceLister;
            _showError          = showError;

            foreach (var profile in AppConfigs.Configuration.Profiles)
            {
                RegisterTriggers(profile);
            }
        }

        private void RegisterTriggers(Profile profile)
        {
            foreach (var trigger in profile.Triggers)
            {
                switch (trigger.Type)
                {
                    case TriggerFactory.Enum.HotKey:
                        _profilesByHotkey.Add(trigger.HotKey, profile);
                        break;
                    case TriggerFactory.Enum.Window:
                        _profilesByWindowName.Add(trigger.WindowName, profile);
                        break;
                    case TriggerFactory.Enum.Process:
                        _profileByApplication.Add(trigger.ApplicationPath, profile);
                        break;
                    case TriggerFactory.Enum.Steam:
                        _steamProfile = profile;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private void UnRegisterTriggers(Profile profile)
        {
            foreach (var trigger in profile.Triggers)
            {
                switch (trigger.Type)
                {
                    case TriggerFactory.Enum.HotKey:
                        WindowsAPIAdapter.UnRegisterHotKey(trigger.HotKey);
                        _profilesByHotkey.Remove(trigger.HotKey);
                        break;
                    case TriggerFactory.Enum.Window:
                        _profilesByWindowName.Remove(trigger.WindowName);
                        break;
                    case TriggerFactory.Enum.Process:
                        _profileByApplication.Remove(trigger.ApplicationPath);
                        break;
                    case TriggerFactory.Enum.Steam:
                        _steamProfile = null;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        /// <summary>
        /// Initialize the profile manager. Return the list of Profile that it couldn't register hotkeys for.
        /// </summary>
        /// <returns></returns>
        public Result<Profile[], VoidSuccess> Init()
        {
            RegisterEvents();

            var errors = _profilesByHotkey
                         .Where(pair => !WindowsAPIAdapter.RegisterHotKey(pair.Key))
                         .Select(pair => pair.Value)
                         .ToArray();

            InitializeProfileExistingProcess();

            if (errors.Length > 0)
            {
                return errors;
            }

            return Result.Success();
        }

        private void RegisterEvents()
        {
            _foregroundProcess.Changed += (sender, @event) =>
            {
                if (!_profileByApplication.TryGetValue(@event.ProcessName.ToLower(), out var profile))
                    return;
                SwitchAudio(profile, @event.ProcessId);
            };

            WindowsAPIAdapter.HotKeyPressed += (sender, args) =>
            {
                if (!_profilesByHotkey.TryGetValue(args.HotKey, out var profile))
                    return;
                SwitchAudio(profile);
            };
        }

        private DeviceInfo? CheckDeviceAvailable(DeviceInfo deviceInfo)
        {
            return deviceInfo.Type switch
            {
                DataFlow.Capture => _activeDeviceLister.RecordingDevices.FirstOrDefault(info => info.Equals(deviceInfo)),
                _                => _activeDeviceLister.PlaybackDevices.FirstOrDefault(info => info.Equals(deviceInfo))
            };
        }

        private void SwitchAudio(Profile profile, uint processId)
        {
            foreach (var device in profile.Devices)
            {
                var deviceToUse = CheckDeviceAvailable(device.DeviceInfo);
                if (deviceToUse == null)
                {
                    _showError.Invoke(string.Format(SettingsStrings.profile_error_device_not_found, device.DeviceInfo.NameClean), $"{SettingsStrings.profile_error_title}: {profile.Name}");
                    continue;
                }

                _audioSwitcher.SwitchProcessTo(
                    deviceToUse.Id,
                    device.Role,
                    (EDataFlow) deviceToUse.Type,
                    processId);

                if (profile.AlsoSwitchDefaultDevice)
                {
                    _audioSwitcher.SwitchTo(deviceToUse.Id, device.Role);
                }
            }
        }

        private void SwitchAudio(Profile profile)
        {
            foreach (var device in profile.Devices)
            {
                var deviceToUse = CheckDeviceAvailable(device.DeviceInfo);
                if (deviceToUse == null)
                {
                    _showError.Invoke(string.Format(SettingsStrings.profile_error_device_not_found, device.DeviceInfo.NameClean), $"{SettingsStrings.profile_error_title}: {profile.Name}");
                    continue;
                }

                _audioSwitcher.SwitchTo(deviceToUse.Id, device.Role);
            }
        }


        /// <summary>
        /// Add a profile to the system
        /// </summary>
        public Result<string, VoidSuccess> AddProfile(Profile profile)
        {
            return ValidateProfile(profile)
                .Map(success =>
                {
                    RegisterTriggers(profile);
                    AppConfigs.Configuration.Profiles.Add(profile);
                    AppConfigs.Configuration.Save();

                    return success;
                });
        }

        private Result<string, VoidSuccess> ValidateProfile(Profile profile)
        {
            if (string.IsNullOrEmpty(profile.Name))
            {
                return SettingsStrings.profile_error_no_name;
            }

            if (profile.Triggers.Count == 0)
            {
                return SettingsStrings.profile_error_no_name;
            }

            if (profile.Recording == null && profile.Playback == null)
            {
                return SettingsStrings.profile_error_needPlaybackOrRecording;
            }

            if (AppConfigs.Configuration.Profiles.Contains(profile))
            {
                return string.Format(SettingsStrings.profile_error_name, profile.Name);
            }

            foreach (var trigger in profile.Triggers)
            {
                switch (trigger.Type)
                {
                    case TriggerFactory.Enum.HotKey:
                        if (trigger.HotKey == null || _profilesByHotkey.ContainsKey(trigger.HotKey) || !WindowsAPIAdapter.RegisterHotKey(trigger.HotKey))
                        {
                            return string.Format(SettingsStrings.profile_error_hotkey, trigger.HotKey);
                        }

                        break;
                    case TriggerFactory.Enum.Window:
                        if (string.IsNullOrEmpty(trigger.WindowName) || _profilesByWindowName.ContainsKey(trigger.WindowName.ToLower()))
                        {
                            return string.Format(SettingsStrings.profile_error_window, trigger.WindowName);
                        }

                        break;
                    case TriggerFactory.Enum.Process:

                        if (string.IsNullOrEmpty(trigger.ApplicationPath) || _profileByApplication.ContainsKey(trigger.ApplicationPath.ToLower()))
                        {
                            return string.Format(SettingsStrings.profile_error_application, trigger.ApplicationPath);
                        }

                        break;
                    case TriggerFactory.Enum.Steam:
                        if (_steamProfile != null)
                        {
                            return SettingsStrings.profile_error_steam;
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return Result.Success();
        }

        private Result<string, VoidSuccess> ValidateAddProfile(ProfileSetting profile)
        {
            if (string.IsNullOrEmpty(profile.ProfileName))
            {
                return SettingsStrings.profile_error_no_name;
            }

            if (string.IsNullOrEmpty(profile.ApplicationPath) && profile.HotKey == null)
            {
                return SettingsStrings.profile_error_needHKOrPath;
            }

            if (profile.Recording == null && profile.Playback == null)
            {
                return SettingsStrings.profile_error_needPlaybackOrRecording;
            }

            if (profile.HotKey != null && _profilesByHotkey.ContainsKey(profile.HotKey))
            {
                return string.Format(SettingsStrings.profile_error_hotkey, profile.HotKey);
            }

            if (!string.IsNullOrEmpty(profile.ApplicationPath) && _profileByApplication.ContainsKey(profile.ApplicationPath.ToLower()))
            {
                return string.Format(SettingsStrings.profile_error_application, profile.ApplicationPath);
            }

            if (AppConfigs.Configuration.ProfileSettings.Contains(profile))
            {
                return string.Format(SettingsStrings.profile_error_name, profile.ProfileName);
            }

            if (profile.HotKey != null && !WindowsAPIAdapter.RegisterHotKey(profile.HotKey))
            {
                return string.Format(SettingsStrings.profile_error_hotkey, profile.HotKey);
            }

            return Result.Success();
        }

        /// <summary>
        /// Delete the given profiles.
        ///
        /// Result Failure contains the profile that couldn't be deleted because they don't exists.
        /// </summary>
        public Result<Profile[], VoidSuccess> DeleteProfiles(IEnumerable<Profile> profilesToDelete)
        {
            var errors            = new List<Profile>();
            var profiles          = profilesToDelete.ToArray();
            var resetProcessAudio = profiles.Any(profile => profile.Triggers.Any(trigger => trigger.Type == TriggerFactory.Enum.Process || trigger.Type == TriggerFactory.Enum.Window));
            foreach (var profile in profiles)
            {
                if (!AppConfigs.Configuration.Profiles.Contains(profile))
                {
                    errors.Add(profile);
                    continue;
                }

                UnRegisterTriggers(profile);

                AppConfigs.Configuration.Profiles.Remove(profile);
            }

            AppConfigs.Configuration.Save();
            if (errors.Count > 0)
            {
                return errors.ToArray();
            }

            if (resetProcessAudio)
            {
                _audioSwitcher.ResetProcessDeviceConfiguration();
                InitializeProfileExistingProcess();
            }

            return Result.Success();
        }

        private void InitializeProfileExistingProcess()
        {
            if (_profileByApplication.Count == 0)
            {
                return;
            }

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited)
                        continue;
                    if (!process.Responding)
                        continue;
                    var filePath = process.MainModule?.FileName.ToLower();
                    if (filePath == null)
                    {
                        continue;
                    }

                    if (_profileByApplication.TryGetValue(filePath, out var profile))
                    {
                        SwitchAudio(profile, (uint) process.Id);
                    }
                }
                catch (Win32Exception)
                {
                    //Happen when trying to access process MainModule belonging to windows like svchost
                }
                catch (InvalidOperationException)
                {
                    //ApplicationPath in a weird state, can't get MainModule for it.
                }
                catch (Exception e)
                {
                    Log.Error(e, "Couldn't get information about the given process.");
                }
            }
        }
    }
}