﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using HidSharp;
using Newtonsoft.Json;
using OpenTabletDriver.Desktop.Binding;
using OpenTabletDriver.Desktop.Contracts;
using OpenTabletDriver.Debugging;
using OpenTabletDriver.Desktop;
using OpenTabletDriver.Desktop.Interop;
using OpenTabletDriver.Desktop.Migration;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Logging;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Plugin.Tablet.Interpolator;
using OpenTabletDriver.Reflection;

namespace OpenTabletDriver.Daemon
{
    public class DriverDaemon : IDriverDaemon
    {
        public DriverDaemon()
        {
            Log.Output += (sender, message) =>
            {
                LogMessages.Add(message);
                Console.WriteLine(Log.GetStringFormat(message));
                Message?.Invoke(sender, message);
            };
            Driver.Reading += async (_, isReading) => TabletChanged?.Invoke(this, isReading ? await GetTablet() : null);

            LoadUserSettings();

            HidSharp.DeviceList.Local.Changed += async (sender, e) => 
            {
                var newDevices = from device in DeviceList.Local.GetHidDevices()
                    where !CurrentDevices.Any(d => d == device)
                    select device;

                if (newDevices.Count() > 0)
                {
                    if (await GetTablet() == null)
                        await DetectTablets();
                }
                CurrentDevices = DeviceList.Local.GetHidDevices();
            };
        }

        private async void LoadUserSettings()
        {
            await LoadPlugins();
            await DetectTablets();

            var appdataDir = new DirectoryInfo(AppInfo.Current.AppDataDirectory);
            if (!appdataDir.Exists)
            {
                appdataDir.Create();
                Log.Write("Settings", $"Created OpenTabletDriver application data directory: {appdataDir.FullName}");
            }

            var settingsFile = new FileInfo(AppInfo.Current.SettingsFile);
            if (Settings == null && settingsFile.Exists)
            {
                var settings = Settings.Deserialize(settingsFile);
                await SetSettings(settings);
            }
        }

        public event EventHandler<LogMessage> Message;
        public event EventHandler<DebugTabletReport> TabletReport;
        public event EventHandler<DebugAuxReport> AuxReport;
        public event EventHandler<TabletState> TabletChanged;

        public DesktopDriver Driver { private set; get; } = new DesktopDriver();
        private Settings Settings { set; get; }
        private IEnumerable<HidDevice> CurrentDevices { set; get; } = DeviceList.Local.GetHidDevices();
        private Collection<LogMessage> LogMessages { set; get; } = new Collection<LogMessage>();
        private Collection<ITool> Tools { set; get; } = new Collection<ITool>();

        private static JsonSerializer JsonSerializer = new JsonSerializer
        {
            Formatting = Formatting.Indented
        };

        public Task WriteMessage(LogMessage message)
        {
            Log.OnOutput(message);
            return Task.CompletedTask;
        }

        public Task<TabletState> GetTablet()
        {
            return Task.FromResult(Driver.Tablet);
        }

        public async Task<TabletState> DetectTablets()
        {
            var configDir = new DirectoryInfo(AppInfo.Current.ConfigurationDirectory);
            if (configDir.Exists)
            {
                foreach (var file in configDir.EnumerateFiles("*.json", SearchOption.AllDirectories))
                {
                    using (var fs = file.OpenRead())
                    using (var sr = new StreamReader(fs))
                    using (var jr = new JsonTextReader(sr))
                    {
                        var tablet = JsonSerializer.Deserialize<TabletConfiguration>(jr);
                        if (Driver.TryMatch(tablet))
                            return await GetTablet();
                    }
                }
            }
            else
            {
                Log.Write("Detect", $"The configuration directory '{configDir.FullName}' does not exist.", LogLevel.Error);
            }
            Log.Write("Detect", "No tablet found.");
            return null;
        }

        public Task SetSettings(Settings settings)
        {
            Settings = SettingsMigrator.Migrate(settings);
            
            Driver.OutputMode = new PluginReference(Settings.OutputMode).Construct<IOutputMode>();

            if (Driver.OutputMode != null)
                Log.Write("Settings", $"Output mode: {Driver.OutputMode.GetType().FullName}");

            if (Driver.OutputMode is IOutputMode outputMode)
                SetOutputModeSettings(outputMode);
            
            if (Driver.OutputMode is AbsoluteOutputMode absoluteMode)
                SetAbsoluteModeSettings(absoluteMode);

            if (Driver.OutputMode is RelativeOutputMode relativeMode)
                SetRelativeModeSettings(relativeMode);

            SetBindingHandlerSettings();
                

            if (Settings.AutoHook)
            {
                Driver.EnableInput = true;
                Log.Write("Settings", "Driver is auto-enabled.");
            }

            SetToolSettings();
            SetInterpolatorSettings();
            return Task.CompletedTask;
        }

