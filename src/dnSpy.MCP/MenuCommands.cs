using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;
using dnSpy.MCP.Mcp;
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
            var ext = DnSpyContext.Extension;
            ext?.StartServer();
        }

        public override bool IsVisible(IMenuItemContext context) {
            return DnSpyContext.Extension != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Stop", Group = McpMenuConstants.GROUP_MCP1, Order = 5)]
    sealed class StopMcpCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            DnSpyContext.Extension?.StopServer();
        }

        public override bool IsVisible(IMenuItemContext context) {
            return DnSpyContext.Extension != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Status", Group = McpMenuConstants.GROUP_MCP1, Order = 10)]
    sealed class StatusCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            var ext = DnSpyContext.Extension;
            if (ext == null) {
                McpLogger.Warn("Extension not loaded");
                return;
            }
            var running = ext.IsServerRunning ? "Running" : "Stopped";
            McpLogger.Info($"MCP Server: {running}, Port: {ext.ServerPort}");
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Show Log", Group = McpMenuConstants.GROUP_MCP1, Order = 20)]
    sealed class ShowLogCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            McpLogger.Info(McpLogger.GetRecentLogs());
        }

        public override bool IsVisible(IMenuItemContext context) {
            return DnSpyContext.Extension != null;
        }
    }

    [ExportMenuItem(OwnerGuid = McpMenuConstants.APP_MENU_MCP, Header = "_Clear Log", Group = McpMenuConstants.GROUP_MCP1, Order = 30)]
    sealed class ClearLogCommand : MenuItemBase {
        public override void Execute(IMenuItemContext context) {
            McpLogger.ClearLog();
            McpLogger.Info("Log cleared");
        }

        public override bool IsVisible(IMenuItemContext context) {
            return DnSpyContext.Extension != null;
        }
    }
}
