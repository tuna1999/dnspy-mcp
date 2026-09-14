using System;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Settings;
using MC = dnSpy.Contracts.Menus.MenuConstants;

namespace dnSpy.MCP {
    static class McpMenuConstants {
        public const string APP_MENU_MCP = "B91A3E8A-4B1C-4D9E-A2F5-8E6F1A7B9C3D";
        public const string GROUP_MCP1 = "0,B91A3E8A-4B1C-4D9E-A2F5-8E6F1A7B9C3D";
    }

    [ExportMenu(OwnerGuid = MC.APP_MENU_GUID, Guid = McpMenuConstants.APP_MENU_MCP, Order = MC.ORDER_APP_MENU_DEBUG + 0.2, Header = "_MCP Server")]
    sealed class McpMenu : IMenu {
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Start", Group = McpMenuConstants.GROUP_MCP1, Order = 0)]
    sealed class StartMcpCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            TheExtension.Instance?.StartServer();
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Stop", Group = McpMenuConstants.GROUP_MCP1, Order = 5)]
    sealed class StopMcpCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            TheExtension.Instance?.StopServer();
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Status", Group = McpMenuConstants.GROUP_MCP1, Order = 10)]
    sealed class StatusCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            var ext = TheExtension.Instance;
            if (ext == null) {
                McpLogger.Warn("Extension not loaded");
                return;
            }
            var status = $"MCP Server: {(ext.IsServerRunning ? "Running" : "Stopped")}, Port: {ext.ServerPort}";
            // A status query IS a log event (record once), then rendered for visibility.
            McpLogger.Info(status);
            ext.WriteToOutputPane(status);
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Show Log", Group = McpMenuConstants.GROUP_MCP1, Order = 20)]
    sealed class ShowLogCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            var ext = TheExtension.Instance;
            if (ext == null) return;
            // Rendering only. MUST NOT call McpLogger with the displayed lines:
            // re-logging GetRecent() output feeds the log back into itself (duplicate
            // entries, unbounded file growth, eviction of legitimate history).
            var lines = McpLogger.GetRecent();
            ext.WriteToOutputPane($"--- last {lines.Length} log lines ({McpLogger.LogPath}) ---");
            foreach (var line in lines)
                ext.WriteToOutputPane(line);
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }
    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Clear Log", Group = McpMenuConstants.GROUP_MCP1, Order = 30)]
    sealed class ClearLogCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            McpLogger.ClearLog();
            McpLogger.Info("Log cleared");
            TheExtension.Instance?.ClearOutputPane();
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "S_ettings...", Group = McpMenuConstants.GROUP_MCP1, Order = 40)]
    sealed class SettingsCommand : MenuItemBase {
        readonly Lazy<IAppSettingsService> appSettingsService;

        [ImportingConstructor]
        SettingsCommand(Lazy<IAppSettingsService> appSettingsService) =>
            this.appSettingsService = appSettingsService;

        public override void Execute(IMenuItemContext context) {
            appSettingsService.Value.Show(McpAppSettingsPage.PAGE_GUID);
        }

        public override bool IsVisible(IMenuItemContext context) {
            return TheExtension.Instance != null;
        }
    }
}
