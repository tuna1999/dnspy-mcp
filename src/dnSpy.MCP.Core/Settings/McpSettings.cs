using dnSpy.Contracts.MVVM;

// Cross-project namespace: this POCO lives in Core but keeps the dnSpy.MCP.Settings
// namespace so Extension files keep their existing `using dnSpy.MCP.Settings;` and find
// McpSettings without code changes. The MEF-exported McpSettingsImpl subclass lives in the
// Extension project under the same namespace.
namespace dnSpy.MCP.Settings {
	/// <summary>
	/// Settings POCO. Host-agnostic — used by both the dnSpy Extension (via the
	/// MEF-exported <see cref="McpSettingsImpl"/> subclass) and the Headless host.
	/// All property setters fire <see cref="ViewModelBase.OnPropertyChanged"/> so the
	/// dnSpy Options dialog binds correctly; the Headless host simply ignores the events.
	/// </summary>
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
}