        private void SetOutputModeSettings(IOutputMode outputMode)
        {
            outputMode.Filters = from filterPath in Settings?.Filters
                let filter = new PluginReference(filterPath).Construct<IFilter>()
                where filter != null
                select filter;

            foreach (var filter in outputMode.Filters)
            {
                foreach (var property in filter.GetType().GetProperties())
                {
                    var settingPath = filter.GetType().FullName + "." + property.Name;
                    if (property.GetCustomAttribute<PropertyAttribute>(false) != null && 
                        Settings.PluginSettings.TryGetValue(settingPath, out var strValue))
                    {
                        try
                        {
                            var value = Convert.ChangeType(strValue, property.PropertyType);
                            property.SetValue(filter, value);
                        }
                        catch (FormatException)
                        {
                            Log.Write("Settings", $"Invalid filter setting for '{property.Name}', this setting will be cleared.");
                            Settings.PluginSettings.Remove(settingPath);
                        }
                    }
                }
            }

            if (outputMode.Filters != null && outputMode.Filters.Count() > 0)
                Log.Write("Settings", $"Filters: {string.Join(", ", outputMode.Filters)}");
            
            outputMode.Tablet = Driver.Tablet;
        }

        private void SetAbsoluteModeSettings(AbsoluteOutputMode absoluteMode)
        {
            absoluteMode.Output = new Area
            {
                Width = Settings.DisplayWidth,
                Height = Settings.DisplayHeight,
                Position = new Vector2
                {
                    X = Settings.DisplayX,
                    Y = Settings.DisplayY
                }
            };
            Log.Write("Settings", $"Display area: {absoluteMode.Output}");

            absoluteMode.Input = new Area
            {
                Width = Settings.TabletWidth,
                Height = Settings.TabletHeight,
                Position = new Vector2
                {
                    X = Settings.TabletX,
                    Y = Settings.TabletY
                },
                Rotation = Settings.TabletRotation
            };
            Log.Write("Settings", $"Tablet area: {absoluteMode.Input}");

            absoluteMode.AreaClipping = Settings.EnableClipping;   
            Log.Write("Settings", $"Clipping: {(absoluteMode.AreaClipping ? "Enabled" : "Disabled")}");

            absoluteMode.AreaLimiting = Settings.EnableAreaLimiting;
            Log.Write("Settings", $"Ignoring reports outside area: {(absoluteMode.AreaLimiting ? "Enabled" : "Disabled")}");
        }

        private void SetRelativeModeSettings(RelativeOutputMode relativeMode)
        {
            relativeMode.Sensitivity = new Vector2(Settings.XSensitivity, Settings.YSensitivity);
            Log.Write("Settings", $"Relative Mode Sensitivity (X, Y): {relativeMode.Sensitivity}");

            relativeMode.Rotation = Settings.RelativeRotation;
            Log.Write("Settings", $"Relative Mode Rotation: {relativeMode.Rotation}");

            relativeMode.ResetTime = Settings.ResetTime;
            Log.Write("Settings", $"Reset time: {relativeMode.ResetTime}");
        }

        private void SetBindingHandlerSettings()
        {
            BindingHandler.TipBinding = BindingTools.GetBinding(Settings.TipButton);
            BindingHandler.TipActivationPressure = Settings.TipActivationPressure;
            Log.Write("Settings", $"Tip Binding: '{(BindingHandler.TipBinding is IBinding binding ? binding.ToString() : "None")}'@{BindingHandler.TipActivationPressure}%");

            if (Settings.PenButtons != null)
            {
                for (int index = 0; index < Settings.PenButtons.Count; index++)
                    BindingHandler.PenButtonBindings[index] = BindingTools.GetBinding(Settings.PenButtons[index]);

                Log.Write("Settings", $"Pen Bindings: " + string.Join(", ", BindingHandler.PenButtonBindings));
            }

            if (Settings.AuxButtons != null)
            {
                for (int index = 0; index < Settings.AuxButtons.Count; index++)
                    BindingHandler.AuxButtonBindings[index] = BindingTools.GetBinding(Settings.AuxButtons[index]);

                Log.Write("Settings", $"Express Key Bindings: " + string.Join(", ", BindingHandler.AuxButtonBindings));
            }
        }

