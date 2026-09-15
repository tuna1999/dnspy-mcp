using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading;
using dnSpy.Contracts.Settings;

namespace dnSpy.MCP.Settings {
	/// <summary>
	/// MEF-exported settings implementation. Loads from / saves to the dnSpy
	/// <see cref="ISettingsService"/> under a stable GUID section. Property change events
	/// are debounced (500ms) to avoid writing to disk on every keystroke.
	/// The POCO base <see cref="McpSettings"/> lives in the Core project.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new("F7A2B3C4-D5E6-7890-ABCD-EF1234567890");
		readonly ISettingsService settingsService;

		/// <summary>
		/// Debounce timer to avoid writing settings to disk on every keystroke.
		/// Persists settings 500ms after the last property change.
		/// </summary>
		Timer? _saveTimer;

		[ImportingConstructor]
		McpSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;
			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			Port = sect.Attribute<int?>(nameof(Port)) ?? Port;
			Host = sect.Attribute<string>(nameof(Host)) ?? Host;
			AutoStart = sect.Attribute<bool?>(nameof(AutoStart)) ?? AutoStart;
			RequireAuth = sect.Attribute<bool?>(nameof(RequireAuth)) ?? RequireAuth;
			ApiToken = sect.Attribute<string>(nameof(ApiToken)) ?? ApiToken;
			AllowedOrigins = sect.Attribute<string>(nameof(AllowedOrigins)) ?? AllowedOrigins;
			MaxConcurrency = sect.Attribute<int?>(nameof(MaxConcurrency)) ?? MaxConcurrency;
			MaxRequestSizeMB = sect.Attribute<int?>(nameof(MaxRequestSizeMB)) ?? MaxRequestSizeMB;
			ToolTimeoutSeconds = sect.Attribute<int?>(nameof(ToolTimeoutSeconds)) ?? ToolTimeoutSeconds;
			PropertyChanged += OnSettingChanged;
		}

        void OnSettingChanged(object? sender, PropertyChangedEventArgs e) {
            // Debounce: reset timer on each change, save 500ms after last change
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SaveSettings(), null, 500, Timeout.Infinite);
        }

        void SaveSettings() {
            // Timer callbacks run on threadpool threads with NO exception handler —
            // an unhandled throw here kills the dnSpy process. Never let one escape.
            try {
                // Serialize settings-tree mutation onto the WPF UI thread: dnSpy's
                // ISettingsService is written from the UI thread (options dialog, exit
                // writer); mutating sections from a threadpool timer can race the
                // exit-time XmlSettingsWriter enumerating the section collection.
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    DoSaveSettings();
                else
                    dispatcher.BeginInvoke(() => {
                        // Guard the DISPATCHED path too: an exception escaping a
                        // BeginInvoke delegate surfaces as an unhandled dispatcher
                        // exception on the UI thread, not in the try below.
                        try { DoSaveSettings(); }
                        catch (Exception ex) {
                            System.Diagnostics.Debug.WriteLine($"MCP [SETTINGS SAVE ERROR]: {ex.Message}");
                        }
                    });
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"MCP [SETTINGS SAVE ERROR]: {ex.Message}");
            }
        }

        void DoSaveSettings() {
            var sect = settingsService.RecreateSection(SETTINGS_GUID);
            sect.Attribute(nameof(Port), Port);
            sect.Attribute(nameof(Host), Host);
            sect.Attribute(nameof(AutoStart), AutoStart);
            sect.Attribute(nameof(RequireAuth), RequireAuth);
            sect.Attribute(nameof(ApiToken), ApiToken);
            sect.Attribute(nameof(AllowedOrigins), AllowedOrigins);
            sect.Attribute(nameof(MaxConcurrency), MaxConcurrency);
            sect.Attribute(nameof(MaxRequestSizeMB), MaxRequestSizeMB);
            sect.Attribute(nameof(ToolTimeoutSeconds), ToolTimeoutSeconds);
        }
	}
}
