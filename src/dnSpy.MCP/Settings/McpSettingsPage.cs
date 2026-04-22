using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.MCP.Settings {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class McpSettingsPageProvider : IAppSettingsPageProvider {
		readonly McpSettings settings;

		[ImportingConstructor]
		McpSettingsPageProvider(McpSettings settings) => this.settings = settings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new McpAppSettingsPage(settings);
		}
	}

	sealed class McpAppSettingsPage : AppSettingsPage {
		internal static readonly Guid PAGE_GUID = new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
		public override Guid Guid => PAGE_GUID;
		public override double Order => AppSettingsConstants.ORDER_BOOKMARKS + 100;
		public override string Title => "MCP Server";

		public override object? UIObject {
			get {
				uiObject ??= new McpSettingsControl { DataContext = editSettings };
				return uiObject;
			}
		}
		McpSettingsControl? uiObject;

		readonly McpSettings globalSettings;
		readonly McpSettings editSettings;

		public McpAppSettingsPage(McpSettings settings) {
			globalSettings = settings;
			editSettings = settings.Clone();
		}

		public override void OnApply() => editSettings.CopyTo(globalSettings);
	}
}