        private void SetToolSettings()
        {
            foreach (var runningTool in Tools)
            {
                runningTool.Dispose();
            }
            
            foreach (var toolName in Settings.Tools)
            {
                var plugin = new PluginReference(toolName);
                var type = plugin.GetTypeReference<ITool>();
                
                var tool = plugin.Construct<ITool>();
                foreach (var property in type.GetProperties())
                {
                    if (property.GetCustomAttribute<PropertyAttribute>(false) != null && 
                        Settings.PluginSettings.TryGetValue(type.FullName + "." + property.Name, out var strValue))
                    {
                        var value = Convert.ChangeType(strValue, property.PropertyType);
                        property.SetValue(tool, value);
                    }
                }

                if (tool.Initialize())
                    Tools.Add(tool);
                else
                    Log.Write("Tool", $"Failed to initialize {plugin.Name} tool.", LogLevel.Error);
            }
        }

        private void SetInterpolatorSettings()
        {
            foreach (var interpolator in Driver.Interpolators)
                interpolator.Dispose();
            
            Driver.Interpolators.Clear();
            if (Settings.Interpolators != null)
            {
                foreach (var interpolatorName in Settings.Interpolators)
                {
                    var plugin = new PluginReference(interpolatorName);
                    var type = plugin.GetTypeReference<Interpolator>();

                    var interpolator = plugin.Construct<Interpolator>(Platform.Timer);
                    foreach (var property in type.GetProperties())
                    {
                        if (property.GetCustomAttribute<PropertyAttribute>(false) != null &&
                            Settings.PluginSettings.TryGetValue(type.FullName + "." + property.Name, out var strValue))
                        {
                            var value = Convert.ChangeType(strValue, property.PropertyType);
                            property.SetValue(interpolator, value);
                        }
                    }

                    Driver.Interpolators.Add(interpolator);

                    Log.Write("Settings", $"Interpolator: {interpolator}");
                }
            }
        }

        public Task<Settings> GetSettings()
        {
            return Task.FromResult(Settings);
        }

        public Task<AppInfo> GetApplicationInfo()
        {
            return Task.FromResult(AppInfo.Current);
        }

        public async Task LoadPlugins()
        {
            var pluginDir = new DirectoryInfo(AppInfo.Current.PluginDirectory);
            if (pluginDir.Exists)
            {
                await PluginManager.LoadPluginsAsync();
            }
            else
            {
                pluginDir.Create();
                Log.Write("Detect", $"The plugin directory '{pluginDir.FullName}' has been created");
            }
        }

        public Task EnableInput(bool isHooked)
        {
            Driver.EnableInput = isHooked;
            return Task.CompletedTask;
        }

        public Task SetTabletDebug(bool enabled)
        {
            void onDeviceReport(object _, IDeviceReport report)
            {
                if (report is ITabletReport tabletReport)
                    TabletReport?.Invoke(this, new DebugTabletReport(tabletReport));
                if (report is IAuxReport auxReport)
                    AuxReport?.Invoke(this, new DebugAuxReport(auxReport));
            }
            if (enabled)
            {
                Driver.TabletReader.Report += onDeviceReport;
                Driver.AuxReader.Report += onDeviceReport;
            }
            else
            {
                Driver.TabletReader.Report -= onDeviceReport;
                Driver.AuxReader.Report -= onDeviceReport;
            }
            return Task.CompletedTask;
        }

        public Task<string> RequestDeviceString(int index)
        {
            return Task.FromResult(Driver.TabletReader?.Device?.GetDeviceString(index) ?? null);
        }

        public Task<string> RequestDeviceString(int vid, int pid, int index)
        {
            HidDevice tablet = null;
            foreach (var device in DeviceList.Local.GetHidDevices(vendorID: vid, productID: pid))
            {
                tablet = device;
                break;
            }
            if (tablet == null)
            {
                throw new Exception("Device not found");
            }

            return Task.FromResult(tablet.GetDeviceString(index));
        }

        public Task<IEnumerable<LogMessage>> GetCurrentLog()
        {
            IEnumerable<LogMessage> messages = LogMessages;
            return Task.FromResult(messages);
        }
    }
}
