using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace dnSpy.MCP.Settings {
	public partial class McpSettingsControl : UserControl {
		// WPF PasswordBox.Password does not support two-way binding by design (keeps the secret out
		// of the binding/data-context layer). We sync it manually here, guarded against re-entry.
		McpSettings? _settings;
		bool _syncing;

		public McpSettingsControl() {
			InitializeComponent();
			DataContextChanged += OnDataContextChanged;
			ApiTokenBox.PasswordChanged += OnPasswordChanged;
		}

		void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
			if (_settings != null)
				_settings.PropertyChanged -= OnSettingsPropertyChanged;
			_settings = e.NewValue as McpSettings;
			if (_settings != null) {
				_settings.PropertyChanged += OnSettingsPropertyChanged;
				SyncSettingsToBox();
			}
		}

		void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(McpSettings.ApiToken))
				SyncSettingsToBox();
		}

		void SyncSettingsToBox() {
			if (_settings == null || _syncing)
				return;
			_syncing = true;
			try {
				ApiTokenBox.Password = _settings.ApiToken ?? string.Empty;
			}
			finally {
				_syncing = false;
			}
		}

		void OnPasswordChanged(object sender, RoutedEventArgs e) {
			if (_settings == null || _syncing)
				return;
			_syncing = true;
			try {
				_settings.ApiToken = ApiTokenBox.Password;
			}
			finally {
				_syncing = false;
			}
		}
	}
}
