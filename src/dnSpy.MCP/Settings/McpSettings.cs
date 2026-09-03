using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.MCP.Settings {
	public class McpSettings : ViewModelBase {
		public int Port {
			get => port;
			set { if (port != value) { port = value; OnPropertyChanged(nameof(Port)); } }
		}
		int port = 5150;

		public string Host {
			get => host;
			set { if (host != value) { host = value; OnPropertyChanged(nameof(Host)); } }
		}
		string host = "127.0.0.1";

		public bool AutoStart {
			get => autoStart;
			set { if (autoStart != value) { autoStart = value; OnPropertyChanged(nameof(AutoStart)); } }
		}
		bool autoStart;

		public bool RequireAuth {
			get => requireAuth;
			set { if (requireAuth != value) { requireAuth = value; OnPropertyChanged(nameof(RequireAuth)); } }
		}
		bool requireAuth;

		public string ApiToken {
			get => apiToken;
			set { if (apiToken != value) { apiToken = value; OnPropertyChanged(nameof(ApiToken)); } }
		}
		string apiToken = string.Empty;

		/// <summary>
		/// CORS origins allowed to call the server. Empty by default (CORS disabled —
		/// no Access-Control-Allow-Origin header emitted). Set to a specific origin (e.g.
		/// "http://localhost:3000") or comma-separated list for browser clients. Avoid "*"
		/// when <see cref="Host"/> is bound to a non-loopback address: a wildcard combined
		/// with a token-bearing API lets any website attempt cross-origin calls.
		/// </summary>
		public string AllowedOrigins {
			get => allowedOrigins;
			set { if (allowedOrigins != value) { allowedOrigins = value; OnPropertyChanged(nameof(AllowedOrigins)); } }
		}
		string allowedOrigins = string.Empty;


		public int MaxConcurrency {
			get => maxConcurrency;
			set { if (maxConcurrency != value) { maxConcurrency = value; OnPropertyChanged(nameof(MaxConcurrency)); } }
		}
		int maxConcurrency = 4;

		public int MaxRequestSizeMB {
			get => maxRequestSizeMB;
			set { if (maxRequestSizeMB != value) { maxRequestSizeMB = value; OnPropertyChanged(nameof(MaxRequestSizeMB)); } }
		}
		int maxRequestSizeMB = 1;

		/// <summary>
		/// Tool execution timeout in seconds. Default 30s.
		/// </summary>
		public int ToolTimeoutSeconds {
			get => toolTimeoutSeconds;
			set { if (toolTimeoutSeconds != value) { toolTimeoutSeconds = value; OnPropertyChanged(nameof(ToolTimeoutSeconds)); } }
		}
		int toolTimeoutSeconds = 30;

		public McpSettings Clone() => CopyTo(new McpSettings());

		public McpSettings CopyTo(McpSettings other) {
			other.Port = Port;
			other.Host = Host;
			other.AutoStart = AutoStart;
			other.RequireAuth = RequireAuth;
			other.ApiToken = ApiToken;
			other.AllowedOrigins = AllowedOrigins;
			other.MaxConcurrency = MaxConcurrency;
			other.MaxRequestSizeMB = MaxRequestSizeMB;
			other.ToolTimeoutSeconds = ToolTimeoutSeconds;
			return other;
		}
	}

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
